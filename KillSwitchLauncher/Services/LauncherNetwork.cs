using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
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

    /// <summary>Соединения, которые сейчас идут через ядро, или почему их узнать не удалось (не дольше <paramref name="timeout"/>).</summary>
    Task<CoreConnectionList> ConnectionsAsync(TimeSpan timeout, CancellationToken cancellation);

    /// <summary>Прокси работающего ядра для проверки подключений через него; null — ядро не запущено или служба старая.</summary>
    CoreCheckProxy? CheckProxy { get; }

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

/// <summary>
/// Сервер подключения перестал отвечать: кого закрыли и кто ждёт без сети. <paramref name="NoNetwork"/> — у самого
/// компьютера в этот момент нет сети.
/// </summary>
public sealed record ConnectionDownInfo(Guid ConnectionId, string ConnectionName, KillReport Closed, IReadOnlyList<string> Waiting,
    bool NoNetwork = false);

/// <summary>
/// Перед запуском сервер не ответил: подключение, что сказало ядро и строки из его журнала. <paramref name="LocalCause"/> —
/// причина на компьютере (нет сети, ядро не видит сеть), тогда сервер ни при чём; null — не отвечает сам сервер.
/// </summary>
public sealed record ProbeFailure(Guid ConnectionId, string Summary, IReadOnlyList<string> CoreLog, string? LocalCause = null);

/// <summary>
/// Подключения лаунчера в работе: порядок «подключение → регистрация → запуск», проверка серверов, Kill-Switch
/// приложений через ядро и выключение ядра, когда оно больше не нужно.
///
/// Пока в настройках есть приложения через подключение лаунчера, ядро работает всё время, пока открыт лаунчер
/// (<see cref="KeepRunningAsync"/>): через него идут и копии этих приложений, запущенные мимо лаунчера. Без таких
/// приложений ядро работает, пока работает хоть одно приложение через него (и ещё <see cref="IdleStop"/> после последнего).
/// Пока ядро работает, через его адаптер идут и соединения остальных программ (ядро отправляет их напрямую), и выключение
/// ядра их оборвало бы. Поэтому без приложений ядро выключается, только когда соединений других программ через него
/// не осталось (проверка каждые <see cref="IdleStop"/>).
/// Перезапуск ядра (поменялись настройки) приостанавливает работающие приложения, чтобы их соединения не ушли напрямую,
/// пока адаптера нет, и обрывает их соединения и соединения других программ. Поэтому изменения, без которых программа
/// пошла бы не так, как выбрано (<see cref="UrgentChange"/>: копии приложений мимо лаунчера, работающие приложения,
/// «Защита от утечки DNS», приложение убрали), применяются сразу. Остальные (новые настройки самих подключений, IPv6)
/// ждут: ядро перезапускается, только если без них запускаемое приложение не пойдёт как настроено, или когда через ядро
/// ничего не идёт.
/// Сервер проверяется каждые <see cref="HealthInterval"/>; два промаха подряд — сервер не отвечает: приложения
/// с «Закрывать при обрыве» закрываются, остальные ждут без сети (их трафик всё равно идёт только в подключение).
/// Сеть Windows изменилась или компьютер проснулся — внеочередная проверка (<see cref="CheckSoon"/>).
/// Ядро может не заметить, что сеть вернулась (компьютер уснул сразу после отключения кабеля), и до перезапуска
/// отвечать на любое соединение «no route to internet». Если так, а у Windows сеть есть, виноват не сервер: ядро
/// перезапускается с теми же настройками (<see cref="RestartIfBlindAsync"/>), и промах не засчитывается.
/// Ядро упало или пропала служба: работающие приложения через ядро приостанавливаются (пока ядра нет, фильтры Windows
/// и так не пускают их в сеть) и ядро запускается заново. Не вышло или ядро падает <see cref="CrashesToClose"/>-й раз
/// за <see cref="CrashWindow"/> — приложения закрываются.
/// </summary>
public sealed class LauncherNetwork : IDisposable
{
    public static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Сколько ждать от ядра список соединений. Не ответило — считается, что соединений других программ нет.</summary>
    private static readonly TimeSpan ConnectionsTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Так начинается путь процесса приложения лаунчера в ядре: «launcher:метка|путь» (core/README.md).</summary>
    internal const string LauncherProcessPrefix = "launcher:";

    /// <summary>Перед запуском: столько попыток проверить сервер и не дольше этого (сразу после запуска ядра сеть может быть не готова).</summary>
    private const int LaunchProbeAttempts = 4;
    private static readonly TimeSpan LaunchProbeBudget = TimeSpan.FromSeconds(14);
    private const int MissesToDown = 2;

    /// <summary>Столько сбоев ядра за <see cref="CrashWindow"/> — и приложения закрываются, а не ждут перезапуска.</summary>
    private const int CrashesToClose = 3;
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(5);

    /// <summary>Так ядро отвечает на любое соединение, пока не знает адаптера с выходом в интернет.</summary>
    internal const string NoRouteMarker = "no route to internet";

    /// <summary>Не больше стольких перезапусков ядра, не видящего сеть, за <see cref="CrashWindow"/>: дальше перезапуск не помогает.</summary>
    private const int BlindRestartsLimit = 2;

    private readonly IAppProcesses _processes;
    private readonly Func<ICoreBackend> _backendFactory;
    private readonly TimeSpan _idleStop;
    private readonly TimeSpan _healthInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private readonly Timer _idleTimer;
    private readonly Timer _healthTimer;
    private readonly Timer _quickTimer;
    private readonly Dictionary<Guid, int> _misses = new();
    private readonly HashSet<Guid> _down = new();

    /// <summary>Номер запуска ядра: проверка, начатая со старым ядром, не засчитывает промах новому.</summary>
    private int _generation;

    /// <summary>Когда ядро падало (за последние <see cref="CrashWindow"/>).</summary>
    private readonly List<DateTime> _crashes = new();

    /// <summary>Когда перезапускалось ядро, не видящее сеть (за последние <see cref="CrashWindow"/>).</summary>
    private readonly List<DateTime> _blindRestarts = new();

    /// <summary>Когда в журнал записано, что ядро не видит сеть и больше не перезапускается (не чаще раза за <see cref="CrashWindow"/>).</summary>
    private DateTime _blindGaveUpLogged;

    private ICoreBackend? _backend;
    private CorePlan _desired = CorePlan.Empty;
    private CorePlan? _applied;

    /// <summary>Ядро потеряно (упало, пропала служба): следующий запуск — «после потери ядра».</summary>
    private string? _lostReason;

    /// <summary>Приложений через ядро нет, а ядро не выключено ради соединений других программ (в журнал — один раз).</summary>
    private bool _keptForOthers;

    private int _healthRunning;
    private bool _disposed;

    /// <summary>Устанавливается обновление: ядро остановлено и не запускается до <see cref="Resume"/>.</summary>
    private bool _held;

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
        _quickTimer = new Timer(_ => _ = CheckHealthAsync(quick: true), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Ядро упало или пропала служба и запустить его заново не вышло: приложения через ядро уже закрыты (отчёт бывает и
    /// пустым: тогда ядро было нужно только копиям приложений мимо лаунчера). Любой поток.
    /// </summary>
    public event Action<string, KillReport>? CoreLost;

    /// <summary>Ядро упало и запущено заново, приложения продолжили работу: причина сбоя и их имена. Любой поток.</summary>
    public event Action<string, IReadOnlyList<string>>? CoreRecovered;

    /// <summary>Сколько пытаться запустить ядро после сбоя: служба после своего сбоя возвращается через 5 с (в тестах короче).</summary>
    internal TimeSpan RecoveryBudget { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Пауза между попытками запустить ядро после сбоя (в тестах короче).</summary>
    internal TimeSpan RecoveryPause { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Через сколько ещё раз запустить ядро, если служба не вернулась за <see cref="RecoveryBudget"/> (в тестах короче).</summary>
    internal TimeSpan RecoveryRetry { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Сервер подключения перестал отвечать. Любой поток.</summary>
    public event Action<ConnectionDownInfo>? ConnectionDown;

    /// <summary>Пауза между попытками проверки перед запуском (в тестах короче).</summary>
    internal TimeSpan ProbeRetryPause { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Через сколько после смены сети или пробуждения проверять серверы (<see cref="CheckSoon"/>): сети нужно время,
    /// а несколько изменений подряд дают одну проверку. В тестах — никогда, тесты проверяют сами.
    /// </summary>
    internal TimeSpan QuickCheckDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Есть ли сеть у Windows (<see cref="HasNetwork"/>; в тестах подменяется).</summary>
    internal Func<bool> NetworkAvailable { get; set; } = HasNetwork;

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

    /// <summary>
    /// Как проверить подключение, пока работает ядро. WireGuard или AmneziaWG с теми же настройками, что у подключения
    /// в работающем ядре, проверяется через само ядро: вторая копия ядра с тем же ключом отбила бы у него связь с сервером,
    /// и приложения через это подключение остались бы без сети. Иначе null: проверка отдельной копией ядра. Любой поток.
    /// </summary>
    public CoreCheckRoute? CheckRouteFor(System.Text.Json.Nodes.JsonObject server)
    {
        lock (_state)
        {
            if (_applied?.WireGuardTag(server) is not { } tag || _backend?.CheckProxy is not { } proxy)
                return null;
            return new CoreCheckRoute(proxy, tag);
        }
    }

    /// <summary>
    /// Копии приложения, запущенные мимо лаунчера, в работающем ядре: true — без сети, false — через подключение
    /// приложения, null — напрямую (ядро не работает, приложение не через него или у него нет программ). Любой поток.
    /// </summary>
    public bool? OutsideBlocked(Guid appId)
    {
        lock (_state)
        {
            if (_applied is not { } plan || !plan.Apps.TryGetValue(appId, out var app))
                return null;
            return plan.Request.Apps.FirstOrDefault(r => r.Tag == app.Tag) is { Programs.Count: > 0 } route ? route.BlockOutside : null;
        }
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
    /// Новые настройки. Сами по себе ядро не трогают: их применяет <see cref="KeepRunningAsync"/> (срочное сразу,
    /// остальное, когда через ядро ничего не идёт) или запуск приложения, которому они нужны.
    /// </summary>
    public void Update(CorePlan plan)
    {
        lock (_state)
            _desired = plan;
    }

    /// <summary>
    /// Ядро работает с настройками, которые сейчас выбраны. Пока в настройках есть приложения через подключение лаунчера,
    /// ядро включено: по нему идут и копии этих приложений, запущенные мимо лаунчера (по ярлыку, автозагрузкой).
    /// Ядро не работает — запускается. Работает со старыми настройками: срочные изменения (<see cref="UrgentChange"/>)
    /// применяются сразу, работающие приложения на время перезапуска приостанавливаются; остальные ждут, пока через ядро
    /// ничего не идёт. Таких приложений в настройках не осталось — ядро выключается: сразу, если по нему шли копии
    /// приложений или работающие приложения, иначе когда через него ничего не будет идти.
    /// null — ядро работает или не нужно, иначе почему оно не запустилось.
    /// </summary>
    /// <param name="released">
    /// Приложения, которые только что убрали из лаунчера, пока они работали. Лаунчер их уже отпустил, но ядро по прежним
    /// настройкам ещё ведёт их соединения через VPN, поэтому для срочности они считаются работающими.
    /// </param>
    public async Task<string?> KeepRunningAsync(string trigger, CancellationToken cancellation = default,
        IReadOnlyCollection<Guid>? released = null)
    {
        lock (_state)
        {
            if (_held || _applied is null && _desired.Apps.Count == 0 || _applied?.Fingerprint == _desired.Fingerprint)
                return null;
        }

        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            CorePlan desired;
            CorePlan? applied;
            bool held;
            lock (_state)
            {
                desired = _desired;
                applied = _applied;
                held = _held;
            }
            if (held || applied is null && desired.Apps.Count == 0 || applied?.Fingerprint == desired.Fingerprint)
                return null;
            // Приостанавливаются и приложения, которые только что перевели на подключение лаунчера: фильтры Windows уже пускают
            // их в адаптер ядра, а пока ядро не запущено с новыми настройками, оно не знает их метки и отправило бы их
            // соединения напрямую.
            var all = _processes.RunningApps();
            if (applied is null)
                return await StartLockedAsync(desired, trigger, CorePlanDiff.Describe(null, desired),
                    all.Where(desired.Apps.ContainsKey).Distinct().ToList(), cancellation).ConfigureAwait(false);

            var running = all.Where(id => applied.Apps.ContainsKey(id) || desired.Apps.ContainsKey(id)).Distinct().ToList();
            var urgent = UrgentChange(applied, desired, released is { Count: > 0 } ? all.Concat(released).Distinct().ToList() : all);
            if (urgent is null)
            {
                if (desired.Apps.Count == 0)
                {
                    ScheduleIdleCheck(); // ядро выключится, когда через него ничего не будет идти
                    return null;
                }

                var names = running.Select(id => NameOf(id, applied, desired)).Distinct().ToList();
                var others = running.Count == 0 ? await OthersAsync(Backend(), cancellation).ConfigureAwait(false) : null;
                if (running.Count > 0 || others is { Count: > 0 })
                {
                    TechLog.Info(LogAreas.Network, LogCodes.CoreStart,
                        $"Ядро не перезапускается ({trigger}): перезапуск оборвал бы " +
                        (running.Count > 0 ? "соединения " + Names(names) : "соединения других программ (" + Describe(others!) + ")") +
                        ", а изменения не срочные. Они применятся при запуске приложения, которому они нужны, или когда через ядро " +
                        "ничего не будет идти: " + CorePlanDiff.Describe(applied, desired));
                    if (running.Count == 0)
                        ScheduleIdleCheck(); // проверим снова, когда соединения других программ закроются
                    return null;
                }

                return await StartLockedAsync(desired, trigger, CorePlanDiff.Describe(applied, desired), running, cancellation).ConfigureAwait(false);
            }

            if (desired.Apps.Count == 0)
            {
                // Ни одно приложение больше не идёт через ядро: и копии, и работающие приложения пойдут как настроено теперь.
                await StopLockedAsync($"через ядро больше не идёт ни одно приложение, а прежние настройки касались: {urgent} ({trigger})",
                    cancellation).ConfigureAwait(false);
                return null;
            }

            return await StartLockedAsync(desired, trigger, $"срочное изменение, касается: {urgent}. {CorePlanDiff.Describe(applied, desired)}",
                running, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Что в новых настройках нельзя отложить: без перезапуска ядра программа шла бы не так, как выбрано. Это копии
    /// приложений, запущенные мимо лаунчера (их программы, подключение, исключения, «без интернета»; приложение убрали),
    /// работающие приложения из <paramref name="running"/> (подключение, исключения, другое условие запуска) и «Защита
    /// от утечки DNS» (касается всех программ). Не срочно: настройки самих подключений (обновилась подписка, выбран другой
    /// сервер), IPv6 и приложения, которые не запущены и копии которых идут напрямую: прежние настройки продолжают работать,
    /// а запускаемое приложение получит новые при запуске. Исключение: приложение не запущено, а его копии мимо лаунчера
    /// идут через ядро. Запуска, который применил бы новые настройки подключения, может и не быть, а копия, открытая по
    /// ярлыку, должна идти через выбранный сервер, поэтому для таких приложений срочно и подключение (сервер, его DNS).
    /// У запущенного приложения новый сервер подействует при следующем запуске (так и пишет уведомление «Сервер выбран»),
    /// поэтому, пока то же подключение использует запущенное приложение, новый сервер ждёт и для копий.
    /// null — срочного нет, иначе чего оно касается (для журнала).
    /// </summary>
    internal static string? UrgentChange(CorePlan applied, CorePlan desired, IReadOnlyCollection<Guid> running)
    {
        if (applied.Request.HideDns != desired.Request.HideDns)
            return "DNS-запросы всех программ";
        if (desired.Apps.Count > 0 && applied.Request.DnsOutbound != desired.Request.DnsOutbound)
            return "редкие DNS-запросы";

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var app in applied.Apps.Values.Concat(desired.Apps.Values))
            names[app.Tag] = app.Name;
        var runningTags = new HashSet<string>(running.Select(CorePlan.AppTag), StringComparer.Ordinal);
        var before = applied.Request.Apps.ToDictionary(r => r.Tag, StringComparer.Ordinal);
        var after = desired.Request.Apps.ToDictionary(r => r.Tag, StringComparer.Ordinal);
        var launchedOutbounds = new HashSet<string>(before.Values.Where(r => runningTags.Contains(r.Tag)).Select(r => r.Outbound),
            StringComparer.Ordinal);
        foreach (var tag in before.Keys.Union(after.Keys).OrderBy(t => t, StringComparer.Ordinal))
        {
            before.TryGetValue(tag, out var was);
            after.TryGetValue(tag, out var now);
            var copies = was?.Programs is { Count: > 0 } || now?.Programs is { Count: > 0 };
            var launched = runningTags.Contains(tag);
            if (!copies && !launched)
                continue;
            if (was is not null && now is not null && was.Outbound == now.Outbound && was.BlockOutside == now.BlockOutside &&
                was.DirectDomains.SequenceEqual(now.DirectDomains, StringComparer.Ordinal) && CorePlanDiff.SamePrograms(was.Programs, now.Programs) &&
                (launched || launchedOutbounds.Contains(now.Outbound) ||
                 OutboundJson(applied, was.Outbound) == OutboundJson(desired, now.Outbound) &&
                 OutboundDns(applied, was.Outbound) == OutboundDns(desired, now.Outbound)))
                continue;

            var name = names.TryGetValue(tag, out var known) ? known : tag;
            return launched ? $"работающее приложение «{name}»" : $"копии «{name}» мимо лаунчера";
        }

        return null;
    }

    /// <summary>
    /// Запустить (или перезапустить) ядро с <paramref name="desired"/>. Работающие через ядро приложения
    /// <paramref name="running"/> на это время приостанавливаются (пока адаптера нет, их соединения ушли бы напрямую), а если
    /// ядро не запустилось, закрываются. Только под <see cref="_gate"/>. null — работает, иначе почему не запустилось.
    /// </summary>
    private async Task<string?> StartLockedAsync(CorePlan desired, string trigger, string reason, IReadOnlyList<Guid> running,
        CancellationToken cancellation)
    {
        CorePlan? applied;
        lock (_state)
            applied = _applied;
        var restart = applied is not null;
        TechLog.Info(LogAreas.Network, restart ? LogCodes.CoreRestart : LogCodes.CoreStart,
            $"Ядро: {(restart ? "перезапуск" : "запуск")} ({trigger}), чтобы программы шли как настроено. " +
            $"Причина: {reason}. Подключений {desired.Request.Outbounds.Count}, приложений в плане {desired.Apps.Count}" +
            (running.Count > 0 ? ". Приостановлены на время перезапуска: " + Names(running.Select(id => NameOf(id, applied, desired)).Distinct().ToList()) : string.Empty));
        var watch = Stopwatch.StartNew();
        using (running.Count > 0 ? _processes.Freeze(running) : null)
        {
            try
            {
                NextGeneration();
                await Backend().StartAsync(desired.Request, cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TechLog.Error(LogAreas.Network, LogCodes.CoreFail, $"Ядро не запустилось за {watch.ElapsedMilliseconds} мс ({trigger}): {ex.Message}" +
                    (ex is CoreServiceException cse ? CoreLines(ImportantLines(cse.Log)) : string.Empty));
                lock (_state)
                    _applied = null;
                // Ядра нет, а приложения приостановлены: закрываем те, что должны идти через ядро, пока они не продолжили
                // без него. Приложения, которые с новыми настройками идут не через ядро, ему больше не нужны.
                var close = running.Where(desired.Apps.ContainsKey).ToList();
                if (close.Count > 0)
                {
                    var report = _processes.KillApps(close, blockRelaunch: true);
                    CoreLost?.Invoke("Ядро не запустилось с новыми настройками.", report);
                }

                StateChanged?.Invoke();
                return ex.Message;
            }

            lock (_state)
            {
                _applied = desired;
                _lostReason = null;
                _keptForOthers = false;
                KeepDownLocked(desired);
            }
        }

        UnblockReady(desired);
        TechLog.Info(LogAreas.Network, LogCodes.CoreReady, $"Ядро работает, {watch.ElapsedMilliseconds} мс" +
            (running.Count > 0 ? " (приложения продолжили работу)" : string.Empty));
        _healthTimer.Change(_healthInterval, _healthInterval);
        StateChanged?.Invoke();
        return null;
    }

    private static string NameOf(Guid appId, CorePlan? applied, CorePlan desired) =>
        desired.Apps.TryGetValue(appId, out var app) || applied is not null && applied.Apps.TryGetValue(appId, out app) ? app.Name : appId.ToString();

    /// <summary>
    /// Подготовить запуск приложения через ядро: ядро работает с текущими настройками, сервер отвечает.
    /// null — можно запускать, иначе причина отказа для пользователя.
    /// </summary>
    public async Task<string?> PrepareLaunchAsync(Guid appId, CancellationToken cancellation = default)
    {
        CorePlan desired;
        lock (_state)
        {
            if (_held)
                return "Устанавливается обновление лаунчера: приложение можно будет запустить после него.";
            desired = _desired;
        }
        if (desired.Problems.TryGetValue(appId, out var problem))
        {
            TechLog.Warn(LogAreas.Network, LogCodes.AppReject, "Приложение не может идти через подключение: " + problem);
            return problem;
        }
        if (!desired.Apps.TryGetValue(appId, out var app))
            return null; // приложение не через ядро

        _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        CorePlan active;
        try
        {
            active = await EnsureCoreAsync(desired, app, cancellation).ConfigureAwait(false);
        }
        catch (CoreServiceException ex)
        {
            TechLog.Error(LogAreas.Network, LogCodes.CoreFail, $"Ядро не запустилось для «{app.Name}»: {ex.Message}" + CoreLines(ex.Log));
            return "Не удалось включить подключение: " + ex.Message;
        }

        // Ядро могло остаться с прежними настройками (SameRoute): одинаковые подключения WireGuard там одно, и нового
        // подключения приложения в них может не быть. Метка его туннеля та же, что в новых настройках.
        var tag = desired.ConnectionTags.TryGetValue(app.ConnectionId, out var desiredTag) ? desiredTag : active.ConnectionTags[app.ConnectionId];
        var backend = Backend();
        int generation;
        lock (_state)
            generation = _generation;
        var watch = Stopwatch.StartNew();
        var since = DateTime.Now;
        CoreProbe probe;
        var attempts = 0;
        var restarted = false;
        var blind = false;
        while (true)
        {
            attempts++;
            probe = await backend.ProbeAsync(tag, ProbeTimeout, cancellation).ConfigureAwait(false);
            if (probe.Delay is not null)
                break;
            TechLog.Info(LogAreas.Network, LogCodes.ProbeFail,
                $"Проверка «{app.ConnectionName}» перед запуском «{app.Name}», попытка {attempts}: {probe.Failure ?? "нет ответа"} ({watch.ElapsedMilliseconds} мс с начала)");
            // Ещё попытка, только если до конца бюджета остаётся хотя бы её срок: с промахом на первой странице проверка
            // откроет и вторую, то есть может занять вдвое больше.
            if (attempts < LaunchProbeAttempts && watch.Elapsed + ProbeRetryPause + ProbeTimeout <= LaunchProbeBudget)
            {
                await Task.Delay(ProbeRetryPause, cancellation).ConfigureAwait(false);
                continue;
            }

            // Ядро могло не заметить, что сеть вернулась: тогда оно перезапускается, и проверка начинается заново (один раз).
            if (restarted)
            {
                blind = await IsBlindAsync(backend, since, cancellation).ConfigureAwait(false);
                break;
            }

            var restart = await RestartIfBlindAsync(backend, generation, since, $"запуск «{app.Name}»", cancellation).ConfigureAwait(false);
            if (restart != true)
            {
                blind = restart == false;
                break;
            }

            restarted = true;
            backend = Backend();
            lock (_state)
                generation = _generation;
            attempts = 0;
            watch.Restart();
            since = DateTime.Now;
        }

        if (probe.Delay is null)
        {
            var local = blind ? "ядро лаунчера не видит сеть компьютера"
                : !NetworkAvailable() ? "компьютер не подключён к сети"
                : null;
            await ReportProbeFailureAsync(backend, app, tag, probe, attempts, local).ConfigureAwait(false);
            ScheduleIdleCheck();
            return local is null
                ? $"Сервер подключения «{app.ConnectionName}» не отвечает. Проверьте подключение в настройках или выберите другое. " +
                  "Подробности сохранены в журналах."
                : blind
                    ? "Ядро лаунчера не видит сеть компьютера, хотя она есть. Закройте лаунчер и откройте снова. Подробности сохранены в журналах."
                    : "Компьютер не подключён к сети. Проверьте кабель или Wi-Fi и запустите приложение снова.";
        }

        TechLog.Info(LogAreas.Network, LogCodes.ProbeOk,
            $"Сервер «{app.ConnectionName}» ответил за {probe.Delay} мс (попыток {attempts}" + (probe.Note is { } note ? $", по второй странице: {note}" : string.Empty) +
            $"), можно запускать «{app.Name}»");
        MarkUp(desired, app.ConnectionId, generation);
        return null;
    }

    private async Task ReportProbeFailureAsync(ICoreBackend backend, CoreApp app, string tag, CoreProbe probe, int attempts, string? local)
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
            (local is not null ? $"; причина на компьютере: {local}" : string.Empty) + CoreLines(important));
        ProbeFailed?.Invoke(new ProbeFailure(app.ConnectionId,
            $"Проверка сервера «{app.ConnectionName}» через ядро не прошла (попыток: {attempts}): {probe.Failure ?? "причина неизвестна"}",
            important, local));
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

    /// <summary>Процессы приложений изменились: если через ядро больше ничего не идёт, ядро скоро выключится.</summary>
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
                ["keptForOthers"] = _keptForOthers,
                ["down"] = new System.Text.Json.Nodes.JsonArray(_down.Select(id => (System.Text.Json.Nodes.JsonNode)Tag(id)).ToArray()),
                ["misses"] = new System.Text.Json.Nodes.JsonObject(_misses.Select(p =>
                    new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(Tag(p.Key), p.Value)))
            };
        }
    }

    /// <summary>
    /// Остановить ядро и не запускать его до <see cref="Resume"/>: лаунчер передаёт обновление службе, а служба принимает
    /// его, только когда ядро не работает. Приложения к этому времени закрыты.
    /// </summary>
    public async Task HoldAsync(CancellationToken cancellation = default)
    {
        lock (_state)
            _held = true;
        await StopAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>Обновление не состоялось: ядро снова запускается как обычно (вызывающий сам вызывает KeepRunningAsync).</summary>
    public void Resume()
    {
        lock (_state)
            _held = false;
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

    /// <summary>Ядро работает с настройками, с которыми <paramref name="app"/> пойдёт как настроено. Возвращает действующий план.</summary>
    private async Task<CorePlan> EnsureCoreAsync(CorePlan desired, CoreApp app, CancellationToken cancellation)
    {
        var trigger = $"запуск «{app.Name}»";
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            CorePlan? applied;
            string? lostReason;
            lock (_state)
            {
                applied = _applied;
                lostReason = _lostReason;
                _keptForOthers = false; // ядро снова нужно приложению
            }
            if (applied?.Fingerprint == desired.Fingerprint)
            {
                TechLog.Debug(LogAreas.Network, LogCodes.CoreStart, $"Ядро уже работает с нужными настройками ({trigger}), перезапуск не нужен");
                return applied;
            }

            // Перезапуск: пока адаптера нет, новые соединения работающих приложений ушли бы напрямую. Приостанавливаются и
            // работающие приложения, которые только что перевели на подключение лаунчера: ядро ещё не знает их метки.
            var running = _processes.RunningApps()
                .Where(id => applied is not null && applied.Apps.ContainsKey(id) || desired.Apps.ContainsKey(id)).Distinct().ToArray();
            var reason = CorePlanDiff.Describe(applied, desired);
            // Соединения других программ через ядро. При работающих приложениях их можно не спрашивать: ядро и так
            // перезапускается, только если без этого нельзя.
            var others = applied is not null && running.Length == 0
                ? await OthersAsync(Backend(), cancellation).ConfigureAwait(false)
                : null;

            // Перезапуск оборвал бы соединения работающих приложений или других программ. Запускаемому хватает нынешних
            // настроек — остальные изменения подождут, пока через ядро ничего не идёт: ядро остановится, а следующий
            // запуск возьмёт новые.
            if (applied is not null && (running.Length > 0 || others is { Count: > 0 }) && SameRoute(applied, desired, app.AppId))
            {
                TechLog.Info(LogAreas.Network, LogCodes.CoreStart,
                    $"Ядро не перезапускается ({trigger}): «{app.Name}» пойдёт так же, как с новыми настройками, а перезапуск оборвал бы " +
                    (running.Length > 0
                        ? "соединения " + Names(running.Select(id => NameOf(id, applied, desired)).Distinct().ToList())
                        : "соединения других программ (" + Describe(others!) + ")") +
                    $". Изменения применятся, когда через ядро ничего не будет идти: {reason}");
                return applied!;
            }
            if (applied is null && lostReason is not null)
                reason = "после потери ядра (" + lostReason + ")";
            TechLog.Info(LogAreas.Network, applied is null ? LogCodes.CoreStart : LogCodes.CoreRestart,
                $"Ядро: {(applied is null ? "запуск" : "перезапуск")} ({trigger}). Причина: {reason}. " +
                $"Подключений {desired.Request.Outbounds.Count}, приложений в плане {desired.Apps.Count}, IPv6 {(desired.Request.Ipv6 ? "да" : "нет")}" +
                (running.Length > 0 ? ". Приостановлены на время перезапуска: " + Names(running.Select(id => NameOf(id, applied, desired)).Distinct().ToList()) : string.Empty) +
                (others is { Count: > 0 } ? ". Оборвутся соединения других программ: " + Describe(others) : string.Empty));
            var watch = Stopwatch.StartNew();
            using (running.Length > 0 ? _processes.Freeze(running) : null)
            {
                try
                {
                    NextGeneration();
                    await Backend().StartAsync(desired.Request, cancellation).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    TechLog.Error(LogAreas.Network, LogCodes.CoreFail, $"Ядро не запустилось за {watch.ElapsedMilliseconds} мс: {ex.Message}" +
                        (ex is CoreServiceException cse ? CoreLines(ImportantLines(cse.Log)) : string.Empty));
                    lock (_state)
                        _applied = null;
                    // Ядра нет, а приложения приостановлены: закрываем те, что должны идти через ядро, пока они не продолжили без него.
                    var close = running.Where(desired.Apps.ContainsKey).ToList();
                    if (close.Count > 0)
                    {
                        var report = _processes.KillApps(close, blockRelaunch: true);
                        CoreLost?.Invoke("Ядро не запустилось с новыми настройками.", report);
                    }

                    StateChanged?.Invoke();
                    throw;
                }

                lock (_state)
                {
                    _applied = desired;
                    _lostReason = null;
                    KeepDownLocked(desired);
                }
            }

            UnblockReady(desired);

            TechLog.Info(LogAreas.Network, LogCodes.CoreReady,
                $"Ядро работает с новыми настройками, {watch.ElapsedMilliseconds} мс" + (running.Length > 0 ? " (приложения продолжили работу)" : string.Empty));

            _healthTimer.Change(_healthInterval, _healthInterval);
            StateChanged?.Invoke();
            return desired;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Пойдёт ли приложение через ядро с настройками <paramref name="applied"/> так же, как с <paramref name="desired"/>:
    /// то же подключение с теми же настройками и DNS, те же исключения, та же «Защита от утечки DNS», так же идут его копии
    /// мимо лаунчера.
    /// </summary>
    internal static bool SameRoute(CorePlan applied, CorePlan desired, Guid appId)
    {
        if (applied.Request.HideDns != desired.Request.HideDns ||
            !applied.Apps.TryGetValue(appId, out var was) || !desired.Apps.TryGetValue(appId, out var now))
            return false;

        var before = applied.Request.Apps.FirstOrDefault(r => r.Tag == was.Tag);
        var after = desired.Request.Apps.FirstOrDefault(r => r.Tag == now.Tag);
        return before is not null && after is not null && before.Outbound == after.Outbound &&
               before.DirectDomains.SequenceEqual(after.DirectDomains, StringComparer.Ordinal) &&
               before.BlockOutside == after.BlockOutside && CorePlanDiff.SamePrograms(before.Programs, after.Programs) &&
               OutboundJson(applied, before.Outbound) is { } json && json == OutboundJson(desired, after.Outbound) &&
               OutboundDns(applied, before.Outbound) == OutboundDns(desired, after.Outbound);
    }

    private static string? OutboundJson(CorePlan plan, string tag) =>
        plan.Request.Outbounds.FirstOrDefault(o => (string?)o["tag"] == tag)?.ToJsonString();

    private static string? OutboundDns(CorePlan plan, string tag) =>
        plan.Request.OutboundDns is { } dns && dns.TryGetValue(tag, out var server) ? server : null;

    private async Task StopLockedAsync(string reason, CancellationToken cancellation)
    {
        ICoreBackend? backend;
        lock (_state)
        {
            if (_applied is null)
                return;
            TechLog.Info(LogAreas.Network, reason == IdleReason ? LogCodes.CoreIdle : LogCodes.CoreStop, "Ядро останавливается: " + reason);
            _applied = null;
            _generation++;
            _keptForOthers = false;
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
                CorePlan desired;
                ICoreBackend? backend;
                lock (_state)
                {
                    applied = _applied;
                    desired = _desired;
                    backend = _backend;
                }
                // Работающими приложениями (и теми, что только переводят на подключение лаунчера) занимается KeepRunningAsync.
                if (applied is null || _processes.RunningApps().Any(id => applied.Apps.ContainsKey(id) || desired.Apps.ContainsKey(id)))
                    return;

                // Ядро нужно копиям приложений, запущенным мимо лаунчера, и настройки у него те, что нужно.
                var keep = desired.Apps.Count > 0;
                if (keep && applied.Fingerprint == desired.Fingerprint)
                    return;

                // Через адаптер ядра идут и соединения других программ: выключение или перезапуск ядра их оборвали бы. Но если
                // иначе копии приложений пошли бы не так, как выбрано (или приложение убрали), ядро не ждёт.
                var urgent = UrgentChange(applied, desired, Array.Empty<Guid>());
                var others = urgent is not null || backend is null ? null : await OthersAsync(backend, CancellationToken.None).ConfigureAwait(false);
                if (others is { Count: > 0 })
                {
                    bool first;
                    lock (_state)
                    {
                        if (!ReferenceEquals(_applied, applied))
                            return; // ядро потеряно, пока шла проверка
                        first = !_keptForOthers;
                        _keptForOthers = true;
                    }

                    if (first)
                        TechLog.Info(LogAreas.Network, LogCodes.CoreIdle, keep
                            ? "Приложений через ядро не осталось, новые настройки ядра ждут: через него идут соединения других программ (" +
                              Describe(others) + "), перезапуск их оборвал бы. Ядро перезапустится, когда они закроются, или при запуске приложения"
                            : "Приложений через ядро не осталось, но ядро не выключается: через него идут соединения других программ (" +
                              Describe(others) + "), выключение их оборвало бы. Ядро выключится, когда они закроются");
                    ScheduleIdleCheck();
                    return;
                }

                if (keep)
                    await StartLockedAsync(desired, "через ядро ничего не идёт", CorePlanDiff.Describe(applied, desired), Array.Empty<Guid>(),
                        CancellationToken.None).ConfigureAwait(false);
                else
                    await StopLockedAsync(urgent is null ? IdleReason : $"через ядро больше не идёт ни одно приложение, а прежние настройки касались: {urgent}",
                        CancellationToken.None).ConfigureAwait(false);
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

    private const string IdleReason = "через ядро больше ничего не идёт";

    /// <summary>
    /// Соединения других программ через ядро (не приложений лаунчера): выключение или перезапуск ядра их оборвёт.
    /// null — ядро не ответило (тогда, как и без этой проверки, считается, что их нет).
    /// </summary>
    private static async Task<IReadOnlyList<CoreConnection>?> OthersAsync(ICoreBackend backend, CancellationToken cancellation)
    {
        CoreConnectionList list;
        try
        {
            list = await backend.ConnectionsAsync(ConnectionsTimeout, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested)
        {
            list = CoreConnectionList.Failed(ex.Message);
        }

        if (list.Items is { } items)
            return OthersOf(items);
        TechLog.Warn(LogAreas.Network, LogCodes.CoreIdle, "Не удалось узнать у ядра соединения других программ: " + list.Failure);
        return null;
    }

    /// <summary>
    /// Соединения других программ из списка ядра. Не в счёт: приложения лаунчера (их соединения остаются у ядра
    /// после закрытия приложения, пока не истечёт срок UDP) и разовые запросы DNS и времени (UDP 53 и 123).
    /// </summary>
    internal static IReadOnlyList<CoreConnection> OthersOf(IEnumerable<CoreConnection> connections) =>
        connections.Where(c => !c.Process.StartsWith(LauncherProcessPrefix, StringComparison.Ordinal) &&
                               !(c.Network == "udp" && c.Port is 53 or 123))
            .ToList();

    /// <summary>Для журнала: «5: chrome.exe ×3, Telegram.exe ×2» (только программы, без адресов).</summary>
    internal static string Describe(IReadOnlyCollection<CoreConnection> connections)
    {
        var programs = connections.GroupBy(c => ProgramName(c.Process), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shown = programs.Take(5).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
        return $"{connections.Count}: {string.Join(", ", shown)}" + (programs.Count > 5 ? " и другие" : string.Empty);
    }

    private static string ProgramName(string path)
    {
        var name = path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        return name.Length > 0 ? name : "программа не определена";
    }

    private void OnLost(string reason, IReadOnlyList<string> log)
    {
        CorePlan? applied;
        bool keep;
        int crashes;
        lock (_state)
        {
            applied = _applied;
            keep = _desired.Apps.Count > 0;
            _applied = null;
            _generation++;
            _lostReason = reason;
            _keptForOthers = false;
            _misses.Clear(); // _down остаётся: сервер, который не отвечал, после перезапуска ядра проверяется заново
            if (_backend is { } backend)
            {
                backend.Lost -= OnLost;
                _backend = null;
                backend.Dispose();
            }

            var now = DateTime.UtcNow;
            _crashes.RemoveAll(t => now - t > CrashWindow);
            if (applied is not null)
                _crashes.Add(now);
            crashes = _crashes.Count;
        }

        _healthTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (applied is null)
            return;

        var running = _processes.RunningApps().Where(applied.Apps.ContainsKey).Distinct().ToList();
        // Ядро нужно работающим приложениям или копиям приложений, запущенным мимо лаунчера.
        var restart = running.Count > 0 || keep;
        if (restart && crashes < CrashesToClose)
        {
            // Пока ядра нет, фильтры Windows не пускают приложения в сеть, а приостановленные ничего и не отправят.
            var frozen = running.Count > 0 ? _processes.Freeze(running) : null;
            TechLog.Warn(LogAreas.Network, LogCodes.CoreLost,
                $"Ядро потеряно: {reason} Сбоев за 5 минут: {crashes}. Ядро запускается заново" +
                (running.Count > 0 ? ", приостановлены: " + Names(running.Select(id => applied.Apps[id].Name).ToList()) : string.Empty) +
                CoreLines(log.TakeLast(12).ToList()));
            StateChanged?.Invoke();
            _ = RecoverAsync(applied, running, frozen, reason);
            return;
        }

        // Ядро падает слишком часто: перезапуск не поможет, приложения закрываются, что бы ни стояло в «Закрывать при обрыве».
        var report = _processes.KillApps(applied.Apps.Keys.ToList(), blockRelaunch: true);
        TechLog.Error(LogAreas.Network, LogCodes.CoreLost, $"Ядро потеряно: {reason}" +
            (restart ? $" Сбоев за 5 минут: {crashes}, перезапуск не поможет." : string.Empty) +
            $" Закрыты: {Names(report.Killed)}" +
            (report.Failed.Count > 0 ? $", не удалось закрыть: {Names(report.Failed)}" : string.Empty) + CoreLines(log.TakeLast(12).ToList()));
        // Ядро ждало, пока закроются соединения других программ: сообщать пользователю не о чем. Если же оно было нужно
        // копиям приложений мимо лаунчера, без него они идут напрямую, и об этом нужно сказать, даже если закрывать было некого.
        if (report.Killed.Count > 0 || report.Failed.Count > 0 || keep)
            CoreLost?.Invoke(restart
                ? $"{reason} Ядро падает слишком часто ({crashes} раза за 5 минут), поэтому " +
                  (report.Killed.Count > 0 || report.Failed.Count > 0 ? "приложения закрыты." : "оно выключено до запуска приложения из лаунчера.")
                : reason, report);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Ядро упало, приложения <paramref name="running"/> приостановлены (или их нет, а ядро нужно копиям приложений):
    /// запустить ядро с прежними настройками (служба после своего сбоя возвращается не сразу, поэтому несколько попыток)
    /// и продолжить их. Не вышло — закрыть их.
    /// </summary>
    private async Task RecoverAsync(CorePlan plan, IReadOnlyList<Guid> running, IDisposable? frozen, string reason)
    {
        var names = running.Select(id => plan.Apps[id].Name).ToList();
        var watch = Stopwatch.StartNew();
        string? failure = null;
        var recovered = false;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                CorePlan? started;
                lock (_state)
                    started = _applied;
                if (started is not null)
                {
                    // Ядро уже запустил запуск другого приложения: приложения продолжат с ним.
                    recovered = true;
                }

                for (var attempt = 1; !recovered; attempt++)
                {
                    try
                    {
                        NextGeneration();
                        await Backend().StartAsync(plan.Request, CancellationToken.None).ConfigureAwait(false);
                        lock (_state)
                        {
                            _applied = plan;
                            _lostReason = null;
                            KeepDownLocked(plan);
                        }

                        UnblockReady(plan);
                        recovered = true;
                    }
                    catch (CoreServiceException ex)
                    {
                        failure = ex.Message;
                        TechLog.Warn(LogAreas.Network, LogCodes.CoreFail,
                            $"Ядро не запустилось после сбоя (попытка {attempt}, {watch.ElapsedMilliseconds} мс): {ex.Message}" + CoreLines(ImportantLines(ex.Log)));
                        if (watch.Elapsed + RecoveryPause >= RecoveryBudget)
                            break;
                        await Task.Delay(RecoveryPause).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            // лаунчер закрывается
            failure = ex.Message;
        }

        try
        {
            if (recovered)
            {
                frozen?.Dispose(); // ядро уже знает приложения: их новые соединения пойдут через подключения
                TechLog.Info(LogAreas.Network, LogCodes.CoreReady,
                    $"Ядро запущено заново после сбоя за {watch.ElapsedMilliseconds} мс, продолжили работу: {Names(names)}");
                _healthTimer.Change(_healthInterval, _healthInterval);
                // Работающих приложений не было: сообщать пользователю не о чем.
                if (names.Count > 0)
                    CoreRecovered?.Invoke(reason, names);
                StateChanged?.Invoke();
                // Ядро запущено с настройками до сбоя: если их поменяли, пока ядра не было, применить, как после изменения настроек.
                _ = CatchUpAsync("ядро запущено заново после сбоя");
                return;
            }

            // Ядро так и не запустилось: приложения закрываются, пока приостановлены.
            var report = _processes.KillApps(running, blockRelaunch: true);
            frozen?.Dispose();
            TechLog.Error(LogAreas.Network, LogCodes.CoreLost,
                $"Ядро не удалось запустить заново за {watch.ElapsedMilliseconds} мс ({failure ?? "причина неизвестна"}). Закрыты: {Names(report.Killed)}" +
                (report.Failed.Count > 0 ? $", не удалось закрыть: {Names(report.Failed)}" : string.Empty));
            bool keep;
            lock (_state)
                keep = !_disposed && _desired.Apps.Count > 0;
            if (report.Killed.Count > 0 || report.Failed.Count > 0 || keep)
                CoreLost?.Invoke(reason + " Запустить ядро заново не удалось.", report);
            StateChanged?.Invoke();
            if (keep)
                _ = RetryLaterAsync();
        }
        catch (Exception)
        {
            // лаунчер закрывается: приложения через ядро Windows закрывает вместе с ним
        }
    }

    /// <summary>
    /// Служба не вернулась за время восстановления, а ядро нужно копиям приложений мимо лаунчера: ещё одна попытка позже.
    /// Потом ядро запустится при запуске приложения или изменении настроек.
    /// </summary>
    private async Task RetryLaterAsync()
    {
        try
        {
            await Task.Delay(RecoveryRetry).ConfigureAwait(false);
            lock (_state)
            {
                if (_disposed || _applied is not null || _desired.Apps.Count == 0)
                    return;
            }

            if (await KeepRunningAsync("ещё одна попытка после сбоя").ConfigureAwait(false) is null)
                TechLog.Info(LogAreas.Network, LogCodes.CoreReady, "Ядро запущено заново после сбоя со второй попытки");
        }
        catch (Exception)
        {
            // лаунчер закрывается
        }
    }

    private async Task CatchUpAsync(string trigger)
    {
        try
        {
            await KeepRunningAsync(trigger).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // лаунчер закрывается
        }
    }

    /// <summary>
    /// Проверка серверов, через которые сейчас идут приложения. Внеочередная (<paramref name="quick"/>, после смены сети
    /// или пробуждения) промахи не засчитывает: сеть появляется не сразу, и короткий перебой не должен закрывать
    /// приложения. Она отмечает вернувшиеся серверы и перезапускает ядро, если оно не видит сеть.
    /// </summary>
    internal async Task CheckHealthAsync(bool quick = false)
    {
        if (Interlocked.Exchange(ref _healthRunning, 1) != 0)
            return;
        try
        {
            CorePlan? applied;
            ICoreBackend? backend;
            int generation;
            lock (_state)
            {
                applied = _applied;
                backend = _backend;
                generation = _generation;
            }

            if (applied is null || backend is null)
                return;

            // Подключения работающих приложений и те, что не отвечали: когда сервер вернётся, закрытые при обрыве приложения
            // снова можно запускать, даже если ни одно из них сейчас не запущено.
            var running = _processes.RunningApps().Where(applied.Apps.ContainsKey).Select(id => applied.Apps[id]).ToList();
            List<Guid> down;
            lock (_state)
                down = _down.Where(applied.ConnectionTags.ContainsKey).ToList();
            foreach (var connectionId in running.Select(a => a.ConnectionId).Concat(down).Distinct().ToList())
            {
                if (applied.Apps.Values.FirstOrDefault(a => a.ConnectionId == connectionId) is not { } user)
                    continue; // подключение осталось в ядре только ради DNS
                var tag = applied.ConnectionTags[connectionId];
                var name = user.ConnectionName;
                var since = DateTime.Now;
                var probe = await backend.ProbeAsync(tag, ProbeTimeout, CancellationToken.None).ConfigureAwait(false);
                if (probe.Delay is { } delay)
                {
                    if (probe.Note is { } note)
                        TechLog.Info(LogAreas.Network, LogCodes.ProbeOk, $"Проверка «{name}»: {delay} мс по второй странице, {note}");
                    else
                        TechLog.Debug(LogAreas.Network, LogCodes.ProbeOk, $"Проверка «{name}»: {delay} мс");
                    MarkUp(applied, connectionId, generation);
                    continue;
                }

                // Ядро не видит сеть, которая у Windows есть: виноват не сервер. Ядро перезапущено, промах не засчитывается,
                // а серверы проверяются снова, как только ядро поднимется.
                if (await RestartIfBlindAsync(backend, generation, since, quick ? "внеочередная проверка" : "проверка сервера",
                        CancellationToken.None).ConfigureAwait(false) == true)
                {
                    CheckSoon();
                    return;
                }

                if (quick)
                {
                    TechLog.Debug(LogAreas.Network, LogCodes.ProbeFail,
                        $"Внеочередная проверка: сервер «{name}» не ответил, промах не засчитывается: {probe.Failure ?? "нет ответа"}");
                    continue;
                }

                bool justDown;
                int misses;
                lock (_state)
                {
                    if (!ReferenceEquals(_applied, applied) || _generation != generation)
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
        CorePlan desired;
        lock (_state)
            desired = _desired;
        // «Закрывать при обрыве» и имена — как в настройках сейчас: они меняются и без перезапуска ядра.
        var apps = plan.Apps.Values.Where(a => a.ConnectionId == connectionId)
            .Select(a => desired.Apps.TryGetValue(a.AppId, out var now) ? a with { Name = now.Name, CloseOnDrop = now.CloseOnDrop } : a)
            .ToList();
        var running = new HashSet<Guid>(_processes.RunningApps());
        var close = apps.Where(a => a.CloseOnDrop).Select(a => a.AppId).ToList();
        var report = close.Count > 0 ? _processes.KillApps(close, blockRelaunch: true) : new KillReport(Array.Empty<string>(), Array.Empty<string>());
        var waiting = apps.Where(a => !a.CloseOnDrop && running.Contains(a.AppId)).Select(a => a.Name).ToList();
        var name = ConnectionNameOf(connectionId, plan, desired);
        TechLog.Info(LogAreas.KillSwitch, LogCodes.KsClose, $"Обрыв «{name}»: закрыты {Names(report.Killed)}" +
            (report.Failed.Count > 0 ? $", не удалось закрыть {Names(report.Failed)}" : string.Empty) +
            (waiting.Count > 0 ? $", ждут без сети {Names(waiting)}" : string.Empty));
        ConnectionDown?.Invoke(new ConnectionDownInfo(connectionId, name, report, waiting, !NetworkAvailable()));
        StateChanged?.Invoke();
        ScheduleIdleCheck();
    }

    /// <summary>
    /// Сеть Windows изменилась или компьютер проснулся: проверить серверы вне очереди через <see cref="QuickCheckDelay"/>
    /// (<see cref="CheckHealthAsync"/> с quick). Несколько вызовов подряд дают одну проверку. Любой поток.
    /// </summary>
    public void CheckSoon()
    {
        lock (_state)
        {
            if (_disposed || _applied is null)
                return;
            _quickTimer.Change(QuickCheckDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Сервер не ответил. Ядро могло не заметить, что сеть вернулась (например, компьютер уснул сразу после отключения
    /// кабеля): тогда до перезапуска оно отвечает на любое соединение «no route to internet», хотя у Windows сеть есть.
    /// В этом случае ядро перезапускается с теми же настройками, а работающие через него приложения на это время
    /// приостанавливаются, как при любом перезапуске: фильтры Windows и так не пускают их мимо адаптера ядра.
    /// null — ядро сеть видит (виноват сервер, или сети нет у самой Windows); true — ядро перезапущено (или его уже
    /// перезапустили), проверку стоит повторить; false — ядро сеть не видит, а перезапуск не помог или не удался.
    /// </summary>
    /// <param name="since">Когда началась проверка: старые строки журнала о том, что сети не было, в счёт не идут.</param>
    private async Task<bool?> RestartIfBlindAsync(ICoreBackend backend, int generation, DateTime since, string trigger,
        CancellationToken cancellation)
    {
        if (!await IsBlindAsync(backend, since, cancellation).ConfigureAwait(false))
            return null;

        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            CorePlan? applied;
            lock (_state)
            {
                if (_generation != generation)
                    return _applied is not null ? true : null; // ядро уже перезапустили, пока шла проверка
                applied = _applied;
                if (applied is null)
                    return null;
                var now = DateTime.UtcNow;
                _blindRestarts.RemoveAll(t => now - t > CrashWindow);
                if (_blindRestarts.Count >= BlindRestartsLimit)
                {
                    if (now - _blindGaveUpLogged > CrashWindow)
                    {
                        _blindGaveUpLogged = now;
                        TechLog.Warn(LogAreas.Network, LogCodes.CoreFail,
                            $"Ядро не видит сеть, хотя у Windows она есть ({trigger}), и после {_blindRestarts.Count} перезапусков за 5 минут. " +
                            "Больше не перезапускается: промахи засчитываются как обычно");
                    }

                    return false;
                }

                _blindRestarts.Add(now);
            }

            var running = _processes.RunningApps().Where(applied.Apps.ContainsKey).Distinct().ToList();
            return await StartLockedAsync(applied, trigger,
                "ядро не видит сеть, которая у Windows есть («no route to internet»), так бывает после сна компьютера",
                running, cancellation).ConfigureAwait(false) is null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ядро не видит сеть, которая у Windows есть (<see cref="CoreSeesNoNetwork"/>).</summary>
    private async Task<bool> IsBlindAsync(ICoreBackend backend, DateTime since, CancellationToken cancellation)
    {
        if (!NetworkAvailable())
            return false;
        try
        {
            return CoreSeesNoNetwork(await backend.LogTailAsync(cancellation).ConfigureAwait(false), since);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // журнала нет: судить о ядре не по чему
        }
    }

    /// <summary>
    /// Ядро не видит сеть: в его журнале с момента <paramref name="since"/> (секунда в запас) есть «no route to internet».
    /// Строки журнала начинаются с местного времени «ЧЧ:мм:сс.ммм»: служба работает на том же компьютере.
    /// </summary>
    internal static bool CoreSeesNoNetwork(IReadOnlyList<string> log, DateTime since)
    {
        for (var i = log.Count - 1; i >= 0; i--)
        {
            var line = log[i];
            if (line.Length < 12 || !line.Contains(NoRouteMarker, StringComparison.Ordinal) ||
                !TimeSpan.TryParseExact(line.AsSpan(0, 12), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var time))
                continue;
            // Время без даты: разница по кругу суток (проверка могла начаться до полуночи).
            var after = time - since.TimeOfDay;
            if (after < -TimeSpan.FromHours(12))
                after += TimeSpan.FromDays(1);
            else if (after > TimeSpan.FromHours(12))
                after -= TimeSpan.FromDays(1);
            if (after >= -TimeSpan.FromSeconds(1))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Есть ли у Windows сеть: работающий адаптер со шлюзом, кроме адаптера ядра. Без шлюза выхода в интернет нет, и ядро
    /// отвечает «no route to internet» по делу. Не узнать — считается, что сеть есть.
    /// </summary>
    public static bool HasNetwork()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    VpnAdapterDetector.IsOwnTunnel(adapter.Name))
                    continue;
                if (adapter.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                    return true;
            }
        }
        catch (NetworkInformationException)
        {
            return true;
        }

        return false;
    }

    private void NextGeneration()
    {
        lock (_state)
            _generation++;
    }

    /// <summary>
    /// Ядро запущено заново: серверы, которые не отвечали, считаются недоступными, пока проверка не покажет обратное (иначе
    /// приложения, ждущие сеть, на время выглядели бы защищёнными). Подключения, которых в настройках больше нет, забываются.
    /// </summary>
    private void KeepDownLocked(CorePlan plan)
    {
        _down.RemoveWhere(id => !plan.ConnectionTags.ContainsKey(id));
        _misses.Clear();
    }

    /// <summary>
    /// Ядро работает: приложения, закрытые из-за сбоя ядра, снова можно запускать как угодно (копия по ярлыку пойдёт через
    /// ядро). Закрытые из-за сервера, который всё ещё не отвечает, ждут его (<see cref="MarkUp"/>).
    /// </summary>
    private void UnblockReady(CorePlan plan)
    {
        HashSet<Guid> down;
        lock (_state)
            down = new HashSet<Guid>(_down);
        foreach (var app in plan.Apps.Values.Where(a => !down.Contains(a.ConnectionId)))
            _processes.StopBlocking(app.AppId);
    }

    private void MarkUp(CorePlan plan, Guid connectionId, int generation)
    {
        bool wasDown;
        CorePlan desired;
        lock (_state)
        {
            if (_generation != generation)
                return; // ответило старое ядро: новое проверится отдельно
            _misses.Remove(connectionId);
            wasDown = _down.Remove(connectionId);
            desired = _desired;
        }

        if (!wasDown)
            return;
        var name = ConnectionNameOf(connectionId, plan, desired);
        TechLog.Info(LogAreas.Network, LogCodes.ConnUp, $"Сервер «{name}» снова отвечает");
        foreach (var app in plan.Apps.Values.Where(a => a.ConnectionId == connectionId))
            _processes.StopBlocking(app.AppId);
        ConnectionUp?.Invoke(connectionId, name);
        StateChanged?.Invoke();
    }

    /// <summary>Имя подключения как в настройках сейчас: его можно переименовать, не перезапуская ядро.</summary>
    private static string ConnectionNameOf(Guid connectionId, CorePlan plan, CorePlan desired) =>
        (desired.Apps.Values.FirstOrDefault(a => a.ConnectionId == connectionId)
         ?? plan.Apps.Values.First(a => a.ConnectionId == connectionId)).ConnectionName;

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
        _quickTimer.Dispose();
        if (backend is not null)
        {
            backend.Lost -= OnLost;
            backend.Dispose(); // служба останавливает ядро, как только закрывается связь с лаунчером
        }
    }
}
