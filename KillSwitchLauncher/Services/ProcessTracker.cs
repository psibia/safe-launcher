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

/// <summary>Чем закончился запуск.</summary>
/// <param name="ProcessId">PID запущенного процесса; 0 — Windows не вернула процесс.</param>
/// <param name="Contained">Процесс и все его будущие потомки в задании Windows: Kill-Switch закроет их целиком.</param>
/// <param name="Elevated">Запущено с правами администратора через запрос UAC.</param>
/// <param name="Adopted">Сколько уже работавших процессов этого приложения взято под контроль.</param>
public sealed record LaunchResult(int ProcessId, bool Contained, bool Elevated, int Adopted);

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
///      копии вне задания под контроль не берутся: они идут напрямую, и лаунчер только считает их (OutsideFor),
///      чтобы предложить перезапуск под защитой. Выйти из задания такие процессы не могут, а при закрытии лаунчера
///      закрываются всегда.
///   7. Браузер, открытый по ссылке из приложения, попадает в его задание, но частью приложения не считается:
///      не идёт через подключение, не входит в число процессов и не закрывается вместе с приложением.
/// </summary>
public sealed class ProcessTracker : IAppProcesses, IDisposable
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BlockedNoticeInterval = TimeSpan.FromSeconds(10);

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

    private readonly ConcurrentDictionary<Guid, AppFamily> _families = new();

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

    /// <summary>Пересчитать копии приложения вне лаунчера прямо сейчас (перед запуском).</summary>
    public int RefreshOutside(Guid appId)
    {
        int count;
        lock (_gate)
        {
            if (_disposed || !_slots.TryGetValue(appId, out var slot) || !slot.ViaCore || slot.Family is null)
                return 0;
            var hits = FindFamilyLocked(new[] { slot });
            foreach (var hit in hits)
                hit.Handle.Dispose();
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
                if (GetImagePath(handle) is { } path && !IsBrowserOf(appId, Path.GetFileName(path)) &&
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
    /// Как вести приложение. viaCore — его трафик ведёт ядро лаунчера: копии вне задания не подбираются, а считаются
    /// (<see cref="OutsideFor"/>), выйти из задания нельзя, при закрытии лаунчера процессы закрываются всегда.
    /// killOnClose — закрывать приложение, если лаунчер завершится (в том числе аварийно).
    /// </summary>
    public void SetPolicy(AppEntry entry, bool viaCore, bool killOnClose)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var slot = GetSlot(entry);
            slot.Name = entry.Name;
            slot.ViaCore = viaCore;
            slot.KillOnClose = killOnClose || viaCore;
            if (viaCore && slot.Family is null && !entry.IsPackaged)
                SetFamilyLocked(slot, AppFamily.For(entry));
            if (!viaCore)
                slot.Outside = 0;
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
            flags = FlagsFor(GetSlot(entry));
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
            // так нельзя: её прежние соединения уже идут напрямую. Такие копии только считаются.
            adopted = 0;
            foreach (var hit in FindFamilyLocked(new[] { slot }))
            {
                if (slot.ViaCore)
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

        // Вне блокировки: Windows может запускать приложение несколько секунд.
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
            else
            {
                contained = AdoptLocked(new FamilyHit(slot, pid, handle, canControl));
            }

            if (slot.Family is not null)
            {
                foreach (var hit in FindFamilyLocked(new[] { slot }))
                {
                    if (slot.ViaCore)
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
        return new LaunchResult(pid, contained, false, adopted);
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

            foreach (var hit in FindFamilyLocked(new[] { slot }))
            {
                using (hit.Handle)
                {
                    if (TerminateHit(hit) is { } error)
                        failed.Add($"{slot.Name} (PID {hit.Pid}): {error}");
                }
            }

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
            foreach (var hit in FindFamilyLocked(armed.Where(s => s.Family is not null).ToList()))
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

        if (_slots.Values.Any(s => s.IsArmed || s.Retired.Count > 0 || (s.Blocked || s.ViaCore) && s.Family is not null))
        {
            _timerScheduled = true;
            _timer.Change(WatchInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer()
    {
        var changed = false;
        var blocked = new List<string>();

        try
        {
            lock (_gate)
            {
                _timerScheduled = false;
                if (_disposed)
                    return;

                try
                {
                    changed = SweepLocked(blocked);
                }
                finally
                {
                    ScheduleLocked();
                }
            }

            if (changed)
                Changed?.Invoke(this, EventArgs.Empty);
            foreach (var name in blocked)
                RelaunchBlocked?.Invoke(this, name);
        }
        catch (Exception ex)
        {
            // Исключение в потоке таймера уронило бы лаунчер, а вместе с ним и защиту.
            Debug.WriteLine(ex);
        }
    }

    /// <summary>Один проход наблюдения. Возвращает true, если изменилось число процессов.</summary>
    private bool SweepLocked(List<string> blockedNames)
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

        var watched = _slots.Values.Where(s => s.Family is not null && (s.IsArmed || s.Blocked || s.ViaCore)).ToList();
        var outside = new Dictionary<AppSlot, int>();
        foreach (var hit in FindFamilyLocked(watched))
        {
            if (hit.Slot.Blocked)
            {
                // Приложение, закрытое Kill-Switch, запустилось снова, а VPN всё ещё нет.
                using (hit.Handle)
                {
                    if (TerminateHit(hit) is null && DateTime.UtcNow - hit.Slot.LastBlockedNotice > BlockedNoticeInterval)
                    {
                        hit.Slot.LastBlockedNotice = DateTime.UtcNow;
                        if (!blockedNames.Contains(hit.Slot.Name))
                            blockedNames.Add(hit.Slot.Name);
                    }
                }
            }
            else if (hit.Slot.ViaCore)
            {
                // Копия мимо лаунчера: её соединения уже идут напрямую, под контроль не берём, только считаем.
                hit.Handle.Dispose();
                outside[hit.Slot] = outside.GetValueOrDefault(hit.Slot) + 1;
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

    private readonly record struct FamilyHit(AppSlot Slot, int Pid, SafeKernelHandle Handle, bool CanControl);

    /// <summary>
    /// Ищет процессы приложений вне заданий лаунчера. Возвращённые дескрипторы принадлежат вызывающему.
    /// </summary>
    private List<FamilyHit> FindFamilyLocked(IReadOnlyList<AppSlot> slots)
    {
        var hits = new List<FamilyHit>();
        slots = slots.Where(s => s.Family is not null).ToList(); // у программ из папки Windows и Store семейства нет
        if (slots.Count == 0)
            return hits;

        var ourJobs = _slots.Values.SelectMany(s => s.Jobs.Concat(s.Retired)).ToList();
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
            if (owner is null || ourJobs.Any(j => j.Contains(handle)) || !IsAlive(handle))
            {
                handle.Dispose();
                continue;
            }

            hits.Add(new FamilyHit(owner, pid, handle, canControl));
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
                    processName = (handle.IsInvalid ? null : GetImagePath(handle)) is { } path ? Path.GetFileName(path) : $"PID {pid}";
                    var browser = !slot.Loose.Any(p => p.Pid == pid) && IsBrowserOf(slot.Id, processName);
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

                var browser = IsBrowserOf(appId, name);
                lock (_membersGate)
                {
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

    /// <summary>Браузер, открытый приложением по ссылке (а не само приложение, даже если это браузер).</summary>
    private bool IsBrowserOf(Guid appId, string exeName) =>
        Browsers.IsBrowser(exeName) && !(_families.TryGetValue(appId, out var family) && family.HasName(exeName));

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

        /// <summary>Отпустить задание: оставшиеся в нём процессы (браузер) не закроются вместе с ним.</summary>
        public void Release()
        {
            SetLimits(0);
            Dispose();
        }

        public void SetLimits(uint flags, uint activeProcessLimit = 0)
        {
            var info = new JobObjectExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = flags;
            info.BasicLimitInformation.ActiveProcessLimit = activeProcessLimit;
            SetInformationJobObject(Handle, JobInfoExtendedLimit, ref info,
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
