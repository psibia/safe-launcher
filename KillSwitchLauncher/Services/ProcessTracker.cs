using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using KillSwitchLauncher.Models;
using static KillSwitchLauncher.Services.NativeMethods;

namespace KillSwitchLauncher.Services;

public sealed record KillReport(IReadOnlyList<string> Killed, IReadOnlyList<string> Failed);

/// <summary>Работающее приложение лаунчера и его процессы в этом сеансе Windows (перед обновлением лаунчера).</summary>
/// <param name="Outside">Работает только мимо лаунчера (по ярлыку, автозагрузкой).</param>
public sealed record RunningApp(Guid AppId, string Name, IReadOnlyList<int> ProcessIds, bool Outside);

/// <summary>Чем закончился запуск.</summary>
/// <param name="ProcessId">PID запущенного процесса; 0 — Windows не вернула процесс.</param>
/// <param name="Contained">Процесс и все его будущие потомки в задании Windows: Kill-Switch закроет их целиком.</param>
/// <param name="Elevated">Запущено с правами администратора через запрос UAC.</param>
/// <param name="Adopted">Сколько уже работавших процессов этого приложения взято под контроль.</param>
/// <param name="OpenedBefore">
/// Приложение Microsoft Store уже было открыто не из лаунчера, и Windows показала его, а не запустила заново. Через подключение
/// лаунчера такое приложение под контроль не берётся: его соединения уже шли не через подключение.
/// </param>
public sealed record LaunchResult(int ProcessId, bool Contained, bool Elevated, int Adopted, bool OpenedBefore = false);

/// <summary>
/// Контроль процессов, запущенных лаунчером.
///
/// Почему не хватало запоминать PID: многие программы запускаются через «заглушку», которая сразу завершается.
/// Ярлык Discord — это «Update.exe --processStart Discord.exe»: Update.exe стартует Discord.exe и выходит,
/// и лаунчер терял приложение из виду. Process.Kill(entireProcessTree) тоже не помогал: он ищет потомков
/// по живым родителям, а у осиротевшего Discord.exe родителя уже нет.
///
/// Как устроено теперь:
///   1. Задание Windows (Job Object). Программа создаётся приостановленной, помещается в задание и только потом
///      запускается. Всё, что она порождает (обновления, перезапуски, помощники), автоматически попадает
///      в то же задание, даже если родитель уже завершился. При обрыве VPN задание закрывается целиком одним вызовом.
///   2. Семейство приложения (AppFamily). Процессы с тем же .exe из папки приложения, работающие вне задания
///      (запущенный раньше Discord, перезапуск обходным путём), подбираются в задание раз в секунду и при запуске.
///   3. Блокировка повторного запуска. После срабатывания Kill-Switch процессы закрытого приложения,
///      появившиеся снова, закрываются, пока VPN не вернётся или Kill-Switch не выключат.
///   4. Закрытие вместе с лаунчером. При включённом Kill-Switch у заданий стоит флаг «закрыть процессы
///      при закрытии задания»: если лаунчер аварийно завершится или его снимут в диспетчере задач,
///      Windows сама закроет приложения, а не оставит их без защиты.
///   5. Чей это процесс. Задания сообщают через порт уведомлений о каждом появившемся и завершившемся
///      процессе, поэтому по PID всегда можно узнать приложение (это нужно журналу DNS и ядру VPN).
///   6. Приложения через подключение лаунчера (SetPolicy, viaCore). Их трафик ведёт ядро по метке задания, поэтому
///      копии вне задания под контроль не берутся: ядро ведёт их, как выбрано в «Приложения, открытые не из лаунчера»,
///      а лаунчер только считает их (OutsideFor), чтобы предложить перезапуск под защитой. Выйти из задания такие процессы не могут, а при закрытии лаунчера
///      закрываются всегда.
///   7. Браузер, открытый по ссылке из приложения, попадает в его задание, но частью приложения не считается:
///      не идёт через подключение, не входит в число процессов и не закрывается вместе с приложением. Если этот браузер
///      сам добавлен в лаунчер и идёт через подключение лаунчера, для него это копия мимо лаунчера (считается и
///      закрывается по «Перезапустить под защитой», как любая другая).
///   8. Приложение убрали из лаунчера (Forget): его процессы работают дальше как обычные программы.
///   9. «Через лаунчер» (SetPolicy, adoptOutside). Копия приложения, открытая не из лаунчера, становится его запуском: если
///      приложение сейчас можно запустить (<see cref="LaunchCheck"/>, те же условия, что у запуска с плитки), она берётся
///      в задание, в том числе у приложения через подключение лаунчера, иначе сразу закрывается. Копии, открытые раньше
///      (до открытия лаунчера, в первые <see cref="AdoptGrace"/> после него или до выбора варианта), не закрываются, а ждут
///      условия запуска: при входе в Windows программы из автозагрузки открываются раньше, чем подключится VPN.
/// </summary>
public sealed class ProcessTracker : IAppProcesses, IDisposable
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BlockedNoticeInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Копия мимо лаунчера показывается, только если работает дольше этого. Новая копия многих программ (Chrome, Discord) при
    /// уже запущенном приложении передаёт ему окно и сразу выходит: она не должна выглядеть как запущенная мимо лаунчера.
    /// </summary>
    private static readonly TimeSpan OutsideMinAge = TimeSpan.FromSeconds(2);

    private readonly DateTime _createdUtc = DateTime.UtcNow;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, AppSlot> _slots = new();
    private readonly Timer _timer;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly uint _ownSession;
    private readonly char[] _pathBuffer = new char[32768];

    private bool _killOnClose;
    private bool _timerScheduled;
    private bool _disposed;

    // Порт уведомлений заданий: какие процессы в них появились и завершились. Свой поток и своя блокировка,
    // чтобы уведомления не ждали Kill-Switch и наоборот.
    private static readonly TimeSpan ExitedMemberKeep = TimeSpan.FromSeconds(10);
    private readonly SafeKernelHandle? _port;
    private readonly Thread? _portThread;
    private readonly ConcurrentDictionary<nuint, Guid> _portKeys = new();
    private readonly ConcurrentDictionary<Guid, nuint> _appKeys = new();
    private readonly object _membersGate = new();
    private readonly Dictionary<int, Member> _members = new();
    private readonly char[] _portPathBuffer = new char[32768];
    private long _nextPortKey;

    public ProcessTracker()
    {
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        ProcessIdToSessionId((uint)_ownPid, out _ownSession);

        var port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
        if (port.IsInvalid)
        {
            // Без порта всё работает как раньше, только журнал DNS будет узнавать процессы медленнее.
            port.Dispose();
            return;
        }

        _port = port;
        _portThread = new Thread(PortLoop) { IsBackground = true, Name = "Уведомления заданий" };
        _portThread.Start();
    }

    /// <summary>Изменилось число процессов под контролем. Может вызываться из любого потока.</summary>
    public event EventHandler? Changed;

    /// <summary>Закрытое Kill-Switch приложение запустилось снова без VPN и было закрыто. Аргумент — название. Любой поток.</summary>
    public event EventHandler<string>? RelaunchBlocked;

    /// <summary>
    /// В задании приложения появился процесс (не браузер, открытый по ссылке): сразу, без ожидания таймера.
    /// Аргументы — приложение и полный путь к exe. Поток уведомлений заданий.
    /// </summary>
    public event Action<Guid, string>? ProcessAdded;

    /// <summary>
    /// «Через лаунчер»: можно ли сейчас запустить приложение, как с плитки. null — можно, иначе причина отказа со строчной
    /// буквы. Вызывается в потоке наблюдения вне блокировки трекера: проверка обращается к монитору VPN и ядру.
    /// </summary>
    public Func<Guid, string?>? LaunchCheck { get; set; }

    /// <summary>
    /// «Через лаунчер»: копии, открытые в первые секунды после открытия лаунчера, не закрываются, даже если приложение пока
    /// запускать нельзя, а ждут, пока подключится VPN и запустится ядро. Сети у них в это время нет: фильтры Windows для
    /// программ таких приложений действуют всё время, пока открыт лаунчер.
    /// </summary>
    internal TimeSpan AdoptGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// «Через лаунчер»: приложение не работало, его копию открыли не из лаунчера, и она взята под контроль, как запуск
    /// с плитки. Аргументы — приложение и удалось ли поместить процесс в задание. Поток наблюдения.
    /// </summary>
    public event Action<Guid, bool>? OutsideLaunched;

    /// <summary>
    /// «Через лаунчер»: копию открыли не из лаунчера, когда приложение запускать нельзя, и она закрыта. Аргументы — название
    /// и причина. Поток наблюдения.
    /// </summary>
    public event Action<string, string>? OutsideRejected;

    private readonly ConcurrentDictionary<Guid, AppFamily> _families = new();

    /// <summary>Имя exe самого приложения: его процессы — не «браузер, открытый по ссылке», даже если приложение — браузер.</summary>
    private readonly ConcurrentDictionary<Guid, string> _exeNames = new();

    /// <summary>Сколько процессов сейчас под контролем.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _slots.Values.Sum(CountLocked);
        }
    }

    public int CountFor(Guid appId)
    {
        lock (_gate)
            return _slots.TryGetValue(appId, out var slot) ? CountLocked(slot) : 0;
    }

    /// <summary>Сколько копий приложения работает вне лаунчера (только у приложений через подключение лаунчера).</summary>
    public int OutsideFor(Guid appId)
    {
        lock (_gate)
            return _slots.TryGetValue(appId, out var slot) ? slot.Outside : 0;
    }

    /// <summary>
    /// Пересчитать копии приложения вне лаунчера прямо сейчас (перед запуском). У приложения не через ядро копия — только
    /// браузер в задании другого приложения (открыт им по ссылке): остальные его копии запуск возьмёт под контроль сам,
    /// а этот взять нельзя, и Kill-Switch за ним не следил бы.
    /// </summary>
    public int RefreshOutside(Guid appId)
    {
        int count;
        lock (_gate)
        {
            if (_disposed || !_slots.TryGetValue(appId, out var slot) || slot.Family is null)
                return 0;
            var hits = FindFamilyLocked(new[] { slot }, foreignBrowsers: _ => true);
            foreach (var hit in hits)
                hit.Handle.Dispose();
            if (!slot.ViaCore)
                return hits.Count(h => h.Foreign);
            if (hits.Count == slot.Outside)
                return hits.Count;
            slot.Outside = count = hits.Count;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return count;
    }

    /// <summary>
    /// Пути к exe работающих процессов приложения, без браузеров, открытых им по ссылке (они идут как обычно).
    /// Для фильтров «только через VPN». Любой поток.
    /// </summary>
    public IReadOnlyList<string> ImagePaths(Guid appId)
    {
        var paths = new List<string>();
        lock (_gate)
        {
            if (_disposed || !_slots.TryGetValue(appId, out var slot))
                return paths;

            void Add(SafeKernelHandle handle)
            {
                if (GetImagePath(handle) is { } path && !IsBrowserOf(appId, Path.GetFileName(path), path) &&
                    !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    paths.Add(path);
            }

            foreach (var job in slot.Jobs.Concat(slot.Retired))
            {
                foreach (var pid in job.ProcessIds() ?? new List<int>())
                {
                    using var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
                    if (!handle.IsInvalid)
                        Add(handle);
                }
            }

            foreach (var loose in slot.Loose)
            {
                if (loose.Handle is { IsInvalid: false } handle && loose.IsAlive)
                    Add(handle);
            }
        }

        return paths;
    }

    /// <summary>Приложения, у которых сейчас есть процессы под контролем.</summary>
    public IReadOnlyList<Guid> RunningApps()
    {
        lock (_gate)
            return _slots.Values.Where(s => CountLocked(s) > 0).Select(s => s.Id).ToList();
    }

    /// <summary>
    /// У приложения действует «Через лаунчер»: лаунчер узнаёт его копии и берёт их под контроль. У программы в общей папке
    /// (Program Files, System32, «Загрузки») и общего «движка» (java, cmd) копии не узнаются. Любой поток.
    /// </summary>
    public bool AdoptsCopies(Guid appId)
    {
        lock (_gate)
            return _slots.TryGetValue(appId, out var slot) && slot.AdoptOutside && slot.Family is not null;
    }

    /// <summary>
    /// Как вести приложение. viaCore — его трафик ведёт ядро лаунчера: копии вне задания не подбираются, а считаются
    /// (<see cref="OutsideFor"/>), выйти из задания нельзя, при закрытии лаунчера процессы закрываются всегда.
    /// killOnClose — закрывать приложение, если лаунчер завершится (в том числе аварийно).
    /// adoptOutside — «Через лаунчер»: копии вне задания берутся под контроль как запуск с плитки (<see cref="LaunchCheck"/>).
    /// </summary>
    public void SetPolicy(AppEntry entry, bool viaCore, bool killOnClose, bool adoptOutside = false)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var slot = GetSlot(entry);
            slot.Name = entry.Name;
            RememberExeName(entry);
            slot.ViaCore = viaCore;
            slot.KillOnClose = killOnClose || viaCore;
            // Семейство нужно сразу, чтобы узнавать копии мимо лаунчера (и браузер приложения, открытый по ссылке другим
            // приложением). У приложения Store — по программе из его пакета.
            if (slot.Family is null)
                SetFamilyLocked(slot, entry.IsPackaged
                    ? PackagedApps.ExecutablePath(entry.AppUserModelId) is { } packaged ? AppFamily.ForImage(packaged) : null
                    : AppFamily.For(entry));
            if (!viaCore)
                slot.Outside = 0;
            // Копии, которые уже работают, вариант не закрывает: они ждут условия запуска.
            if (adoptOutside && !slot.AdoptOutside)
                slot.CloseOutsideFrom = new DateTime(Math.Max(DateTime.UtcNow.Ticks, (_createdUtc + AdoptGrace).Ticks), DateTimeKind.Utc);
            slot.AdoptOutside = adoptOutside;
            foreach (var job in slot.Jobs)
                job.SetLimits(FlagsFor(slot));
            ScheduleLocked();
        }
    }

    /// <summary>«1 процесс», «3 процесса», «7 процессов».</summary>
    public static string ProcessesText(int count)
    {
        var mod100 = count % 100;
        var mod10 = count % 10;
        var word = mod100 is >= 11 and <= 14 ? "процессов"
            : mod10 == 1 ? "процесс"
            : mod10 is >= 2 and <= 4 ? "процесса"
            : "процессов";
        return $"{count} {word}";
    }

    // ---------------------------------------------------------------- запуск

    /// <summary>
    /// Запускает приложение внутри задания Windows. Бросает Win32Exception, если запустить не удалось
    /// (код 1223 — пользователь отменил запрос UAC).
    /// </summary>
    /// <param name="withoutProxy">
    /// Убрать из окружения программы переменные прокси (HTTP_PROXY и т. п.): иначе программы, которые их читают,
    /// отправили бы трафик прокси, а не через VPN приложения.
    /// </param>
    public LaunchResult Launch(AppEntry entry, bool withoutProxy = false)
    {
        ThrowIfDisposed();

        var workDir = ResolveWorkingDirectory(entry);
        var family = AppFamily.For(entry);
        uint flags;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var slot = GetSlot(entry);
            // До запуска: уведомление о первом процессе приходит сразу, и по семейству и имени exe видно, что это само
            // приложение, а не браузер, открытый по ссылке (иначе браузер, добавленный в лаунчер, не считался бы своим).
            RememberExeName(entry);
            if (family is not null)
                SetFamilyLocked(slot, family);
            // Пользователь запускает приложение сам (VPN уже проверен): подхват не должен закрыть новый процесс, пока он не в задании.
            slot.Blocked = false;
            flags = FlagsFor(slot);
        }

        var job = CreateJob(entry.Id, flags);

        int pid;
        bool contained;
        var elevated = false;
        LooseProcess? loose = null;

        // Запуск — вне блокировки: окно UAC может висеть сколько угодно, а Kill-Switch должен срабатывать сразу.
        try
        {
            if (TryCreateSuspended(entry, workDir, withoutProxy ? EnvironmentWithoutProxy() : null, out var info, out var error))
            {
                pid = (int)info.dwProcessId;
                var process = new SafeKernelHandle(info.hProcess, ownsHandle: true);

                // Поток программы стартует только после того, как процесс оказался в задании,
                // поэтому ни один его потомок не успеет появиться вне задания.
                contained = AssignProcessToJobObject(job.Handle, process);

                if (ResumeThread(info.hThread) == uint.MaxValue)
                {
                    var resumeError = Marshal.GetLastWin32Error();
                    CloseHandle(info.hThread);
                    TerminateProcess(process, 1);
                    process.Dispose();
                    throw new Win32Exception(resumeError);
                }

                CloseHandle(info.hThread);

                if (contained)
                    process.Dispose();
                else
                    loose = new LooseProcess(pid, process, null);
            }
            else if (error == ErrorElevationRequired)
            {
                // Программе нужны права администратора: CreateProcess так не умеет, запускаем через оболочку с запросом UAC.
                elevated = true;
                var process = Process.Start(new ProcessStartInfo(entry.ExePath)
                {
                    Arguments = entry.Arguments ?? string.Empty,
                    WorkingDirectory = workDir,
                    UseShellExecute = true
                });

                if (process is null)
                {
                    job.Dispose();
                    return new LaunchResult(0, false, true, 0);
                }

                pid = process.Id;
                contained = TryAssign(job, process.SafeHandle);
                if (contained)
                {
                    process.Dispose();
                }
                else
                {
                    var pin = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, (uint)pid);
                    loose = new LooseProcess(pid, pin.IsInvalid ? null : pin, process);
                }
            }
            else
            {
                throw new Win32Exception(error);
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }

        int adopted;
        lock (_gate)
        {
            if (_disposed)
            {
                job.Dispose();
                loose?.Dispose();
                return new LaunchResult(pid, contained, elevated, 0);
            }

            var slot = GetSlot(entry);
            slot.Name = entry.Name;
            SetFamilyLocked(slot, family);
            slot.Blocked = false;

            if (contained)
            {
                job.SetLimits(FlagsFor(slot)); // Kill-Switch могли переключить, пока шёл запуск
                slot.Jobs.Add(job);
            }
            else
            {
                job.Dispose(); // пустое задание
            }

            if (loose is not null)
                slot.Loose.Add(loose);

            // Программа могла быть уже запущена (например, Discord из автозагрузки): тогда новая копия
            // передаёт управление старой и выходит. Старую берём под контроль сразу. Через подключение лаунчера
            // так нельзя: её прежние соединения уже идут напрямую. Такие копии только считаются. С «Через лаунчер»
            // копии и раньше шли только через подключение (фильтры Windows, ядро как «Через VPN»), их можно взять.
            adopted = 0;
            foreach (var hit in FindFamilyLocked(new[] { slot }))
            {
                if (slot.ViaCore && !slot.AdoptOutside)
                {
                    hit.Handle.Dispose();
                    continue;
                }

                AdoptLocked(hit);
                adopted++;
            }

            slot.LastCount = CountLocked(slot);
            ScheduleLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new LaunchResult(pid, contained, elevated, adopted);
    }

    /// <summary>
    /// Запускает приложение Microsoft Store. Его процесс создаёт сама Windows, поэтому заранее поместить его
    /// в задание нельзя: лаунчер забирает процесс в задание сразу после запуска, а то, что приложение успело
    /// породить за это мгновение, подбирает по папке приложения. Бросает исключение, если запуск не удался.
    /// </summary>
    public LaunchResult LaunchPackaged(AppEntry entry)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            // Пользователь запускает приложение сам (VPN уже проверен): подхват не должен закрыть его, пока Windows его запускает.
            if (!_disposed)
                GetSlot(entry).Blocked = false;
        }

        // Вне блокировки: Windows может запускать приложение несколько секунд.
        var activated = DateTime.UtcNow;
        var pid = PackagedApps.Activate(entry.AppUserModelId!, entry.Arguments);
        if (pid <= 0)
            return new LaunchResult(0, false, false, 0);

        var canControl = true;
        var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize | ProcessSetQuota | ProcessTerminate, false, (uint)pid);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            canControl = false;
            handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, (uint)pid);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return new LaunchResult(pid, false, false, 0); // процесс уже завершился
            }
        }

        bool contained;
        var openedBefore = false;
        var adopted = 0;
        lock (_gate)
        {
            if (_disposed)
            {
                handle.Dispose();
                return new LaunchResult(pid, false, false, 0);
            }

            var slot = GetSlot(entry);
            slot.Name = entry.Name;
            slot.Blocked = false;
            if (GetImagePath(handle) is { } image)
                SetFamilyLocked(slot, AppFamily.ForImage(image));

            if (_slots.Values.SelectMany(s => s.Jobs).Any(j => j.Contains(handle)))
            {
                // Приложение уже было запущено из лаунчера и Windows просто вывела его на передний план.
                handle.Dispose();
                contained = true;
            }
            else if (slot.ViaCore && CreatedBefore(handle, activated))
            {
                // Windows показала копию, открытую раньше не из лаунчера. Её соединения уже идут как копии мимо лаунчера
                // (возможно, напрямую), и взятие под контроль их не перенаправит: она остаётся копией мимо лаунчера.
                handle.Dispose();
                openedBefore = true;
                contained = false;
            }
            else
            {
                contained = AdoptLocked(new FamilyHit(slot, pid, handle, canControl));
            }

            if (slot.Family is not null)
            {
                foreach (var hit in FindFamilyLocked(new[] { slot }))
                {
                    if (slot.ViaCore && !slot.AdoptOutside || openedBefore)
                    {
                        hit.Handle.Dispose();
                        continue;
                    }

                    AdoptLocked(hit);
                    adopted++;
                }
            }

            slot.LastCount = CountLocked(slot);
            ScheduleLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new LaunchResult(pid, contained, false, adopted, openedBefore);
    }

    private static string ResolveWorkingDirectory(AppEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.WorkingDirectory) && Directory.Exists(entry.WorkingDirectory)
            ? entry.WorkingDirectory
            : Path.GetDirectoryName(entry.ExePath) ?? string.Empty;

    /// <summary>Переменные, по которым программы находят прокси (регистр в Windows не важен).</summary>
    internal static readonly string[] ProxyVariables =
    {
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "FTP_PROXY", "SOCKS_PROXY", "SOCKS5_PROXY", "WS_PROXY", "WSS_PROXY", "GRPC_PROXY"
    };

    /// <summary>Окружение лаунчера без переменных прокси в виде блока для CreateProcess; null — их и не было.</summary>
    internal static string? EnvironmentWithoutProxy(IDictionary? variables = null)
    {
        variables ??= Environment.GetEnvironmentVariables();
        var keep = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var removed = false;
        foreach (DictionaryEntry item in variables)
        {
            var name = (string)item.Key;
            if (ProxyVariables.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                removed = true;
                continue;
            }

            keep[name] = item.Value as string ?? string.Empty;
        }

        if (!removed)
            return null;
        var block = new StringBuilder();
        foreach (var (name, value) in keep)
            block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }

    private static bool TryCreateSuspended(AppEntry entry, string workDir, string? environment, out ProcessInformation info, out int error)
    {
        var commandLine = new StringBuilder("\"" + entry.ExePath + "\"");
        if (!string.IsNullOrWhiteSpace(entry.Arguments))
            commandLine.Append(' ').Append(entry.Arguments);

        var startup = new StartupInfo { cb = (uint)Marshal.SizeOf<StartupInfo>() };
        var block = environment is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(environment);
        try
        {
            if (CreateProcessW(entry.ExePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateSuspended | CreateDefaultErrorMode | (environment is null ? 0 : CreateUnicodeEnvironment), block,
                    string.IsNullOrEmpty(workDir) ? null : workDir, ref startup, out info))
            {
                error = 0;
                return true;
            }

            error = Marshal.GetLastWin32Error();
            return false;
        }
        finally
        {
            if (block != IntPtr.Zero)
                Marshal.FreeHGlobal(block);
        }
    }

    // ---------------------------------------------------------------- Kill-Switch

    /// <summary>
    /// Закрывает все приложения под контролем вместе со всеми их процессами.
    /// blockRelaunch — после этого не давать им запуститься снова, пока не вызван StopBlocking (VPN вернулся).
    /// </summary>
    public KillReport KillAll(bool blockRelaunch)
    {
        var killed = new List<string>();
        var failed = new List<string>();

        lock (_gate)
        {
            if (_disposed)
                return new KillReport(killed, failed);

            KillLocked(_slots.Values.Where(s => s.IsArmed).ToList(), blockRelaunch, killed, failed);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new KillReport(killed, failed);
    }

    /// <summary>Закрывает выбранные приложения (Kill-Switch одного подключения или одного правила адаптеров).</summary>
    public KillReport KillApps(IReadOnlyCollection<Guid> appIds, bool blockRelaunch)
    {
        var killed = new List<string>();
        var failed = new List<string>();

        lock (_gate)
        {
            if (_disposed)
                return new KillReport(killed, failed);

            KillLocked(_slots.Values.Where(s => appIds.Contains(s.Id) && s.IsArmed).ToList(), blockRelaunch, killed, failed);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new KillReport(killed, failed);
    }

    /// <summary>
    /// Приложения лаунчера, которые сейчас работают у этого пользователя: под контролем лаунчера (без браузеров, открытых
    /// ими по ссылке) и запущенные мимо него. Обновление лаунчера ставится, только когда список пуст. Любой поток.
    /// </summary>
    public IReadOnlyList<RunningApp> RunningForUpdate()
    {
        var result = new List<RunningApp>();
        lock (_gate)
        {
            if (_disposed)
                return result;
            var outside = FindFamilyLocked(_slots.Values.ToList());
            try
            {
                foreach (var slot in _slots.Values)
                {
                    var own = slot.Jobs.Concat(slot.Retired)
                        .SelectMany(j => j.ProcessIds() ?? new List<int>())
                        .Where(pid => !IsBrowserPid(pid))
                        .Concat(slot.Loose.Where(p => p.IsAlive).Select(p => p.Pid))
                        .ToList();
                    var running = own.Count > 0 || CountLocked(slot) > 0;
                    var copies = outside.Where(h => h.Slot == slot).Select(h => h.Pid).ToList();
                    if (running || copies.Count > 0)
                        result.Add(new RunningApp(slot.Id, slot.Name, own.Concat(copies).Distinct().ToList(), Outside: !running));
                }
            }
            finally
            {
                foreach (var hit in outside)
                    hit.Handle.Dispose();
            }
        }

        return result;
    }

    /// <summary>
    /// Попросить окна процессов закрыться, как крестиком (WM_CLOSE): программа сохранит своё или спросит о несохранённом.
    /// Возвращает, скольким окнам отправлена просьба. Любой поток.
    /// </summary>
    public static int RequestClose(IReadOnlyCollection<int> processIds)
    {
        var pids = new HashSet<int>(processIds);
        var sent = 0;
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindow(hwnd, GwOwner) == IntPtr.Zero &&
                GetWindowThreadProcessId(hwnd, out var pid) != 0 && pids.Contains((int)pid) &&
                PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero))
                sent++;
            return true;
        }, IntPtr.Zero);
        return sent;
    }

    /// <summary>
    /// Принудительно закрыть приложения перед обновлением лаунчера: процессы под контролем и копии мимо лаунчера.
    /// Перезапуск не блокируется.
    /// </summary>
    public KillReport KillForUpdate(IReadOnlyCollection<Guid> appIds)
    {
        var killed = new List<string>();
        var failed = new List<string>();
        lock (_gate)
        {
            if (_disposed)
                return new KillReport(killed, failed);

            var slots = _slots.Values.Where(s => appIds.Contains(s.Id)).ToList();
            KillLocked(slots.Where(s => s.IsArmed).ToList(), blockRelaunch: false, killed, failed);
            foreach (var hit in FindFamilyLocked(slots))
            {
                using (hit.Handle)
                {
                    if (TerminateHit(hit) is { } error)
                        failed.Add($"{hit.Slot.Name} (PID {hit.Pid}): {error}");
                    else if (!killed.Contains(hit.Slot.Name))
                        killed.Add(hit.Slot.Name);
                }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new KillReport(killed, failed);
    }

    /// <summary>
    /// Закрывает копии приложения, запущенные мимо лаунчера («Перезапустить под защитой»).
    /// Возвращает, что закрыть не удалось.
    /// </summary>
    public IReadOnlyList<string> KillOutside(Guid appId)
    {
        var failed = new List<string>();
        lock (_gate)
        {
            if (_disposed || !_slots.TryGetValue(appId, out var slot) || slot.Family is null)
                return failed;

            foreach (var hit in FindFamilyLocked(new[] { slot }, foreignBrowsers: _ => true))
            {
                using (hit.Handle)
                {
                    // Не через ядро: копии вне заданий запуск возьмёт под контроль, закрывается только чужой браузер.
                    if ((slot.ViaCore || hit.Foreign) && TerminateHit(hit) is { } error)
                        failed.Add($"{slot.Name} (PID {hit.Pid}): {error}");
                }
            }

            if (slot.ViaCore)
                slot.Outside = failed.Count;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return failed;
    }

    /// <summary>
    /// Приостанавливает процессы приложений (браузеры не трогаются), пока перезапускается ядро: без ядра их новые
    /// соединения пошли бы напрямую. Dispose возобновляет. Приостановленные процессы не создают ни соединений, ни потомков.
    /// </summary>
    public IDisposable Freeze(IReadOnlyCollection<Guid> appIds)
    {
        var frozen = new List<SafeKernelHandle>();
        var failed = 0;
        lock (_gate)
        {
            if (!_disposed)
            {
                foreach (var slot in _slots.Values.Where(s => appIds.Contains(s.Id)))
                {
                    var pids = slot.Jobs.SelectMany(j => j.ProcessIds() ?? new List<int>())
                        .Where(pid => !IsBrowserPid(pid))
                        .Concat(slot.Loose.Select(p => p.Pid))
                        .Distinct();
                    foreach (var pid in pids)
                    {
                        var handle = OpenProcess(ProcessSuspendResume, false, (uint)pid);
                        if (!handle.IsInvalid && NtSuspendProcess(handle) == 0)
                        {
                            frozen.Add(handle);
                        }
                        else
                        {
                            failed++;
                            handle.Dispose();
                        }
                    }
                }
            }
        }

        TechLog.Info(SafeLauncher.Logging.LogAreas.App, SafeLauncher.Logging.LogCodes.CoreFreeze,
            $"Приостановлено процессов: {frozen.Count}" + (failed > 0 ? $", не удалось приостановить: {failed}" : string.Empty) + " (перезапуск ядра)");
        return new Frozen(frozen);
    }

    private sealed class Frozen : IDisposable
    {
        private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
        private List<SafeKernelHandle>? _handles;

        public Frozen(List<SafeKernelHandle> handles) => _handles = handles;

        public void Dispose()
        {
            var handles = Interlocked.Exchange(ref _handles, null);
            if (handles is null)
                return;
            foreach (var handle in handles)
            {
                NtResumeProcess(handle);
                handle.Dispose();
            }
            TechLog.Info(SafeLauncher.Logging.LogAreas.App, SafeLauncher.Logging.LogCodes.CoreFreeze,
                $"Процессы продолжили работу: {handles.Count}, были приостановлены {_watch.ElapsedMilliseconds} мс");
        }
    }

    /// <summary>
    /// Закрывает одно приложение со всеми его процессами (пункт меню «Завершить все процессы»).
    /// Так же, как Kill-Switch, но перезапуск не блокируется: приложение можно сразу запустить снова.
    /// </summary>
    public KillReport Kill(Guid appId)
    {
        var killed = new List<string>();
        var failed = new List<string>();

        lock (_gate)
        {
            if (_disposed || !_slots.TryGetValue(appId, out var slot) || !slot.IsArmed)
                return new KillReport(killed, failed);

            KillLocked(new List<AppSlot> { slot }, blockRelaunch: false, killed, failed);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new KillReport(killed, failed);
    }

    private void KillLocked(List<AppSlot> armed, bool blockRelaunch, List<string> killed, List<string> failed)
    {
        var counts = armed.ToDictionary(s => s, _ => 0);

        foreach (var slot in armed)
        {
            foreach (var job in slot.Jobs)
            {
                var pids = job.ProcessIds();
                var browsers = pids?.Where(IsBrowserPid).ToList() ?? new List<int>();
                if (browsers.Count == 0)
                {
                    counts[slot] += Math.Max(job.ActiveProcesses, 0);
                    job.Terminate();
                    slot.Retired.Add(job);
                    continue;
                }

                // В задании браузер, открытый по ссылке: закрываем всё, кроме него.
                counts[slot] += TerminateExceptBrowsers(job, pids!, slot.Name, failed);
            }

            slot.Jobs.Clear();

            foreach (var process in slot.Loose.ToList())
            {
                var error = process.Kill();
                if (error is null)
                {
                    counts[slot]++;
                    slot.Loose.Remove(process);
                    process.Dispose();
                }
                else
                {
                    failed.Add($"{slot.Name} (PID {process.Pid}): {error}");
                }
            }
        }

        // Процессы приложения вне задания, которые ещё не успели подобрать.
        try
        {
            foreach (var hit in FindFamilyLocked(armed.Where(s => s.Family is not null).ToList(), foreignBrowsers: _ => true))
            {
                using (hit.Handle)
                {
                    var error = TerminateHit(hit);
                    if (error is null)
                        counts[hit.Slot]++;
                    else
                        failed.Add($"{hit.Slot.Name} (PID {hit.Pid}): {error}");
                }
            }
        }
        catch (Exception ex)
        {
            // Задания уже закрыты; обход остальных процессов повторит наблюдение.
            Debug.WriteLine(ex);
        }

        foreach (var slot in armed)
        {
            var n = counts[slot];
            if (n > 0)
                killed.Add(n == 1 ? slot.Name : $"{slot.Name} ({ProcessesText(n)})");

            if (blockRelaunch)
            {
                slot.Blocked = true;
                slot.LastBlockedNotice = DateTime.UtcNow;
            }

            slot.LastCount = CountLocked(slot);
        }

        ScheduleLocked();
    }

    /// <summary>
    /// VPN снова есть (или Kill-Switch выключен): закрытые приложения можно запускать.
    /// С appId — только для одного приложения (например, его убрали из лаунчера).
    /// </summary>
    public void StopBlocking(Guid? appId = null)
    {
        lock (_gate)
        {
            foreach (var (id, slot) in _slots)
            {
                if (appId is null || id == appId)
                    slot.Blocked = false;
            }
        }
    }

    /// <summary>
    /// Приложение убрали из лаунчера: его процессы работают дальше как обычные программы. Задания отпускаются (процессы не
    /// закроются ни вместе с лаунчером, ни по Kill-Switch), лаунчер их больше не считает, не ищет его копии и не отвечает
    /// ядру, что это процессы приложения. Задания, закрытые Kill-Switch, закрываются до конца.
    /// </summary>
    public void Forget(Guid appId)
    {
        lock (_gate)
        {
            if (_disposed || !_slots.Remove(appId, out var slot))
                return;

            // Отпущенные задания продолжают присылать уведомления о процессах: больше они ничьи. Ключ убираем до того, как
            // отпустить задания, а участников — после (OnPortMessage проверяет ключ под _membersGate).
            if (_appKeys.TryRemove(appId, out var key))
                _portKeys.TryRemove(key, out _);
            foreach (var job in slot.Jobs)
                job.Release();
            foreach (var job in slot.Retired)
                job.Dispose();
            foreach (var process in slot.Loose)
                process.Dispose();
            slot.Jobs.Clear();
            slot.Retired.Clear();
            slot.Loose.Clear();
            SetFamilyLocked(slot, null);
            _exeNames.TryRemove(appId, out _);
        }

        lock (_membersGate)
        {
            foreach (var pid in _members.Where(m => m.Value.AppId == appId).Select(m => m.Key).ToList())
                _members.Remove(pid);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Закрывать ли все приложения, если лаунчер завершится (в том числе аварийно). У отдельных приложений
    /// это задаёт <see cref="SetPolicy"/>.
    /// </summary>
    public void SetKillOnClose(bool enabled)
    {
        lock (_gate)
        {
            _killOnClose = enabled;
            foreach (var slot in _slots.Values)
            {
                foreach (var job in slot.Jobs)
                    job.SetLimits(FlagsFor(slot));
            }
        }
    }

    private uint FlagsFor(AppSlot slot) =>
        // BREAKAWAY_OK: процесс может выйти из задания, только если сам явно об этом попросит
        // (так делают некоторые программы при перезапуске после обновления). Такие процессы с тем же .exe
        // подбирает обход семейства. Через подключение лаунчера выход запрещён: вне задания трафик шёл бы напрямую.
        (slot.ViaCore ? 0 : JobObjectLimitBreakawayOk) |
        (_killOnClose || slot.KillOnClose ? JobObjectLimitKillOnJobClose : 0);

    /// <summary>
    /// Закрывает процессы задания, кроме браузеров. Пока они закрываются, новые процессы в задании запрещены.
    /// Потом задание отпускается: браузер работает дальше и не закроется вместе с лаунчером. Возвращает, сколько закрыто.
    /// </summary>
    private int TerminateExceptBrowsers(Job job, List<int> pids, string name, List<string> failed)
    {
        job.SetLimits(JobObjectLimitActiveProcess, activeProcessLimit: (uint)Math.Max(pids.Count, 1));
        var count = 0;
        foreach (var pid in pids.Where(p => !IsBrowserPid(p)))
        {
            using var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle.IsInvalid)
                continue; // уже завершился
            if (TerminateProcess(handle, 1) || !IsAlive(handle))
                count++;
            else
                failed.Add($"{name} (PID {pid}): {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        job.SetLimits(0);
        job.Dispose();
        return count;
    }

    /// <summary>Сколько процессов задания — не браузеры; -1, если узнать не удалось.</summary>
    private int AppProcesses(Job job)
    {
        var pids = job.ProcessIds();
        return pids is null ? job.ActiveProcesses : pids.Count(p => !IsBrowserPid(p));
    }

    // ---------------------------------------------------------------- наблюдение

    private void ScheduleLocked()
    {
        if (_disposed || _timerScheduled)
            return;

        if (_slots.Values.Any(s => s.IsArmed || s.Retired.Count > 0 || (s.Blocked || s.ViaCore || s.AdoptOutside) && s.Family is not null))
        {
            _timerScheduled = true;
            _timer.Change(WatchInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer()
    {
        var changed = false;
        var sweep = new Sweep();

        try
        {
            // «Через лаунчер»: условие запуска проверяется до блокировки трекера. Нет ответа — копии этого приложения
            // ждут следующего прохода.
            List<Guid> adopting;
            lock (_gate)
            {
                if (_disposed)
                    return;
                adopting = _slots.Values.Where(s => s.AdoptOutside && s.Family is not null && !s.Blocked).Select(s => s.Id).ToList();
            }

            if (LaunchCheck is { } check)
            {
                foreach (var id in adopting)
                {
                    try
                    {
                        sweep.Verdicts[id] = check(id);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                }
            }

            lock (_gate)
            {
                _timerScheduled = false;
                if (_disposed)
                    return;

                try
                {
                    changed = SweepLocked(sweep);
                }
                finally
                {
                    ScheduleLocked();
                }
            }

            if (changed)
                Changed?.Invoke(this, EventArgs.Empty);
            foreach (var name in sweep.Blocked)
                RelaunchBlocked?.Invoke(this, name);
            foreach (var (appId, contained) in sweep.Launched)
                OutsideLaunched?.Invoke(appId, contained);
            foreach (var (name, reason) in sweep.Rejected)
                OutsideRejected?.Invoke(name, reason);
        }
        catch (Exception ex)
        {
            // Исключение в потоке таймера уронило бы лаунчер, а вместе с ним и защиту. Наблюдение должно продолжаться.
            Debug.WriteLine(ex);
            lock (_gate)
            {
                _timerScheduled = false;
                ScheduleLocked();
            }
        }
    }

    /// <summary>Один проход наблюдения: что проверено до него и что сообщить после.</summary>
    private sealed class Sweep
    {
        /// <summary>«Через лаунчер»: приложение → null (запускать можно) или причина отказа.</summary>
        public readonly Dictionary<Guid, string?> Verdicts = new();

        /// <summary>Закрытые Kill-Switch приложения, которые запустились снова.</summary>
        public readonly List<string> Blocked = new();

        public readonly List<(Guid AppId, bool Contained)> Launched = new();

        public readonly List<(string Name, string Reason)> Rejected = new();
    }

    /// <summary>Один проход наблюдения. Возвращает true, если изменилось число процессов.</summary>
    private bool SweepLocked(Sweep sweep)
    {
        foreach (var slot in _slots.Values)
        {
            // Опустевшие задания больше не нужны. Если в задании остался только браузер, открытый по ссылке,
            // приложение завершилось: задание отпускаем, не закрывая браузер.
            slot.Jobs.RemoveAll(job =>
            {
                if (AppProcesses(job) != 0) return false;
                job.Release();
                return true;
            });

            // Закрытые Kill-Switch задания держим, пока не завершится последний процесс; на всякий случай добиваем.
            slot.Retired.RemoveAll(job =>
            {
                var pids = job.ProcessIds();
                if (AppProcesses(job) != 0)
                {
                    if (pids is not null && pids.Any(IsBrowserPid))
                    {
                        TerminateExceptBrowsers(job, pids, slot.Name, new List<string>()); // задание отпущено
                        return true;
                    }

                    job.Terminate();
                    return false;
                }

                job.Release();
                return true;
            });

            slot.Loose.RemoveAll(process =>
            {
                if (process.IsAlive) return false;
                process.Dispose();
                return true;
            });
        }

        var watched = _slots.Values.Where(s => s.Family is not null && (s.IsArmed || s.Blocked || s.ViaCore || s.AdoptOutside)).ToList();
        // Приложения, которые сейчас не работают: первая взятая копия такого приложения — его запуск.
        var idle = watched.Where(s => s.AdoptOutside && CountLocked(s) == 0).ToHashSet();
        var outside = new Dictionary<AppSlot, int>();
        // Браузер в задании другого приложения (открыт по ссылке) — копия мимо лаунчера только для своего приложения через
        // ядро и закрывается, пока его приложение закрыто Kill-Switch. Под контроль его не берём: он уже в чужом задании.
        foreach (var hit in FindFamilyLocked(watched, foreignBrowsers: s => s.ViaCore || s.Blocked))
        {
            if (hit.Slot.Blocked)
            {
                // Приложение, закрытое Kill-Switch, запустилось снова, а VPN всё ещё нет.
                using (hit.Handle)
                {
                    if (TerminateHit(hit) is null && DateTime.UtcNow - hit.Slot.LastBlockedNotice > BlockedNoticeInterval)
                    {
                        hit.Slot.LastBlockedNotice = DateTime.UtcNow;
                        if (!sweep.Blocked.Contains(hit.Slot.Name))
                            sweep.Blocked.Add(hit.Slot.Name);
                    }
                }
            }
            else if (hit.Slot.AdoptOutside && !hit.Foreign && sweep.Verdicts.TryGetValue(hit.Slot.Id, out var refusal) &&
                     (refusal is null || hit.CanControl && !CreatedBefore(hit.Handle, hit.Slot.CloseOutsideFrom)))
            {
                if (refusal is null)
                {
                    // «Через лаунчер», запускать можно: копия — запуск приложения.
                    var contained = AdoptLocked(hit);
                    TechLog.Info(SafeLauncher.Logging.LogAreas.App, SafeLauncher.Logging.LogCodes.AppAdopt,
                        $"«{hit.Slot.Name}»: процесс PID {hit.Pid}, открытый не из лаунчера, {(contained ? "взят под контроль" : "взят под наблюдение вне задания")} (через лаунчер)");
                    if (idle.Remove(hit.Slot))
                        sweep.Launched.Add((hit.Slot.Id, contained));
                }
                else
                {
                    // «Через лаунчер», запускать нельзя: копия закрывается, как не начался бы запуск с плитки.
                    using (hit.Handle)
                    {
                        var error = TerminateHit(hit);
                        TechLog.Info(SafeLauncher.Logging.LogAreas.App, SafeLauncher.Logging.LogCodes.AppReject,
                            $"«{hit.Slot.Name}»: процесс PID {hit.Pid}, открытый не из лаунчера, {(error is null ? "закрыт" : "закрыть не удалось (" + error + ")")}: {refusal}");
                        if (error is null && DateTime.UtcNow - hit.Slot.LastBlockedNotice > BlockedNoticeInterval &&
                            !sweep.Rejected.Any(r => r.Name == hit.Slot.Name))
                        {
                            hit.Slot.LastBlockedNotice = DateTime.UtcNow;
                            sweep.Rejected.Add((hit.Slot.Name, refusal));
                        }
                    }
                }
            }
            else if (hit.Slot.ViaCore)
            {
                // Копия мимо лаунчера: её соединения ядро ведёт по «Приложения, открытые не из лаунчера», под контроль не
                // берём, только считаем.
                using (hit.Handle)
                {
                    if (OlderThan(hit.Handle, OutsideMinAge))
                        outside[hit.Slot] = outside.GetValueOrDefault(hit.Slot) + 1;
                }
            }
            else if (hit.Foreign || !hit.Slot.IsArmed)
            {
                // Браузер в чужом задании или копия «Через лаунчер», которая ждёт условия запуска (сети у неё нет).
                hit.Handle.Dispose();
            }
            else
            {
                var adopted = AdoptLocked(hit);
                TechLog.Info(SafeLauncher.Logging.LogAreas.App, SafeLauncher.Logging.LogCodes.AppAdopt,
                    $"«{hit.Slot.Name}»: процесс PID {hit.Pid}, запущенный мимо лаунчера, {(adopted ? "взят под контроль" : "взять под контроль не удалось")}");
            }
        }

        var changed = false;
        foreach (var slot in _slots.Values)
        {
            var copies = outside.GetValueOrDefault(slot);
            if (copies != slot.Outside)
            {
                slot.Outside = copies;
                changed = true;
            }

            var count = CountLocked(slot);
            if (count == slot.LastCount) continue;
            TechLog.Debug(SafeLauncher.Logging.LogAreas.App, SafeLauncher.Logging.LogCodes.AppProcesses,
                $"«{slot.Name}»: процессов {slot.LastCount} → {count}" + (copies > 0 ? $", копий мимо лаунчера {copies}" : string.Empty));
            slot.LastCount = count;
            changed = true;
        }

        return changed;
    }

    /// <param name="Foreign">Браузер в задании другого приложения лаунчера (открыт им по ссылке): под контроль не берётся.</param>
    private readonly record struct FamilyHit(AppSlot Slot, int Pid, SafeKernelHandle Handle, bool CanControl, bool Foreign = false);

    /// <summary>
    /// Ищет процессы приложений вне заданий лаунчера. Возвращённые дескрипторы принадлежат вызывающему.
    /// </summary>
    /// <param name="foreignBrowsers">
    /// Для каких приложений находить и браузер, открытый по ссылке другим приложением лаунчера: он в задании того
    /// приложения, но частью его не считается, а для своего приложения это копия мимо лаунчера (<see cref="FamilyHit.Foreign"/>).
    /// </param>
    private List<FamilyHit> FindFamilyLocked(IReadOnlyList<AppSlot> slots, Func<AppSlot, bool>? foreignBrowsers = null)
    {
        var hits = new List<FamilyHit>();
        slots = slots.Where(s => s.Family is not null).ToList(); // у программ из папки Windows и Store семейства нет
        if (slots.Count == 0)
            return hits;

        var ourJobs = _slots.Values.SelectMany(s => s.Jobs.Concat(s.Retired).Select(j => (Slot: s, Job: j))).ToList();
        var loosePids = new HashSet<int>(_slots.Values.SelectMany(s => s.Loose).Select(p => p.Pid));

        foreach (var (pid, name) in SnapshotProcesses())
        {
            if (pid == _ownPid || pid <= 4 || loosePids.Contains(pid))
                continue;

            var candidates = slots.Where(s => s.Family!.HasName(name)).ToList();
            if (candidates.Count == 0)
                continue;

            // Только процессы этого пользователя в этом сеансе: на терминальном сервере чужие не трогаем.
            if (!ProcessIdToSessionId((uint)pid, out var session) || session != _ownSession)
                continue;

            var canControl = true;
            var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize | ProcessSetQuota | ProcessTerminate, false, (uint)pid);
            if (handle.IsInvalid)
            {
                // Например, программа работает с правами администратора: управлять нельзя, но узнать можно.
                handle.Dispose();
                canControl = false;
                handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, (uint)pid);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    continue;
                }
            }

            var path = GetImagePath(handle);
            var owner = path is null ? null : candidates.FirstOrDefault(s => s.Family!.ContainsPath(path));
            var containers = owner is null ? null : ourJobs.Where(j => j.Job.Contains(handle)).Select(j => j.Slot).Distinct().ToList();
            var foreign = containers is { Count: > 0 };
            if (owner is null || !IsAlive(handle) ||
                foreign && (foreignBrowsers?.Invoke(owner) != true || containers!.Any(c => c == owner || !IsBrowserOf(c.Id, name, path))))
            {
                handle.Dispose();
                continue;
            }

            hits.Add(new FamilyHit(owner, pid, handle, canControl, foreign));
        }

        return hits;
    }

    /// <summary>
    /// Помещает найденный процесс в задание приложения. Дескриптор переходит во владение метода.
    /// true — процесс в задании; false — под отдельным наблюдением вне задания.
    /// </summary>
    private bool AdoptLocked(FamilyHit hit)
    {
        var slot = hit.Slot;

        if (hit.CanControl)
        {
            if (slot.Jobs.Count > 0 && TryAssign(slot.Jobs[^1], hit.Handle))
            {
                hit.Handle.Dispose();
                return true;
            }

            // Процесс уже в чужом задании: Windows разрешает вложить его только в пустое новое задание.
            var fresh = TryCreateJob(slot.Id, FlagsFor(slot));
            if (fresh is not null)
            {
                if (TryAssign(fresh, hit.Handle))
                {
                    slot.Jobs.Add(fresh);
                    hit.Handle.Dispose();
                    return true;
                }

                fresh.Dispose();
            }
        }

        // В задание не поместить: следим за процессом отдельно. Если закрыть его не удастся,
        // пользователь увидит это в отчёте Kill-Switch.
        slot.Loose.Add(new LooseProcess(hit.Pid, hit.Handle, null));
        return false;
    }

    /// <summary>Закрывает найденный процесс. null — успешно (или он уже завершился), иначе текст ошибки.</summary>
    private static string? TerminateHit(FamilyHit hit)
    {
        if (!hit.CanControl)
            return IsAlive(hit.Handle) ? new Win32Exception(ErrorAccessDenied).Message : null;

        if (TerminateProcess(hit.Handle, 1))
            return null;

        var error = Marshal.GetLastWin32Error();
        return IsAlive(hit.Handle) ? new Win32Exception(error).Message : null;
    }

    private static List<(int Pid, string Name)> SnapshotProcesses()
    {
        var list = new List<(int, string)>(512);
        using var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
            return list;

        var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
        if (!Process32FirstW(snapshot, ref entry))
            return list;

        do
        {
            list.Add(((int)entry.th32ProcessID, entry.szExeFile ?? string.Empty));
        } while (Process32NextW(snapshot, ref entry));

        return list;
    }

    private string? GetImagePath(SafeKernelHandle process)
    {
        var size = (uint)_pathBuffer.Length;
        return QueryFullProcessImageNameW(process, 0, _pathBuffer, ref size) ? new string(_pathBuffer, 0, (int)size) : null;
    }

    private static bool IsAlive(SafeHandle process) =>
        GetExitCodeProcess(process, out var code) && code == StillActive;

    /// <summary>Процесс создан раньше <paramref name="moment"/> (время не узнать — считается, что нет).</summary>
    private static bool CreatedBefore(SafeHandle process, DateTime moment) =>
        GetProcessTimes(process, out var created, out _, out _, out _) && DateTime.FromFileTimeUtc(created) < moment;

    /// <summary>Процесс работает не меньше <paramref name="age"/> (время не узнать — считается, что да).</summary>
    private static bool OlderThan(SafeHandle process, TimeSpan age) =>
        !GetProcessTimes(process, out var created, out _, out _, out _) || DateTime.UtcNow - DateTime.FromFileTimeUtc(created) >= age;

    // ---------------------------------------------------------------- чей процесс

    /// <summary>
    /// Какому приложению лаунчера принадлежит процесс. Завершившиеся процессы помнятся ещё несколько секунд:
    /// события о них (например, DNS-запросы) могут прийти чуть позже самого завершения. Любой поток.
    /// </summary>
    public bool TryGetOwner(int pid, out Guid appId, out string processName)
    {
        lock (_membersGate)
        {
            if (_members.TryGetValue(pid, out var member) &&
                (member.ExitedUtc is not { } exited || DateTime.UtcNow - exited < ExitedMemberKeep))
            {
                appId = member.AppId;
                processName = member.Name;
                return true;
            }
        }

        // Уведомление могло не прийти (порт недоступен) или процесс вне задания: проверяем напрямую.
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        lock (_gate)
        {
            if (!_disposed)
            {
                foreach (var slot in _slots.Values)
                {
                    var owned = slot.Loose.Any(p => p.Pid == pid) ||
                                (!handle.IsInvalid && slot.Jobs.Concat(slot.Retired).Any(j => j.Contains(handle)));
                    if (!owned) continue;

                    appId = slot.Id;
                    var image = handle.IsInvalid ? null : GetImagePath(handle);
                    processName = image is not null ? Path.GetFileName(image) : $"PID {pid}";
                    // Процесс вне задания (запущен с правами администратора) не запоминаем: о его завершении уведомления не будет,
                    // а когда лаунчер его отпустит, Windows может отдать этот PID другой программе.
                    if (slot.Loose.Any(p => p.Pid == pid))
                        return true;
                    var browser = IsBrowserOf(slot.Id, processName, image);
                    lock (_membersGate)
                        _members[pid] = new Member(appId, processName, null, browser);
                    return true;
                }
            }
        }

        appId = Guid.Empty;
        processName = string.Empty;
        return false;
    }

    /// <summary>Подписать задание на порт уведомлений: ключ сообщения указывает на приложение.</summary>
    private void AttachPort(Job job, Guid appId)
    {
        if (_port is null)
            return;

        var key = _appKeys.GetOrAdd(appId, _ => (nuint)Interlocked.Increment(ref _nextPortKey));
        _portKeys[key] = appId;

        var info = new JobObjectAssociateCompletionPort
        {
            CompletionKey = (IntPtr)key,
            CompletionPort = _port.DangerousGetHandle()
        };
        SetInformationJobObject(job.Handle, JobInfoAssociateCompletionPort, ref info,
            (uint)Marshal.SizeOf<JobObjectAssociateCompletionPort>());
    }

    private void PortLoop()
    {
        while (true)
        {
            if (!GetQueuedCompletionStatus(_port!, out var message, out var key, out var data, uint.MaxValue))
            {
                if (Marshal.GetLastWin32Error() == ErrorAbandonedWait0) // порт закрыт
                    return;
                continue;
            }

            if (key == UIntPtr.Zero)
                return; // сигнал остановки из Dispose

            try
            {
                OnPortMessage(message, (nuint)key, (int)data);
            }
            catch (Exception ex)
            {
                // Исключение в этом потоке уронило бы лаунчер вместе с защитой.
                Debug.WriteLine(ex);
            }
        }
    }

    private void OnPortMessage(uint message, nuint key, int pid)
    {
        if (!_portKeys.TryGetValue(key, out var appId))
            return;

        switch (message)
        {
            case JobMsgNewProcess:
            {
                var name = $"PID {pid}";
                string? fullPath = null;
                using (var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid))
                {
                    var size = (uint)_portPathBuffer.Length;
                    if (!handle.IsInvalid && QueryFullProcessImageNameW(handle, 0, _portPathBuffer, ref size))
                    {
                        fullPath = new string(_portPathBuffer, 0, (int)size);
                        name = Path.GetFileName(fullPath);
                    }
                }

                var browser = IsBrowserOf(appId, name, fullPath);
                lock (_membersGate)
                {
                    if (!_portKeys.ContainsKey(key))
                        return; // приложение убрали из лаунчера, пока разбирали сообщение (Forget)
                    _members[pid] = new Member(appId, name, null, browser);
                    PurgeMembersLocked();
                }

                if (!browser && fullPath is not null)
                    ProcessAdded?.Invoke(appId, fullPath);
                break;
            }
            case JobMsgExitProcess or JobMsgAbnormalExitProcess:
                lock (_membersGate)
                {
                    if (_members.TryGetValue(pid, out var member) && member.AppId == appId)
                        _members[pid] = member with { ExitedUtc = DateTime.UtcNow };
                }

                break;
        }
    }

    private void PurgeMembersLocked()
    {
        var now = DateTime.UtcNow;
        foreach (var pid in _members.Where(m => m.Value.ExitedUtc is { } exited && now - exited > ExitedMemberKeep)
                     .Select(m => m.Key).ToList())
            _members.Remove(pid);
    }

    private readonly record struct Member(Guid AppId, string Name, DateTime? ExitedUtc, bool IsBrowser = false);

    /// <summary>Процесс — браузер, открытый по ссылке из приложения лаунчера. Любой поток.</summary>
    public bool IsBrowserPid(int pid)
    {
        lock (_membersGate)
        {
            if (_members.TryGetValue(pid, out var member))
                return member.IsBrowser;
        }

        return false;
    }

    /// <summary>
    /// Чей процесс для ядра VPN: как <see cref="TryGetOwner"/>, но браузер, открытый по ссылке, ничей — он идёт напрямую.
    /// Любой поток.
    /// </summary>
    public bool TryGetRoutedOwner(int pid, out Guid appId)
    {
        if (!TryGetOwner(pid, out appId, out _) || IsBrowserPid(pid))
        {
            appId = Guid.Empty;
            return false;
        }

        return true;
    }

    // ---------------------------------------------------------------- состояние

    private AppSlot GetSlot(AppEntry entry)
    {
        if (!_slots.TryGetValue(entry.Id, out var slot))
            _slots[entry.Id] = slot = new AppSlot(entry.Id);
        return slot;
    }

    /// <summary>Процессы приложения: без браузеров, открытых по ссылке.</summary>
    private int CountLocked(AppSlot slot) =>
        slot.Jobs.Sum(j => Math.Max(AppProcesses(j), 0)) + slot.Loose.Count(p => p.IsAlive);

    private void SetFamilyLocked(AppSlot slot, AppFamily? family)
    {
        slot.Family = family;
        if (family is null)
            _families.TryRemove(slot.Id, out _);
        else
            _families[slot.Id] = family;
    }

    /// <summary>
    /// Браузер, открытый приложением по ссылке (а не само приложение, даже если это браузер). Браузер из папки самого
    /// приложения — его часть: так Opera запускает launcher.exe → opera.exe, а ярлык сайта Chrome — chrome_proxy.exe → chrome.exe.
    /// </summary>
    private bool IsBrowserOf(Guid appId, string exeName, string? imagePath = null) =>
        Browsers.IsBrowser(exeName) &&
        !(_families.TryGetValue(appId, out var family) && (family.HasName(exeName) || imagePath is not null && family.ContainsPath(imagePath))) &&
        !(_exeNames.TryGetValue(appId, out var own) && string.Equals(own, exeName, StringComparison.OrdinalIgnoreCase));

    private void RememberExeName(AppEntry entry)
    {
        if (!entry.IsPackaged && !string.IsNullOrWhiteSpace(entry.ExePath))
            _exeNames[entry.Id] = Path.GetFileName(entry.ExePath);
    }

    private Job CreateJob(Guid appId, uint flags) =>
        TryCreateJob(appId, flags) ?? throw new Win32Exception(Marshal.GetLastWin32Error());

    private Job? TryCreateJob(Guid appId, uint flags)
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var job = new Job(handle);
        job.SetLimits(flags);
        AttachPort(job, appId);
        return job;
    }

    private static bool TryAssign(Job job, SafeHandle process)
    {
        try
        {
            return AssignProcessToJobObject(job.Handle, process);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
            ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();

            // Закрываем задания. Если Kill-Switch включён, Windows закроет и их процессы (флаг KILL_ON_JOB_CLOSE).
            foreach (var slot in _slots.Values)
            {
                foreach (var job in slot.Jobs.Concat(slot.Retired))
                    job.Dispose();
                foreach (var process in slot.Loose)
                    process.Dispose();
            }

            _slots.Clear();
        }

        if (_port is not null)
        {
            // Поток ждёт порт внутри GetQueuedCompletionStatus: будим его особым сообщением, потом закрываем порт.
            PostQueuedCompletionStatus(_port, 0, UIntPtr.Zero, IntPtr.Zero);
            _portThread?.Join(TimeSpan.FromSeconds(2));
            _port.Dispose();
        }
    }

    // ---------------------------------------------------------------- типы

    /// <summary>Всё, что лаунчер знает об одном приложении.</summary>
    private sealed class AppSlot
    {
        public AppSlot(Guid id) => Id = id;

        public readonly Guid Id;
        public string Name = string.Empty;
        public AppFamily? Family;

        /// <summary>Действующие задания: по одному на запуск, плюс задания для подобранных процессов.</summary>
        public readonly List<Job> Jobs = new();

        /// <summary>Задания, закрытые Kill-Switch: держим, пока не завершится последний процесс.</summary>
        public readonly List<Job> Retired = new();

        /// <summary>Процессы вне задания (например, с правами администратора).</summary>
        public readonly List<LooseProcess> Loose = new();

        /// <summary>Закрыто Kill-Switch, VPN ещё нет: повторные запуски закрываются.</summary>
        public bool Blocked;

        /// <summary>Трафик приложения ведёт ядро лаунчера (см. SetPolicy).</summary>
        public bool ViaCore;

        /// <summary>Закрывать процессы, если лаунчер завершится.</summary>
        public bool KillOnClose;

        /// <summary>Копии приложения вне лаунчера (только при ViaCore).</summary>
        public int Outside;

        /// <summary>«Через лаунчер»: копии вне задания берутся под контроль как запуск с плитки.</summary>
        public bool AdoptOutside;

        /// <summary>«Через лаунчер»: копия, открытая раньше этого момента, при невыполненном условии запуска не закрывается, а ждёт.</summary>
        public DateTime CloseOutsideFrom;

        public DateTime LastBlockedNotice;
        public int LastCount;

        public bool IsArmed => Jobs.Count > 0 || Loose.Count > 0;
    }

    private sealed class Job : IDisposable
    {
        public Job(SafeKernelHandle handle) => Handle = handle;

        public SafeKernelHandle Handle { get; }

        /// <summary>Число живых процессов в задании; -1, если узнать не удалось (тогда задание не трогаем).</summary>
        public int ActiveProcesses =>
            QueryInformationJobObject(Handle, JobInfoBasicAccounting, out var info,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(), IntPtr.Zero)
                ? (int)info.ActiveProcesses
                : -1;

        public bool Contains(SafeHandle process) => IsProcessInJob(process, Handle, out var result) && result;

        /// <summary>PID процессов задания; null, если узнать не удалось.</summary>
        public List<int>? ProcessIds()
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: два DWORD (сколько всего и сколько в списке), затем ULONG_PTR на каждый PID.
            var buffer = new UIntPtr[2 + 1024];
            if (!QueryInformationJobObject(Handle, JobInfoBasicProcessIdList, buffer, (uint)(buffer.Length * UIntPtr.Size), IntPtr.Zero))
                return null;

            int count, first;
            if (UIntPtr.Size == 8)
            {
                count = (int)((ulong)buffer[0] >> 32);
                first = 1;
            }
            else
            {
                count = (int)(uint)buffer[1];
                first = 2;
            }

            var pids = new List<int>(count);
            for (var i = 0; i < count && first + i < buffer.Length; i++)
                pids.Add((int)(ulong)buffer[first + i]);
            return pids;
        }

        /// <summary>
        /// Отпустить задание: оставшиеся в нём процессы (браузер, работающее приложение, убранное из лаунчера) не закроются
        /// вместе с ним. Если снять «закрывать вместе с заданием» не вышло, закрытый дескриптор закрыл бы их: тогда задание
        /// остаётся открытым до выхода лаунчера.
        /// </summary>
        public void Release()
        {
            if (SetLimits(0))
            {
                Dispose();
                return;
            }

            var error = Marshal.GetLastWin32Error();
            lock (Kept)
                Kept.Add(this);
            TechLog.Warn(SafeLauncher.Logging.LogAreas.App, SafeLauncher.Logging.LogCodes.AppProcesses,
                $"Задание не отпущено (ошибка {error}): его процессы закроются вместе с лаунчером");
        }

        /// <summary>Задания, которые не удалось отпустить: их дескрипторы держим, чтобы сборщик мусора их не закрыл.</summary>
        private static readonly List<Job> Kept = new();

        public bool SetLimits(uint flags, uint activeProcessLimit = 0)
        {
            var info = new JobObjectExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = flags;
            info.BasicLimitInformation.ActiveProcessLimit = activeProcessLimit;
            return SetInformationJobObject(Handle, JobInfoExtendedLimit, ref info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>());
        }

        /// <summary>Закрыть все процессы задания разом.</summary>
        public void Terminate()
        {
            // Сначала запрещаем новые процессы и выход из задания: всё, что родится, пока остальные закрываются,
            // будет убито сразу же.
            SetLimits(JobObjectLimitActiveProcess | JobObjectLimitKillOnJobClose, activeProcessLimit: 1);
            TerminateJobObject(Handle, 1);
        }

        public void Dispose() => Handle.Dispose();
    }

    /// <summary>Процесс, который не удалось поместить в задание.</summary>
    private sealed class LooseProcess : IDisposable
    {
        public LooseProcess(int pid, SafeKernelHandle? handle, Process? started)
        {
            Pid = pid;
            Handle = handle;
            Started = started;
        }

        public int Pid { get; }

        /// <summary>Пока наш дескриптор открыт, Windows не отдаст этот PID другому процессу.</summary>
        public SafeKernelHandle? Handle { get; }

        /// <summary>Процесс, запущенный через оболочку с запросом UAC.</summary>
        public Process? Started { get; }

        public bool IsAlive
        {
            get
            {
                try
                {
                    if (Handle is not null)
                        return ProcessTracker.IsAlive(Handle);
                    return Started is not null && !Started.HasExited;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>null — закрыт (или уже завершился), иначе текст ошибки.</summary>
        public string? Kill()
        {
            try
            {
                if (Started is not null)
                {
                    Started.Kill(entireProcessTree: true);
                    return null;
                }

                if (Handle is not null && !TerminateProcess(Handle, 1))
                {
                    var error = Marshal.GetLastWin32Error();
                    return IsAlive ? new Win32Exception(error).Message : null;
                }

                return null;
            }
            catch (InvalidOperationException)
            {
                return null; // уже завершился
            }
            catch (Exception ex)
            {
                return IsAlive ? ex.Message : null;
            }
        }

        public void Dispose()
        {
            Handle?.Dispose();
            Started?.Dispose();
        }
    }
}
