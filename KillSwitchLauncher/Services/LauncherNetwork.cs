using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.Services;

/// <summary>Ядро VPN, как его видит <see cref="LauncherNetwork"/>: служба Windows или подмена в тестах.</summary>
public interface ICoreBackend : IDisposable
{
    /// <summary>Запустить или перезапустить ядро с этими настройками. Ошибка — <see cref="CoreServiceException"/>.</summary>
    Task StartAsync(CoreStartRequest request, CancellationToken cancellation);

    Task StopAsync(CancellationToken cancellation);

    /// <summary>Проверка сервера через подключение: задержка или причина неудачи.</summary>
    Task<CoreProbe> ProbeAsync(string outboundTag, TimeSpan timeout, CancellationToken cancellation);

    /// <summary>Последние строки журнала работающего ядра (для журнала событий, когда проверка не прошла).</summary>
    Task<IReadOnlyList<string>> LogTailAsync(CancellationToken cancellation);

    /// <summary>Ядро остановилось само или пропала связь со службой. Причина для пользователя и журнал ядра. Любой поток.</summary>
    event Action<string, IReadOnlyList<string>>? Lost;
}

/// <summary>Процессы приложений, как их видит <see cref="LauncherNetwork"/> (<see cref="ProcessTracker"/>).</summary>
public interface IAppProcesses
{
    IReadOnlyList<Guid> RunningApps();

    IDisposable Freeze(IReadOnlyCollection<Guid> appIds);

    KillReport KillApps(IReadOnlyCollection<Guid> appIds, bool blockRelaunch);

    void StopBlocking(Guid? appId = null);
}

/// <summary>Состояние приложения, которое идёт через подключение лаунчера.</summary>
public enum CoreAppState
{
    /// <summary>Не через ядро или ядро не нужно.</summary>
    None,

    /// <summary>Трафик идёт через подключение.</summary>
    Protected,

    /// <summary>Сервер подключения не отвечает, приложение ждёт без сети («Закрывать при обрыве» выключено).</summary>
    Waiting
}

/// <summary>Сервер подключения перестал отвечать: кого закрыли и кто ждёт без сети.</summary>
public sealed record ConnectionDownInfo(Guid ConnectionId, string ConnectionName, KillReport Closed, IReadOnlyList<string> Waiting);

/// <summary>Перед запуском сервер не ответил: подключение, что сказало ядро и строки из его журнала.</summary>
public sealed record ProbeFailure(Guid ConnectionId, string Summary, IReadOnlyList<string> CoreLog);

/// <summary>
/// Подключения лаунчера в работе: порядок «подключение → регистрация → запуск», проверка серверов, Kill-Switch
/// приложений через ядро и выключение ядра, когда оно больше не нужно.
///
/// Ядро запускается перед запуском первого приложения и работает, пока работает хоть одно приложение через него
/// (и ещё <see cref="IdleStop"/> после последнего). Перезапуск ядра (поменялись настройки) приостанавливает
/// работающие приложения, чтобы их соединения не ушли напрямую, пока адаптера нет.
/// Сервер проверяется каждые <see cref="HealthInterval"/>; два промаха подряд — сервер не отвечает: приложения
/// с «Закрывать при обрыве» закрываются, остальные ждут без сети (их трафик всё равно идёт только в подключение).
/// Ядро упало или пропала служба — все приложения через ядро закрываются сразу: без ядра они пошли бы напрямую.
/// </summary>
public sealed class LauncherNetwork : IDisposable
{
    public static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Перед запуском: столько попыток проверить сервер и не дольше этого (сразу после запуска ядра сеть может быть не готова).</summary>
    private const int LaunchProbeAttempts = 4;
    private static readonly TimeSpan LaunchProbeBudget = TimeSpan.FromSeconds(14);
    private const int MissesToDown = 2;

    private readonly IAppProcesses _processes;
    private readonly Func<ICoreBackend> _backendFactory;
    private readonly TimeSpan _idleStop;
    private readonly TimeSpan _healthInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private readonly Timer _idleTimer;
    private readonly Timer _healthTimer;
    private readonly Dictionary<Guid, int> _misses = new();
    private readonly HashSet<Guid> _down = new();

    private ICoreBackend? _backend;
    private CorePlan _desired = CorePlan.Empty;
    private CorePlan? _applied;

    /// <summary>Ядро потеряно (упало, пропала служба): следующий запуск — «после потери ядра».</summary>
    private string? _lostReason;
    private int _healthRunning;
    private bool _disposed;

    public LauncherNetwork(IAppProcesses processes, Func<ICoreBackend> backendFactory)
        : this(processes, backendFactory, IdleStop, HealthInterval)
    {
    }

    /// <summary>Для тестов: свои интервалы.</summary>
    internal LauncherNetwork(IAppProcesses processes, Func<ICoreBackend> backendFactory, TimeSpan idleStop, TimeSpan healthInterval)
    {
        _processes = processes;
        _backendFactory = backendFactory;
        _idleStop = idleStop;
        _healthInterval = healthInterval;
        _idleTimer = new Timer(_ => OnIdle(), null, Timeout.Infinite, Timeout.Infinite);
        _healthTimer = new Timer(_ => _ = CheckHealthAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Ядро упало или пропала служба; приложения через ядро уже закрыты. Любой поток.</summary>
    public event Action<string, KillReport>? CoreLost;

    /// <summary>Сервер подключения перестал отвечать. Любой поток.</summary>
    public event Action<ConnectionDownInfo>? ConnectionDown;

    /// <summary>Пауза между попытками проверки перед запуском (в тестах короче).</summary>
    internal TimeSpan ProbeRetryPause { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Перед запуском сервер так и не ответил: что сказало ядро и его последние ошибки — для журнала событий. Любой поток.
    /// </summary>
    public event Action<ProbeFailure>? ProbeFailed;

    /// <summary>Сервер снова отвечает. Любой поток.</summary>
    public event Action<Guid, string>? ConnectionUp;

    /// <summary>Ядро запустилось, остановилось или поменялось состояние подключений. Любой поток.</summary>
    public event Action? StateChanged;

    /// <summary>Ядро работает с настройками, которые сейчас действуют.</summary>
    public bool IsCoreRunning
    {
        get
        {
            lock (_state)
                return _applied is not null;
        }
    }

    /// <summary>Метка приложения для ядра (ответ «чей процесс»): только приложения в работающих настройках ядра. Любой поток.</summary>
    public string? TagFor(Guid appId)
    {
        lock (_state)
            return _applied is { } plan && plan.Apps.TryGetValue(appId, out var app) ? app.Tag : null;
    }

    /// <summary>Идёт ли приложение через ядро при текущих настройках.</summary>
    public bool IsViaCore(Guid appId)
    {
        lock (_state)
            return _desired.Apps.ContainsKey(appId) || _desired.Problems.ContainsKey(appId);
    }

    public CoreAppState StateOf(Guid appId)
    {
        lock (_state)
        {
            if (_applied is not { } plan || !plan.Apps.TryGetValue(appId, out var app))
                return CoreAppState.None;
            return _down.Contains(app.ConnectionId) ? CoreAppState.Waiting : CoreAppState.Protected;
        }
    }

    /// <summary>
    /// Новые настройки. Работающее ядро не перезапускается сразу: новые настройки вступят в силу при следующем
    /// запуске приложения (перезапуск ядра на секунды приостанавливает всё, что через него идёт).
    /// </summary>
    public void Update(CorePlan plan)
    {
        lock (_state)
            _desired = plan;
    }

    /// <summary>
    /// Подготовить запуск приложения через ядро: ядро работает с текущими настройками, сервер отвечает (если у приложения
    /// «Не запускать без VPN»). null — можно запускать, иначе причина отказа для пользователя.
    /// </summary>
    public async Task<string?> PrepareLaunchAsync(Guid appId, CancellationToken cancellation = default)
    {
        CorePlan desired;
        lock (_state)
            desired = _desired;
        if (desired.Problems.TryGetValue(appId, out var problem))
        {
            TechLog.Warn(LogAreas.Network, LogCodes.AppReject, "Приложение не может идти через подключение: " + problem);
            return problem;
        }
        if (!desired.Apps.TryGetValue(appId, out var app))
            return null; // приложение не через ядро

        _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            await EnsureCoreAsync(desired, $"запуск «{app.Name}»", cancellation).ConfigureAwait(false);
        }
        catch (CoreServiceException ex)
        {
            TechLog.Error(LogAreas.Network, LogCodes.CoreFail, $"Ядро не запустилось для «{app.Name}»: {ex.Message}" + CoreLines(ex.Log));
            return "Не удалось включить подключение: " + ex.Message;
        }

        if (!app.RequireVpn)
        {
            TechLog.Info(LogAreas.Network, LogCodes.ProbeOk, $"«{app.Name}» без «Не запускать без VPN»: сервер «{app.ConnectionName}» перед запуском не проверяется");
            return null;
        }

        var tag = desired.ConnectionTags[app.ConnectionId];
        var backend = Backend();
        var watch = Stopwatch.StartNew();
        CoreProbe probe;
        var attempts = 0;
        while (true)
        {
            attempts++;
            probe = await backend.ProbeAsync(tag, ProbeTimeout, cancellation).ConfigureAwait(false);
            if (probe.Delay is null)
                TechLog.Info(LogAreas.Network, LogCodes.ProbeFail,
                    $"Проверка «{app.ConnectionName}» перед запуском «{app.Name}», попытка {attempts}: {probe.Failure ?? "нет ответа"} ({watch.ElapsedMilliseconds} мс с начала)");
            if (probe.Delay is not null || attempts >= LaunchProbeAttempts || watch.Elapsed + ProbeRetryPause >= LaunchProbeBudget)
                break;
            await Task.Delay(ProbeRetryPause, cancellation).ConfigureAwait(false);
        }

        if (probe.Delay is null)
        {
            await ReportProbeFailureAsync(backend, app, tag, probe, attempts).ConfigureAwait(false);
            ScheduleIdleCheck();
            return $"Сервер подключения «{app.ConnectionName}» не отвечает. Проверьте подключение в настройках или выберите другое. " +
                   "Подробности сохранены в журналах.";
        }

        TechLog.Info(LogAreas.Network, LogCodes.ProbeOk,
            $"Сервер «{app.ConnectionName}» ответил за {probe.Delay} мс (попыток {attempts}), можно запускать «{app.Name}»");
        MarkUp(desired, app.ConnectionId);
        return null;
    }

    private async Task ReportProbeFailureAsync(ICoreBackend backend, CoreApp app, string tag, CoreProbe probe, int attempts)
    {
        IReadOnlyList<string> log;
        try
        {
            log = await backend.LogTailAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            log = Array.Empty<string>(); // журнал — только подсказка, без него тоже сообщаем
        }

        var important = ImportantLines(log, tag);
        TechLog.Warn(LogAreas.Network, LogCodes.ProbeFail,
            $"Сервер «{app.ConnectionName}» не ответил перед запуском «{app.Name}» (попыток {attempts}): {probe.Failure ?? "причина неизвестна"}" +
            CoreLines(important));
        ProbeFailed?.Invoke(new ProbeFailure(app.ConnectionId,
            $"Проверка сервера «{app.ConnectionName}» через ядро не прошла (попыток: {attempts}): {probe.Failure ?? "причина неизвестна"}",
            important));
    }

    /// <summary>Строки ядра для технического журнала (в одну запись).</summary>
    private static string CoreLines(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? string.Empty : ". Строки ядра: " + string.Join(" | ", lines);

    /// <summary>
    /// Ошибки и предупреждения из журнала ядра; если их нет — последние строки. Сначала — строки о проверяемом
    /// подключении (<paramref name="tag"/>): ошибки чужих программ, идущих напрямую, их не вытесняют.
    /// </summary>
    internal static IReadOnlyList<string> ImportantLines(IReadOnlyList<string> log, string? tag = null)
    {
        if (tag is { Length: > 0 })
        {
            var own = log.Where(l => l.Contains("[" + tag + "]", StringComparison.Ordinal)).TakeLast(6).ToList();
            if (own.Count > 0)
                return own;
        }

        var important = log.Where(l => (l.Contains(" ERROR ", StringComparison.Ordinal) ||
                                        l.Contains(" WARN ", StringComparison.Ordinal) ||
                                        l.Contains(" FATAL ", StringComparison.Ordinal)) &&
                                       !l.Contains("outbound/direct[direct]", StringComparison.Ordinal)).ToList();
        return (important.Count > 0 ? important : log.ToList()).TakeLast(important.Count > 0 ? 6 : 3).ToList();
    }

    /// <summary>Процессы приложений изменились: если через ядро больше ничего не работает, ядро скоро выключится.</summary>
    public void OnProcessesChanged() => ScheduleIdleCheck();

    /// <summary>Состояние для state.json выгрузки: работает ли ядро, совпадают ли примененные и желаемые настройки, упавшие подключения.</summary>
    public System.Text.Json.Nodes.JsonObject DescribeState()
    {
        lock (_state)
        {
            string? Hash(CorePlan? plan) => plan is null ? null
                : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plan.Fingerprint)), 0, 6).ToLowerInvariant();
            string Tag(Guid id) => (_applied ?? _desired).ConnectionTags.TryGetValue(id, out var tag) ? tag : CorePlan.ConnectionTag(id);
            return new System.Text.Json.Nodes.JsonObject
            {
                ["coreRunning"] = _applied is not null,
                ["appliedHash"] = Hash(_applied),
                ["desiredHash"] = Hash(_desired),
                ["upToDate"] = _applied is not null && _applied.Fingerprint == _desired.Fingerprint,
                ["lostReason"] = _lostReason,
                ["down"] = new System.Text.Json.Nodes.JsonArray(_down.Select(id => (System.Text.Json.Nodes.JsonNode)Tag(id)).ToArray()),
                ["misses"] = new System.Text.Json.Nodes.JsonObject(_misses.Select(p =>
                    new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(Tag(p.Key), p.Value)))
            };
        }
    }

    /// <summary>Выключить ядро сейчас (лаунчер закрывается: приложения уже закрыты).</summary>
    public async Task StopAsync(CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await StopLockedAsync("лаунчер закрывается", cancellation).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ICoreBackend Backend()
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_backend is null)
            {
                _backend = _backendFactory();
                _backend.Lost += OnLost;
            }

            return _backend;
        }
    }

    private async Task EnsureCoreAsync(CorePlan desired, string trigger, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            CorePlan? applied;
            string? lostReason;
            lock (_state)
            {
                applied = _applied;
                lostReason = _lostReason;
            }
            if (applied?.Fingerprint == desired.Fingerprint)
            {
                TechLog.Debug(LogAreas.Network, LogCodes.CoreStart, $"Ядро уже работает с нужными настройками ({trigger}), перезапуск не нужен");
                return;
            }

            // Перезапуск: пока адаптера нет, новые соединения работающих приложений ушли бы напрямую.
            var running = applied is null
                ? Array.Empty<Guid>()
                : _processes.RunningApps().Where(applied.Apps.ContainsKey).ToArray();
            var reason = CorePlanDiff.Describe(applied, desired);
            if (applied is null && lostReason is not null)
                reason = "после потери ядра (" + lostReason + ")";
            TechLog.Info(LogAreas.Network, applied is null ? LogCodes.CoreStart : LogCodes.CoreRestart,
                $"Ядро: {(applied is null ? "запуск" : "перезапуск")} ({trigger}). Причина: {reason}. " +
                $"Подключений {desired.Request.Outbounds.Count}, приложений в плане {desired.Apps.Count}, IPv6 {(desired.Request.Ipv6 ? "да" : "нет")}" +
                (running.Length > 0 ? $". Приостановлены на время перезапуска: {string.Join(", ", running.Select(id => applied!.Apps[id].Name))}" : string.Empty));
            var watch = Stopwatch.StartNew();
            using (running.Length > 0 ? _processes.Freeze(running) : null)
            {
                try
                {
                    await Backend().StartAsync(desired.Request, cancellation).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    TechLog.Error(LogAreas.Network, LogCodes.CoreFail, $"Ядро не запустилось за {watch.ElapsedMilliseconds} мс: {ex.Message}" +
                        (ex is CoreServiceException cse ? CoreLines(ImportantLines(cse.Log)) : string.Empty));
                    lock (_state)
                        _applied = null;
                    if (running.Length > 0)
                    {
                        // Ядра нет, а приложения приостановлены: закрываем, пока они не продолжили напрямую.
                        var report = _processes.KillApps(running, blockRelaunch: true);
                        CoreLost?.Invoke("Ядро не запустилось с новыми настройками.", report);
                    }

                    StateChanged?.Invoke();
                    throw;
                }

                lock (_state)
                {
                    _applied = desired;
                    _lostReason = null;
                    _down.Clear();
                    _misses.Clear();
                }
            }

            TechLog.Info(LogAreas.Network, LogCodes.CoreReady,
                $"Ядро работает с новыми настройками, {watch.ElapsedMilliseconds} мс" + (running.Length > 0 ? " (приложения продолжили работу)" : string.Empty));

            _healthTimer.Change(_healthInterval, _healthInterval);
            StateChanged?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopLockedAsync(string reason, CancellationToken cancellation)
    {
        ICoreBackend? backend;
        lock (_state)
        {
            if (_applied is null)
                return;
            TechLog.Info(LogAreas.Network, reason == IdleReason ? LogCodes.CoreIdle : LogCodes.CoreStop, "Ядро останавливается: " + reason);
            _applied = null;
            _down.Clear();
            _misses.Clear();
            backend = _backend;
        }

        _healthTimer.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (backend is not null)
                await backend.StopAsync(cancellation).ConfigureAwait(false);
        }
        catch (CoreServiceException)
        {
            // служба недоступна: ядра тоже нет
        }

        StateChanged?.Invoke();
    }

    private void ScheduleIdleCheck()
    {
        lock (_state)
        {
            if (_disposed || _applied is null)
                return;
        }

        _idleTimer.Change(_idleStop, Timeout.InfiniteTimeSpan);
    }

    private async void OnIdle()
    {
        try
        {
            if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            {
                ScheduleIdleCheck(); // идёт запуск: проверим позже
                return;
            }

            try
            {
                CorePlan? applied;
                lock (_state)
                    applied = _applied;
                if (applied is null || _processes.RunningApps().Any(applied.Apps.ContainsKey))
                    return;
                await StopLockedAsync(IdleReason, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // таймер не должен ронять лаунчер
        }
    }

    private const string IdleReason = "через ядро больше ничего не работает";

    private void OnLost(string reason, IReadOnlyList<string> log)
    {
        CorePlan? applied;
        lock (_state)
        {
            applied = _applied;
            _applied = null;
            _lostReason = reason;
            _down.Clear();
            _misses.Clear();
            if (_backend is { } backend)
            {
                backend.Lost -= OnLost;
                _backend = null;
                backend.Dispose();
            }
        }

        _healthTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (applied is null)
            return;

        // Без ядра трафик приложений пошёл бы напрямую: закрываем сразу, что бы ни стояло в «Закрывать при обрыве».
        var report = _processes.KillApps(applied.Apps.Keys.ToList(), blockRelaunch: true);
        TechLog.Error(LogAreas.Network, LogCodes.CoreLost, $"Ядро потеряно: {reason}. Закрыты: {Names(report.Killed)}" +
            (report.Failed.Count > 0 ? $", не удалось закрыть: {Names(report.Failed)}" : string.Empty) + CoreLines(log.TakeLast(12).ToList()));
        CoreLost?.Invoke(reason, report);
        StateChanged?.Invoke();
    }

    /// <summary>Проверка серверов, через которые сейчас идут приложения.</summary>
    internal async Task CheckHealthAsync()
    {
        if (Interlocked.Exchange(ref _healthRunning, 1) != 0)
            return;
        try
        {
            CorePlan? applied;
            ICoreBackend? backend;
            lock (_state)
            {
                applied = _applied;
                backend = _backend;
            }

            if (applied is null || backend is null)
                return;

            var running = _processes.RunningApps().Where(applied.Apps.ContainsKey).Select(id => applied.Apps[id]).ToList();
            foreach (var connectionId in running.Select(a => a.ConnectionId).Distinct().ToList())
            {
                var tag = applied.ConnectionTags[connectionId];
                var name = running.First(a => a.ConnectionId == connectionId).ConnectionName;
                var probe = await backend.ProbeAsync(tag, ProbeTimeout, CancellationToken.None).ConfigureAwait(false);
                if (probe.Delay is { } delay)
                {
                    TechLog.Debug(LogAreas.Network, LogCodes.ProbeOk, $"Проверка «{name}»: {delay} мс");
                    MarkUp(applied, connectionId);
                    continue;
                }

                bool justDown;
                int misses;
                lock (_state)
                {
                    if (!ReferenceEquals(_applied, applied))
                        return; // ядро перезапустили, пока шла проверка
                    misses = _misses[connectionId] = _misses.GetValueOrDefault(connectionId) + 1;
                    justDown = misses >= MissesToDown && _down.Add(connectionId);
                }

                TechLog.Warn(LogAreas.Network, LogCodes.ProbeFail,
                    $"Сервер «{name}» не ответил на проверку (промах {misses} из {MissesToDown}): {probe.Failure ?? "нет ответа"}");
                if (justDown)
                {
                    IReadOnlyList<string> log;
                    try
                    {
                        log = await backend.LogTailAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        log = Array.Empty<string>();
                    }

                    TechLog.Warn(LogAreas.Network, LogCodes.ConnDown,
                        $"Сервер «{name}» не отвечает: {probe.Failure ?? "нет ответа"}" + CoreLines(ImportantLines(log, tag)));
                    OnConnectionDown(applied, connectionId);
                }
            }
        }
        catch (Exception)
        {
            // таймер не должен ронять лаунчер
        }
        finally
        {
            Volatile.Write(ref _healthRunning, 0);
        }
    }

    private void OnConnectionDown(CorePlan plan, Guid connectionId)
    {
        var apps = plan.Apps.Values.Where(a => a.ConnectionId == connectionId).ToList();
        var running = new HashSet<Guid>(_processes.RunningApps());
        var close = apps.Where(a => a.CloseOnDrop).Select(a => a.AppId).ToList();
        var report = close.Count > 0 ? _processes.KillApps(close, blockRelaunch: true) : new KillReport(Array.Empty<string>(), Array.Empty<string>());
        var waiting = apps.Where(a => !a.CloseOnDrop && running.Contains(a.AppId)).Select(a => a.Name).ToList();
        TechLog.Info(LogAreas.KillSwitch, LogCodes.KsClose, $"Обрыв «{apps[0].ConnectionName}»: закрыты {Names(report.Killed)}" +
            (report.Failed.Count > 0 ? $", не удалось закрыть {Names(report.Failed)}" : string.Empty) +
            (waiting.Count > 0 ? $", ждут без сети {Names(waiting)}" : string.Empty));
        ConnectionDown?.Invoke(new ConnectionDownInfo(connectionId, apps[0].ConnectionName, report, waiting));
        StateChanged?.Invoke();
        ScheduleIdleCheck();
    }

    private void MarkUp(CorePlan plan, Guid connectionId)
    {
        bool wasDown;
        lock (_state)
        {
            _misses.Remove(connectionId);
            wasDown = _down.Remove(connectionId);
        }

        if (!wasDown)
            return;
        TechLog.Info(LogAreas.Network, LogCodes.ConnUp, $"Сервер «{plan.Apps.Values.First(a => a.ConnectionId == connectionId).ConnectionName}» снова отвечает");
        foreach (var app in plan.Apps.Values.Where(a => a.ConnectionId == connectionId))
            _processes.StopBlocking(app.AppId);
        ConnectionUp?.Invoke(connectionId, plan.Apps.Values.First(a => a.ConnectionId == connectionId).ConnectionName);
        StateChanged?.Invoke();
    }

    private static string Names(IReadOnlyCollection<string> names) => names.Count == 0 ? "никто" : string.Join(", ", names);

    public void Dispose()
    {
        ICoreBackend? backend;
        lock (_state)
        {
            if (_disposed)
                return;
            _disposed = true;
            backend = _backend;
            _backend = null;
            _applied = null;
        }

        _idleTimer.Dispose();
        _healthTimer.Dispose();
        if (backend is not null)
        {
            backend.Lost -= OnLost;
            backend.Dispose(); // служба останавливает ядро, как только закрывается связь с лаунчером
        }
    }
}
