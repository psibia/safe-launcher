using System;
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
/// </summary>
public sealed class ProcessTracker : IDisposable
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

    public ProcessTracker()
    {
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        ProcessIdToSessionId((uint)_ownPid, out _ownSession);
    }

    /// <summary>Изменилось число процессов под контролем. Может вызываться из любого потока.</summary>
    public event EventHandler? Changed;

    /// <summary>Закрытое Kill-Switch приложение запустилось снова без VPN и было закрыто. Аргумент — название. Любой поток.</summary>
    public event EventHandler<string>? RelaunchBlocked;

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
    public LaunchResult Launch(AppEntry entry)
    {
        ThrowIfDisposed();

        var workDir = ResolveWorkingDirectory(entry);
        var family = AppFamily.For(entry);
        var job = CreateJob(LiveFlags());

        int pid;
        bool contained;
        var elevated = false;
        LooseProcess? loose = null;

        // Запуск — вне блокировки: окно UAC может висеть сколько угодно, а Kill-Switch должен срабатывать сразу.
        try
        {
            if (TryCreateSuspended(entry, workDir, out var info, out var error))
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
            slot.Family = family;
            slot.Blocked = false;

            if (contained)
            {
                job.SetLimits(LiveFlags()); // Kill-Switch могли переключить, пока шёл запуск
                slot.Jobs.Add(job);
            }
            else
            {
                job.Dispose(); // пустое задание
            }

            if (loose is not null)
                slot.Loose.Add(loose);

            // Программа могла быть уже запущена (например, Discord из автозагрузки): тогда новая копия
            // передаёт управление старой и выходит. Старую берём под контроль сразу.
            adopted = 0;
            foreach (var hit in FindFamilyLocked(new[] { slot }))
            {
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
                slot.Family = AppFamily.ForImage(image);

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

    private static bool TryCreateSuspended(AppEntry entry, string workDir, out ProcessInformation info, out int error)
    {
        var commandLine = new StringBuilder("\"" + entry.ExePath + "\"");
        if (!string.IsNullOrWhiteSpace(entry.Arguments))
            commandLine.Append(' ').Append(entry.Arguments);

        var startup = new StartupInfo { cb = (uint)Marshal.SizeOf<StartupInfo>() };
        if (CreateProcessW(entry.ExePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CreateSuspended | CreateDefaultErrorMode, IntPtr.Zero,
                string.IsNullOrEmpty(workDir) ? null : workDir, ref startup, out info))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
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

            var armed = _slots.Values.Where(s => s.IsArmed).ToList();
            var counts = armed.ToDictionary(s => s, _ => 0);

            foreach (var slot in armed)
            {
                foreach (var job in slot.Jobs)
                {
                    counts[slot] += Math.Max(job.ActiveProcesses, 0);
                    job.Terminate();
                    slot.Retired.Add(job);
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

        Changed?.Invoke(this, EventArgs.Empty);
        return new KillReport(killed, failed);
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

    /// <summary>Закрывать ли приложения, если лаунчер завершится (в том числе аварийно). Совпадает с Kill-Switch.</summary>
    public void SetKillOnClose(bool enabled)
    {
        lock (_gate)
        {
            _killOnClose = enabled;
            foreach (var job in _slots.Values.SelectMany(s => s.Jobs))
                job.SetLimits(LiveFlags());
        }
    }

    private uint LiveFlags() =>
        // BREAKAWAY_OK: процесс может выйти из задания, только если сам явно об этом попросит
        // (так делают некоторые программы при перезапуске после обновления). Такие процессы с тем же .exe
        // подбирает обход семейства.
        JobObjectLimitBreakawayOk | (_killOnClose ? JobObjectLimitKillOnJobClose : 0);

    // ---------------------------------------------------------------- наблюдение

    private void ScheduleLocked()
    {
        if (_disposed || _timerScheduled)
            return;

        if (_slots.Values.Any(s => s.IsArmed || s.Retired.Count > 0 || (s.Blocked && s.Family is not null)))
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
            // Опустевшие задания больше не нужны.
            slot.Jobs.RemoveAll(job =>
            {
                if (job.ActiveProcesses != 0) return false;
                job.Dispose();
                return true;
            });

            // Закрытые Kill-Switch задания держим, пока не завершится последний процесс; на всякий случай добиваем.
            slot.Retired.RemoveAll(job =>
            {
                if (job.ActiveProcesses != 0)
                {
                    job.Terminate();
                    return false;
                }

                job.Dispose();
                return true;
            });

            slot.Loose.RemoveAll(process =>
            {
                if (process.IsAlive) return false;
                process.Dispose();
                return true;
            });
        }

        var watched = _slots.Values.Where(s => s.Family is not null && (s.IsArmed || s.Blocked)).ToList();
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
            else
            {
                AdoptLocked(hit);
            }
        }

        var changed = false;
        foreach (var slot in _slots.Values)
        {
            var count = CountLocked(slot);
            if (count == slot.LastCount) continue;
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
            var fresh = TryCreateJob(LiveFlags());
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

    // ---------------------------------------------------------------- состояние

    private AppSlot GetSlot(AppEntry entry)
    {
        if (!_slots.TryGetValue(entry.Id, out var slot))
            _slots[entry.Id] = slot = new AppSlot();
        return slot;
    }

    private static int CountLocked(AppSlot slot) =>
        slot.Jobs.Sum(j => Math.Max(j.ActiveProcesses, 0)) + slot.Loose.Count(p => p.IsAlive);

    private static Job CreateJob(uint flags) =>
        TryCreateJob(flags) ?? throw new Win32Exception(Marshal.GetLastWin32Error());

    private static Job? TryCreateJob(uint flags)
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var job = new Job(handle);
        job.SetLimits(flags);
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
    }

    // ---------------------------------------------------------------- типы

    /// <summary>Всё, что лаунчер знает об одном приложении.</summary>
    private sealed class AppSlot
    {
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
