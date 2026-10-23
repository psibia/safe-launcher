using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IConnectionActions, ILogActions, IDisposable
{
    private const int ErrorCancelled = 1223; // пользователь отменил запрос UAC

    private readonly SettingsStorage _storage;
    private readonly IDialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private readonly ProcessTracker _tracker = new();
    private readonly UpdateService _updates;
    private readonly ActivityLog _log;
    private readonly DnsMonitor _dns;

    // Приложения, для которых сейчас запускается журнал DNS перед стартом (повторный клик игнорируется).
    private readonly HashSet<Guid> _dnsLaunching = new();

    private Dictionary<string, string> _settingsSnapshot = new();
    private bool? _lastIpv6;
    private string? _keepCoreError;

    // Загрузка перевела настройки с системного VPN: сообщить один раз, когда окно уже показано.
    private SystemVpnMigration? _movedFromSystemVpn;
    private DispatcherTimer? _logTimer;

    // Предупреждение «не удалось взять под полный контроль» показываем один раз на приложение за сеанс.
    private readonly HashSet<Guid> _warnedUncontained = new();
    private readonly VpnMonitor _monitor;

    // Подключения лаунчера: ядро VPN, проверка серверов, Kill-Switch приложений через ядро.
    private readonly LauncherNetwork _network;

    // Блокировка трафика мимо VPN и скрытие DNS: фильтры Windows через службу.
    private readonly VpnGuard _guard;

    // Приложения, для которых фильтры уже поставлены, а процесса ещё нет (идёт запуск).
    private readonly HashSet<Guid> _guardPending = new();

    // Пути программ приложений, замеченные за сеанс: при следующих запусках фильтр сразу ставится и на них.
    private readonly Dictionary<Guid, HashSet<string>> _guardPaths = new();

    // Системный прокси на этом компьютере (другая VPN-программа): путь мимо подключения лаунчера.
    private readonly SystemProxyWatcher _proxy;
    private string? _warnedProxy;

    // Приложения, которые сейчас запускаются (подключение, проверка сервера): повторный клик игнорируется.
    private readonly HashSet<Guid> _launching = new();

    // Запущены разово без сети («Запустить без сети»): пока работают, сети у них нет, что бы ни стояло в настройках.
    private readonly HashSet<Guid> _offlineRuns = new();
    private readonly CoreAppProcesses _coreProcesses;

    // Приложения под системным VPN: их правила адаптеров и защита. Снимок для фонового потока монитора.
    private volatile IReadOnlyList<AdapterApp> _adapterApps = Array.Empty<AdapterApp>();

    // Подписки обновляются при запуске и потом раз в час проверяется, не пора ли.
    private readonly DispatcherTimer _subscriptionTimer;
    private bool _isUpdatingSubscriptions;

    private sealed record AdapterApp(Guid Id, string Name, VpnRule Rule, bool CloseOnDrop);

    // Приложения с «Через лаунчер»: их условия запуска. Снимок для потока наблюдения трекера (CheckOutsideLaunch).
    private volatile IReadOnlyDictionary<Guid, OutsideLaunch> _outsideLaunches = new Dictionary<Guid, OutsideLaunch>();

    private sealed record OutsideLaunch(NetworkPlanKind Kind, VpnRule? Rule, string? ConnectionName);

    // Проверка подключений: отдельная копия ядра с правами пользователя. Тесты подставляют свою.
    private readonly Func<VpnConnection, Task<ConnectionCheck>> _checkConnection;

    // Страницы окна: плитки, настройки, приложение, мастер первого запуска.
    private MainPage _page = MainPage.Apps;
    private SettingsSection _section = SettingsSection.Connections;
    private AppSettingsViewModel? _appPage;
    private WelcomeViewModel? _welcome;
    private AddAppsViewModel? _addApps;
    private readonly Func<Task<IReadOnlyList<StartMenuApp>>> _findStartMenuApps;

    // Сводка в шапке.
    private string _summaryTitle = "Ничего не запущено";
    private string _summarySubtitle = string.Empty;
    private SummaryTone _summaryTone = SummaryTone.Idle;
    private bool _isSummaryOpen;
    private string? _otherVpnWarning;

    // Глифы Segoe Fluent Icons / Segoe MDL2 Assets
    private const string GlyphLock = "\uE72E";
    private const string GlyphUnlock = "\uE785";
    private const string GlyphPlay = "\uE768";
    private const string GlyphAdd = "\uE710";
    private const string GlyphPower = "\uE7E8";
    private const string GlyphCheck = "\uE73E";
    private const string GlyphInfo = "\uE946";
    private const string GlyphGlobe = "\uE774";
    private const string GlyphStop = "\uE71A";

    private bool _isVpnConnected;
    private string _vpnDetailsText = "Проверка сетевых адаптеров…";
    private string _vpnSubtitle = "Проверка…";
    private string _statusMessage = "Готово";

    // Черновик настроек: переключатели на странице меняют только его, а в силу он вступает по кнопке «Применить».
    private bool _draftKillSwitch;
    private bool _draftHideDns;
    private bool _draftStartWithWindows;
    private HideBehavior _draftHideBehavior;
    private OutsideCopies _draftOutsideCopies;
    private string _draftDomainsText = string.Empty;

    /// <param name="log">Журнал событий; по умолчанию — в папке настроек. Тесты передают свой.</param>
    /// <param name="updates">Обновления; по умолчанию — обычные пути. Тесты передают свои.</param>
    /// <param name="dnsSource">Источник событий журнала DNS; по умолчанию — Windows. Тесты передают свой.</param>
    /// <param name="coreBackend">Ядро VPN; по умолчанию — служба «Безопасный лаунчер — сеть». Тесты передают своё.</param>
    /// <param name="checkConnection">
    /// Проверка подключения; по умолчанию — отдельной копией ядра, а WireGuard, который уже работает в ядре, — через ядро.
    /// Тесты передают свою.
    /// </param>
    /// <param name="guardBackend">Фильтры Windows; по умолчанию — через службу. Тесты передают свои.</param>
    /// <param name="systemProxy">Чтение системного прокси; по умолчанию — из реестра. Тесты передают своё.</param>
    /// <param name="startMenuApps">Программы из меню «Пуск»; по умолчанию — ярлыки обоих меню (в фоне). Тесты передают свои.</param>
    public MainViewModel(SettingsStorage storage, IDialogService dialogs, Dispatcher dispatcher,
        ActivityLog? log = null, UpdateService? updates = null,
        Func<Action<DnsQueryEvent>, Task<IDnsEventSource>>? dnsSource = null,
        Func<ICoreBackend>? coreBackend = null,
        Func<VpnConnection, Task<ConnectionCheck>>? checkConnection = null,
        Func<IGuardBackend>? guardBackend = null,
        Func<LocalProxy?>? systemProxy = null,
        Func<Task<IReadOnlyList<StartMenuApp>>>? startMenuApps = null)
    {
        _log = log ?? new ActivityLog();
        _updates = updates ?? new UpdateService();
        _dns = new DnsMonitor(_tracker.TryGetOwner, dnsSource);
        _storage = storage;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _settings = storage.Load();
        _movedFromSystemVpn = _settings.MovedFromSystemVpn;
        if (storage.LoadProblem is { } loadProblem)
        {
            TechLog.Error(LogAreas.Settings, LogCodes.SettingsChange, $"Настройки не прочитаны ({storage.FilePath}): {loadProblem}. Сохранение отключено");
            _dispatcher.BeginInvoke(() => _dialogs.ShowError(
                $"Не удалось прочитать настройки: {loadProblem}\n\n{storage.FilePath}\n\n" +
                "Файл занят другой программой или к нему нет доступа. Лаунчер открыт с пустыми настройками и не сохранит их, " +
                "чтобы не затереть ваши. Закройте лаунчер и откройте его снова."));
        }
        _findStartMenuApps = startMenuApps ?? (() => Task.Run(StartMenuApps.Find));
        ApplyLogSettings();
        if (_movedFromSystemVpn is not null)
        {
            // Переход с системного VPN сохраняем сразу, а сообщаем о нём, когда окно уже показано (ReportSystemVpnRemoved).
            TechLog.Info(LogAreas.Settings, LogCodes.SettingsChange,
                $"Системный VPN заменён основным подключением ({_movedFromSystemVpn.Apps.Count} прил.), прежние настройки в settings.json.system-vpn.bak");
            SaveSettings();
        }
        _coreProcesses = new CoreAppProcesses(_tracker);
        _network = new LauncherNetwork(_coreProcesses, coreBackend ?? (() => new ServiceCoreBackend(ResolveCoreTag)));
        _checkConnection = checkConnection ?? (c =>
        {
            var server = ConnectionOutbound.Resolve(c).Outbound;
            return new ConnectionTester().CheckAsync(server, _network.CheckRouteFor(server));
        });
        _guard = new VpnGuard(guardBackend ?? (() => new ServiceGuardBackend()));
        _proxy = new SystemProxyWatcher(systemProxy);

        ResetDraft();
        AddConnection = new AddConnectionViewModel(() => _settings.Connections, c => AddConnectionAsync(c, makeDefault: false), _dialogs);
        RebuildConnections();

        AddAppCommand = new RelayCommand(OpenAddApps);
        ApplySettingsCommand = new RelayCommand(() => ApplySettings());
        CancelSettingsCommand = new RelayCommand(() =>
        {
            ResetDraft();
            StatusMessage = "Изменения настроек отменены";
        });
        OpenSettingsCommand = new RelayCommand(p => OpenSettings(p is string section && Enum.TryParse<SettingsSection>(section, out var s) ? s : null));
        BackCommand = new RelayCommand(() => GoBack());
        ShowSectionCommand = new RelayCommand(p =>
        {
            if (p is string section && Enum.TryParse<SettingsSection>(section, out var s))
                Section = s;
        });
        ToggleSummaryCommand = new RelayCommand(() => IsSummaryOpen = !IsSummaryOpen);
        CreateUpdateCommands();
        ShowLogCommand = new RelayCommand(() => _dialogs.ShowActivityLog(_log, this));
        CreateLogCommands();
        ShowHideDnsInfoCommand = new RelayCommand(() => _dialogs.ShowInfo(HideDnsExplanation, "Защита от утечки DNS"));

        foreach (var entry in _settings.Apps)
        {
            Apps.Add(CreateTile(entry));

            // Журнал DNS остаётся включённым между запусками, но запись начнётся, только когда приложение
            // запустят из лаунчера: окно UAC не должно появляться при каждом входе в Windows.
            if (entry.TrackDns)
                _dns.SetTracking(entry.Id, entry.Name, true);
        }

        // Перестановка плиток перетаскиванием: порядок сохраняется в settings.Apps.
        Home = new HomeBoardViewModel(_settings, Apps, SaveSettings);

        _dns.StateChanged += (_, _) => _dispatcher.BeginInvoke(RefreshDnsState);
        _dns.SourceLost += (_, _) => _dispatcher.BeginInvoke(OnDnsSourceLost);

        // Без лаунчера защита не работает: приложения закрываются вместе с ним, в том числе при его аварийном завершении.
        _tracker.LaunchCheck = CheckOutsideLaunch;
        ApplyNetworkSettings("открыт лаунчер");
        _tracker.Changed += (_, _) =>
        {
            _network.OnProcessesChanged();
            _dispatcher.BeginInvoke(() =>
            {
                RefreshRunningCounts();
                SubmitGuard();
            });
        };
        _tracker.ProcessAdded += (appId, path) => _dispatcher.BeginInvoke(() => OnAppProcessAdded(appId, path));
        _guard.Failed += error => _dispatcher.BeginInvoke(() => OnGuardFailed(error));
        _guard.Restored += () => _dispatcher.BeginInvoke(() => _log.Add(LogKind.Info, "Блокировка трафика мимо VPN снова работает"));
        _guard.Notice += notice => _dispatcher.BeginInvoke(() => _log.Add(LogKind.Info, notice));
        _proxy.Changed += () => _dispatcher.BeginInvoke(() =>
        {
            SubmitGuard();
            WarnAboutSystemProxy();
        });
        _tracker.RelaunchBlocked += (_, name) => _dispatcher.BeginInvoke(() => OnRelaunchBlocked(name));
        _tracker.OutsideLaunched += (appId, contained) => _dispatcher.BeginInvoke(() => OnOutsideLaunched(appId, contained));
        _tracker.Restarted += (appId, contained) => _dispatcher.BeginInvoke(() => OnAppRestarted(appId, contained));
        _tracker.OutsideRejected += (name, reason) => _dispatcher.BeginInvoke(() => OnOutsideRejected(name, reason));
        _network.CoreLost += (reason, report) => _dispatcher.BeginInvoke(() => OnCoreLost(reason, report));
        _network.CoreRecovered += (reason, apps) => _dispatcher.BeginInvoke(() => OnCoreRecovered(reason, apps));
        _network.ConnectionDown += info => _dispatcher.BeginInvoke(() => OnConnectionDown(info));
        _network.ConnectionUp += (_, name) => _dispatcher.BeginInvoke(() => OnConnectionUp(name));
        _network.ProbeFailed += failure => _dispatcher.BeginInvoke(() => _ = OnProbeFailedAsync(failure));
        _network.StateChanged += () => _dispatcher.BeginInvoke(RefreshRunningCounts);

        _subscriptionTimer = new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => _ = UpdateSubscriptionsAsync(), _dispatcher);

        _monitor = new VpnMonitor(BuildRule());
        _monitor.StatusChanged += OnVpnStatusChanged;
        _monitor.Start();
        ApplySnapshot(_monitor.Current);
        SubmitGuard(); // фильтры приложений с «Через лаунчер» действуют с открытия лаунчера, даже если ничего не запущено

        // Запись автозапуска указывает на эту копию лаунчера (после переустановки в другую папку — тоже).
        if (_settings.StartWithWindows)
            TrySetAutoStart(true);

        RefreshRunningCounts();
        if (!_settings.WelcomeDone && _settings.Apps.Count == 0 && _settings.Connections.Count == 0)
            StartWelcome();

        _log.Add(LogKind.Info, $"Лаунчер {UpdateService.Format(UpdateService.CurrentVersion)} запущен");
        foreach (var names in RepeatedApps())
            _log.Add(LogKind.Info, $"Одна программа добавлена несколько раз: {names}. Ядро VPN узнаёт приложение по программе, поэтому " +
                                   "её копия, открытая мимо лаунчера, пойдёт через подключение одного из них. Лишние удалите через меню плитки.");
        if (TechLog.Previous is { } previous)
            _log.Add(LogKind.Failed, $"Прошлый сеанс лаунчера завершился аварийно ({(previous.CrashUtc ?? previous.StartUtc).ToLocalTime():HH:mm}): " +
                                     (previous.CrashType ?? "лаунчер закрыли принудительно или выключилось питание"));

        // Когда окно уже показано: сообщить итог прошлого обновления и проверить, нет ли нового.
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(OnStartupIdle));
    }

    // ---------------------------------------------------------------- свойства для UI

    /// <summary>Попросить окно показать баннер-уведомление. Всегда вызывается в UI-потоке.</summary>
    public event EventHandler<ToastInfo>? ToastRequested;

    public ObservableCollection<AppTileViewModel> Apps { get; } = new();

    public ICommand AddAppCommand { get; }
    public ICommand ApplySettingsCommand { get; }
    public ICommand CancelSettingsCommand { get; }

    /// <summary>Открыть настройки; параметр — раздел (имя из <see cref="SettingsSection"/>), иначе тот, что был открыт.</summary>
    public ICommand OpenSettingsCommand { get; }

    /// <summary>«← Приложения» и Esc: вернуться к плиткам (с вопросом, если есть несохранённое).</summary>
    public ICommand BackCommand { get; }

    /// <summary>Раздел настроек в списке слева; параметр — имя из <see cref="SettingsSection"/>.</summary>
    public ICommand ShowSectionCommand { get; }

    public ICommand ToggleSummaryCommand { get; }

    /// <summary>Подключения лаунчера: раздел «Подключения» и списки выбора подключения.</summary>
    public ObservableCollection<ConnectionItemViewModel> Connections { get; } = new();

    /// <summary>Форма «Добавить подключение» в настройках.</summary>
    public AddConnectionViewModel AddConnection { get; }
    public ICommand ShowLogCommand { get; }

    /// <summary>«Как это работает и на что влияет» у «Защиты от утечки DNS».</summary>
    public ICommand ShowHideDnsInfoCommand { get; }

    /// <summary>Окно должно закрыться (лаунчер обновляется). Всегда в UI-потоке.</summary>
    public event EventHandler? ShutdownRequested;

    public bool IsVpnConnected
    {
        get => _isVpnConnected;
        private set => SetField(ref _isVpnConnected, value);
    }

    /// <summary>Вторая строка в статусе: какой VPN активен или почему VPN считается отключённым.</summary>
    public string VpnSubtitle
    {
        get => _vpnSubtitle;
        private set => SetField(ref _vpnSubtitle, value);
    }

    public string VpnDetailsText
    {
        get => _vpnDetailsText;
        private set => SetField(ref _vpnDetailsText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string RunningSummary => _tracker.Count switch
    {
        0 => "Нет запущенных приложений",
        var n => $"Под контролем: {ProcessTracker.ProcessesText(n)}"
    };

    public bool HasNoApps => Apps.Count == 0;

    /// <summary>Перестановка плиток главного экрана.</summary>
    public HomeBoardViewModel Home { get; }

    // ---------------------------------------------------------------- страницы

    public MainPage CurrentPage
    {
        get => _page;
        private set
        {
            if (!SetField(ref _page, value)) return;
            OnPropertyChanged(nameof(IsAppsPage));
            OnPropertyChanged(nameof(IsSettingsOpen));
            OnPropertyChanged(nameof(IsAppPage));
            OnPropertyChanged(nameof(IsWelcomePage));
            OnPropertyChanged(nameof(IsAddAppsPage));
            IsSummaryOpen = false;
            if (value == MainPage.Settings && Section == SettingsSection.Connections)
                AddConnection.OfferClipboard();
        }
    }

    public bool IsAppsPage => CurrentPage == MainPage.Apps;
    public bool IsAppPage => CurrentPage == MainPage.App;
    public bool IsWelcomePage => CurrentPage == MainPage.Welcome;
    public bool IsAddAppsPage => CurrentPage == MainPage.AddApps;

    /// <summary>Открыта страница настроек. Закрытие с неприменёнными изменениями спрашивает, применить ли их.</summary>
    public bool IsSettingsOpen
    {
        get => CurrentPage == MainPage.Settings;
        set
        {
            if (value)
                OpenSettings();
            else if (CurrentPage == MainPage.Settings)
                Navigate(MainPage.Apps);
            OnPropertyChanged(); // кнопка-переключатель «Настройки» в шапке должна показывать настоящее состояние
        }
    }

    /// <summary>Раздел настроек, открытый в списке слева.</summary>
    public SettingsSection Section
    {
        get => _section;
        set
        {
            if (!SetField(ref _section, value)) return;
            if (value == SettingsSection.Connections)
                AddConnection.OfferClipboard();
            if (value == SettingsSection.Log)
                _ = RefreshLogUsageAsync();
            if (value == SettingsSection.Updates)
                MarkUpdateSeen();
        }
    }

    /// <summary>Страница приложения; null — не открыта.</summary>
    public AppSettingsViewModel? AppPage
    {
        get => _appPage;
        private set => SetField(ref _appPage, value);
    }

    /// <summary>Мастер первого запуска; null — не показан.</summary>
    public WelcomeViewModel? Welcome
    {
        get => _welcome;
        private set => SetField(ref _welcome, value);
    }

    /// <summary>Страница «Добавить приложения»; null — не открыта.</summary>
    public AddAppsViewModel? AddApps
    {
        get => _addApps;
        private set => SetField(ref _addApps, value);
    }

    /// <summary>Вернуться к плиткам. false — пользователь решил остаться на странице.</summary>
    public bool GoBack() => Navigate(MainPage.Apps);

    /// <summary>Открыть настройки в нужном разделе (или в том, что был открыт).</summary>
    public void OpenSettings(SettingsSection? section = null)
    {
        if (!Navigate(MainPage.Settings))
            return;
        if (section is { } s)
            Section = s;
        else if (TakeUpdatePageJump())
            Section = SettingsSection.Updates; // обновление скачано и проверено: один раз на версию сразу к нему
    }

    private bool Navigate(MainPage target)
    {
        if (target == CurrentPage)
            return true;
        if (!TryLeavePage())
            return false;
        CurrentPage = target;
        return true;
    }

    /// <summary>Уход со страницы: несохранённое — сохранить, не сохранять или остаться.</summary>
    private bool TryLeavePage()
    {
        switch (CurrentPage)
        {
            case MainPage.Settings when HasPendingChanges:
                switch (_dialogs.AskSave("Применить изменения?", "Изменения настроек ещё не вступили в силу.", "Применить"))
                {
                    case null:
                        return false;
                    case true:
                        return ApplySettings();
                    default:
                        ResetDraft();
                        StatusMessage = "Изменения настроек не применены";
                        Toast("Изменения не применены", "Настройки остались прежними", GlyphInfo, ToastKind.Info);
                        return true;
                }

            case MainPage.App when AppPage is { } page:
                if (page.HasChanges)
                {
                    var answer = _dialogs.AskSave(page.IsNew ? $"Добавить «{page.Name}»?" : "Сохранить изменения?",
                        page.IsNew ? "Приложение ещё не добавлено в лаунчер." : $"Изменения «{page.Entry.Name}» ещё не сохранены.",
                        page.SaveText);
                    if (answer is null)
                        return false;
                    if (answer == true)
                    {
                        if (page.Validate() is { } error)
                        {
                            _dialogs.ShowWarning(error, "Не сохранено");
                            return false;
                        }

                        SaveAppPage(page, navigate: false);
                    }
                }

                CloseAppPage();
                return true;

            case MainPage.Welcome:
                FinishWelcome();
                return true;

            case MainPage.AddApps when AddApps is { } addApps:
                var chosen = addApps.List.Selected;
                if (chosen.Count > 0)
                {
                    var answer = _dialogs.AskSave("Добавить отмеченные приложения?", $"Отмечено: {chosen.Count}. Они ещё не добавлены в лаунчер.", "Добавить");
                    if (answer is null)
                        return false;
                    AddApps = null;
                    if (answer == true)
                        AddStartMenuApps(chosen);
                    return true;
                }

                AddApps = null;
                return true;

            default:
                return true;
        }
    }

    /// <summary>На странице есть изменения, которые ещё не применены.</summary>
    public bool HasPendingChanges =>
        _draftKillSwitch != _settings.KillSwitchEnabled ||
        _draftHideBehavior != _settings.HideBehavior ||
        _draftOutsideCopies != _settings.OutsideCopies ||
        _draftHideDns != _settings.HideDns ||
        DomainsDraftChanged ||
        _draftStartWithWindows != _settings.StartWithWindows ||
        HasLogDraftChanges ||
        HasUpdateDraftChanges;

    /// <summary>Подключения есть, а основное не выбрано (удалили): приложения с «Основное подключение» не запустятся.</summary>
    public bool NeedsMainConnection =>
        _settings.Connections.Count > 0 && _settings.Connections.All(c => c.Id != _settings.ConnectionId);

    public bool HasConnections => Connections.Count > 0;

    public bool KillSwitchEnabled
    {
        get => _draftKillSwitch;
        set
        {
            if (_draftKillSwitch == value) return;

            // Выключение защиты — только после явного подтверждения, чтобы не выключить её случайным кликом.
            if (!value && _settings.KillSwitchEnabled && !_dialogs.Confirm(
                    "Выключить Kill-Switch?",
                    "При обрыве VPN запущенные приложения не будут закрываться: пока VPN не вернётся, они останутся без сети. " +
                    "Запросы, которые за них делает Windows (имена сайтов, фоновые загрузки), в это время могут идти мимо VPN.",
                    "Выключить",
                    destructive: true))
            {
                // Возвращаем переключатель обратно (асинхронно, чтобы привязка успела завершить запись).
                _dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(KillSwitchEnabled)));
                return;
            }

            _draftKillSwitch = value;
            OnPropertyChanged();
            OnDraftChanged();
        }
    }

    /// <summary>Метка «Возможна утечка DNS» на главном экране, раз сам переключатель спрятан в настройках.</summary>
    public bool IsDnsLeakPossible => !_settings.HideDns;

    /// <summary>«Приложения, открытые не из лаунчера» (черновик): через лаунчер, через VPN, без интернета или напрямую.</summary>
    public OutsideCopies OutsideCopies
    {
        get => _draftOutsideCopies;
        set
        {
            if (_draftOutsideCopies == value) return;
            _draftOutsideCopies = value;
            OnPropertyChanged();
            OnDraftChanged();
        }
    }

    /// <summary>«Защита от утечки DNS» (черновик). Влияет на весь компьютер: включение — после объяснения.</summary>
    public bool HideDns
    {
        get => _draftHideDns;
        set
        {
            if (_draftHideDns == value) return;
            if (value && !_settings.HideDns && !_dialogs.Confirm("Включить защиту от утечки DNS?", HideDnsExplanation, "Включить"))
            {
                _dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(HideDns)));
                return;
            }

            _draftHideDns = value;
            OnPropertyChanged();
            OnDraftChanged();
        }
    }

    /// <summary>Что делает «Защита от утечки DNS» и на что влияет: показывается перед включением.</summary>
    internal const string HideDnsExplanation =
        "Зачем. Чтобы открыть сайт, программа сначала спрашивает его адрес по имени (DNS). В Windows эти вопросы за все " +
        "программы задаёт одна системная служба, поэтому по ним нельзя понять, какая программа спрашивает. Без этой " +
        "настройки трафик приложений идёт через VPN, но имена сайтов, которые они открывают, может видеть провайдер.\n\n" +
        "Как работает. Пока работает ядро VPN лаунчера (а оно работает, пока открыт лаунчер, если хоть одно приложение " +
        "идёт через подключение), на эти вопросы отвечает лаунчер условными адресами (198.18.x.x), а настоящий адрес " +
        "ищется при соединении: для приложений лаунчера — через VPN, для остальных программ — как обычно.\n\n" +
        "На что влияет. Это настройка на весь компьютер, а не только на приложения лаунчера:\n" +
        "• другие программы тоже получают условные адреса, пока это действует, если спрашивают их у Windows " +
        "(так делает большинство программ);\n" +
        "• команды ping и nslookup покажут адреса 198.18.x.x — это нормально.\n\n" +
        "Когда ядро выключается, лаунчер очищает кэш DNS Windows, и всё работает как без него.";

    /// <summary>Общие исключения (черновик): домены по одному в строке, как в поле на странице приложения.</summary>
    public string CommonDomainsText
    {
        get => _draftDomainsText;
        set
        {
            value ??= string.Empty;
            if (_draftDomainsText == value) return;
            _draftDomainsText = value;
            OnPropertyChanged();
            OnDraftChanged();
        }
    }

    /// <summary>Общие исключения в черновике отличаются от действующих (с учётом приведения к одному виду).</summary>
    private bool DomainsDraftChanged =>
        DomainList.Problem(_draftDomainsText) is not null ||
        !DomainList.Normalize(DomainList.Split(_draftDomainsText)).SequenceEqual(_settings.DirectDomains, StringComparer.Ordinal);

    /// <summary>«Запускать вместе с Windows» (черновик).</summary>
    public bool StartWithWindows
    {
        get => _draftStartWithWindows;
        set
        {
            if (_draftStartWithWindows == value) return;
            _draftStartWithWindows = value;
            OnPropertyChanged();
            OnDraftChanged();
        }
    }

    /// <summary>Что делает кнопка «Закрыть» в заголовке окна (черновик, вступает в силу по «Применить»).</summary>
    public HideBehavior HideBehavior
    {
        get => _draftHideBehavior;
        set
        {
            if (_draftHideBehavior == value) return;
            _draftHideBehavior = value;
            OnPropertyChanged();
            OnDraftChanged();
        }
    }

    // ---------------------------------------------------------------- применение настроек

    private void OnDraftChanged() => OnPropertyChanged(nameof(HasPendingChanges));

    /// <summary>Сбросить черновик к действующим настройкам.</summary>
    private void ResetDraft()
    {
        _draftKillSwitch = _settings.KillSwitchEnabled;
        _draftHideDns = _settings.HideDns;
        _draftStartWithWindows = _settings.StartWithWindows;
        _draftHideBehavior = _settings.HideBehavior;
        _draftOutsideCopies = _settings.OutsideCopies;
        _draftDomainsText = string.Join(Environment.NewLine, _settings.DirectDomains);
        ResetLogDraft();
        ResetUpdateDraft();

        OnPropertyChanged(nameof(KillSwitchEnabled));
        OnPropertyChanged(nameof(HideDns));
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(HideBehavior));
        OnPropertyChanged(nameof(OutsideCopies));
        OnPropertyChanged(nameof(CommonDomainsText));
        OnDraftChanged();
    }

    /// <summary>Применить черновик. false — не применено (что исправить, уже показано).</summary>
    private bool ApplySettings()
    {
        if (!HasPendingChanges) return true;

        if (HasUpdateDraftChanges && NeedsUpdateConnectionSelection)
        {
            Section = SettingsSection.Updates;
            _dialogs.ShowWarning(_settings.Connections.Count == 0
                    ? "Чтобы скачивать обновления через подключение лаунчера, сначала добавьте подключение в разделе «Подключения»."
                    : "Выберите подключение, через которое скачивать обновления.",
                "Подключение не выбрано");
            return false;
        }

        if (DomainsDraftChanged && CommonDomainsProblem() is { } domainsProblem)
        {
            Section = SettingsSection.Exclusions;
            _dialogs.ShowWarning(domainsProblem, "Исключения не сохранены");
            return false;
        }

        if (!ConfirmLogDraft())
            return false;

        var killSwitchChanged = _draftKillSwitch != _settings.KillSwitchEnabled;
        var hideDnsChanged = _draftHideDns != _settings.HideDns;
        var outsideChanged = _draftOutsideCopies != _settings.OutsideCopies;
        var domainsChanged = DomainsDraftChanged;
        var autoStartChanged = _draftStartWithWindows != _settings.StartWithWindows;
        if (autoStartChanged && !TrySetAutoStart(_draftStartWithWindows))
        {
            _draftStartWithWindows = _settings.StartWithWindows;
            OnPropertyChanged(nameof(StartWithWindows));
            autoStartChanged = false;
        }

        _settings.KillSwitchEnabled = _draftKillSwitch;
        _settings.HideDns = _draftHideDns;
        _settings.StartWithWindows = _draftStartWithWindows;
        _settings.HideBehavior = _draftHideBehavior;
        _settings.OutsideCopies = _draftOutsideCopies;
        if (domainsChanged)
        {
            _settings.DirectDomains = DomainList.Normalize(DomainList.Split(_draftDomainsText));
            _draftDomainsText = string.Join(Environment.NewLine, _settings.DirectDomains);
            OnPropertyChanged(nameof(CommonDomainsText));
        }
        ApplyLogDraft();
        ApplyUpdateDraft();
        ApplyNetworkSettings();
        UnblockWithoutKillSwitch();
        SaveSettings();

        OnPropertyChanged(nameof(IsDnsLeakPossible));
        OnDraftChanged();
        RebuildConnections();

        if (killSwitchChanged)
            _log.Add(LogKind.Info, _settings.KillSwitchEnabled ? "Kill-Switch включён" : "Kill-Switch выключен");
        if (hideDnsChanged)
            _log.Add(LogKind.Info, _settings.HideDns ? "Защита от утечки DNS включена (на весь компьютер, пока открыт лаунчер)" : "Защита от утечки DNS выключена");
        if (outsideChanged)
            _log.Add(LogKind.Info, "Приложения, открытые не из лаунчера: " + _settings.OutsideCopies switch
            {
                OutsideCopies.Block => "без интернета",
                OutsideCopies.Allow => "напрямую",
                OutsideCopies.Launcher => "через лаунчер",
                _ => "через VPN"
            });
        if (domainsChanged)
            _log.Add(LogKind.Info, _settings.DirectDomains.Count == 0
                ? "Общие исключения убраны"
                : $"Общие исключения: {_settings.DirectDomains.Count} (напрямую, мимо подключения лаунчера)");
        if (autoStartChanged)
            _log.Add(LogKind.Info, _settings.StartWithWindows ? "Лаунчер будет запускаться вместе с Windows" : "Автозапуск лаунчера выключен");

        if (killSwitchChanged)
        {
            var on = _settings.KillSwitchEnabled;
            StatusMessage = on
                ? "Kill-Switch включён: при обрыве VPN запущенные приложения будут закрыты"
                : "Kill-Switch выключен: при обрыве VPN приложения продолжат работу";
            Toast(on ? "Kill-Switch включён" : "Kill-Switch выключен",
                on ? "Приложения закроются при обрыве VPN" : "Приложения не будут закрываться при обрыве VPN",
                GlyphPower, on ? ToastKind.Success : ToastKind.Danger);
        }
        else
        {
            StatusMessage = "Настройки применены";
            Toast("Настройки применены", DescribeMain(), GlyphCheck, ToastKind.Success);
        }

        // «Защита от утечки DNS» и копии мимо лаунчера действуют сразу, и для работающих приложений:
        // ядро перезапускается (ApplyNetworkSettings → KeepCoreRunning), фильтры Windows меняются.
        RefreshRunningCounts();

        return true;
    }

    /// <summary>
    /// Что не так с общими исключениями в черновике: не домены или вместе со своими исключениями какого-то приложения
    /// («Дополнять общие») их больше, чем примет служба. null — можно применять.
    /// </summary>
    private string? CommonDomainsProblem()
    {
        if (DomainList.Problem(_draftDomainsText) is { } problem)
            return problem;

        var common = DomainList.Normalize(DomainList.Split(_draftDomainsText));
        if (common.Count > DomainList.MaxPerApp)
            return $"Исключений больше {DomainList.MaxPerApp}: столько ядро не примет. Уберите лишние.";
        var crowded = _settings.Apps.FirstOrDefault(a => a.Network.DomainsMode == DomainsMode.AddToCommon &&
                                                         DomainList.Normalize(common.Concat(a.Network.DirectDomains)).Count > DomainList.MaxPerApp);
        return crowded is null
            ? null
            : $"Вместе с исключениями приложения «{crowded.Name}» получается больше {DomainList.MaxPerApp}: столько ядро не примет. " +
              "Уберите лишние здесь или у приложения.";
    }

    /// <summary>Основное подключение словами: «Основное подключение «Дом»».</summary>
    private string DescribeMain() => _settings.Connections.FirstOrDefault(c => c.Id == _settings.ConnectionId) is { } main
        ? $"Основное подключение «{main.Name}»"
        : "Основное подключение не выбрано";

    /// <summary>Работающие приложения, которых касается изменение.</summary>
    private List<string> AffectedRunningApps(Func<AppEntry, bool> affected)
    {
        // Запущенные разово без сети настройки сети не касаются.
        var running = new HashSet<Guid>(_tracker.RunningApps());
        running.ExceptWith(_offlineRuns);
        return _settings.Apps.Where(a => running.Contains(a.Id) && affected(a)).Select(a => a.Name).ToList();
    }

    /// <summary>Сетевые условия приложения с учётом разового запуска без сети.</summary>
    private NetworkPlan PlanFor(AppEntry entry) => NetworkPlan.For(entry, _settings, _offlineRuns.Contains(entry.Id));

    // ---------------------------------------------------------------- VPN и Kill-Switch

    /// <summary>Вызывается в фоновом потоке монитора.</summary>
    private void OnVpnStatusChanged(object? sender, VpnStatusChangedEventArgs e)
    {
        // Сеть Windows изменилась: серверы подключений лаунчера проверяются вне очереди, а ядро, которое не заметило
        // вернувшуюся сеть, перезапускается.
        _network.CheckSoon();

        // У каждого приложения под системным VPN своё правило адаптеров и своя защита. Монитор присылает снимок
        // при любом изменении адаптеров, поэтому каждое правило проверяется и до, и после изменения.
        var lost = new List<AdapterApp>();
        foreach (var app in _adapterApps)
        {
            var now = VpnMonitor.Apply(app.Rule, e.Current.Adapters).IsConnected;
            var before = e.Previous is null ? now : VpnMonitor.Apply(app.Rule, e.Previous.Adapters).IsConnected;
            if (now)
                _tracker.StopBlocking(app.Id); // VPN снова есть: закрытое приложение можно запускать
            else if (before)
                lost.Add(app);
        }

        // Убиваем процессы прямо здесь, в фоновом потоке, не дожидаясь UI:
        // окно может быть занято (модальный диалог и т.п.), а реагировать надо немедленно.
        var close = lost.Where(a => a.CloseOnDrop).Select(a => a.Id).ToList();
        var report = close.Count > 0 ? _tracker.KillApps(close, blockRelaunch: true) : null;
        var unprotected = lost.Where(a => !a.CloseOnDrop && _tracker.CountFor(a.Id) > 0).Select(a => a.Name).ToList();

        _dispatcher.BeginInvoke(() =>
        {
            ApplySnapshot(e.Current);
            SubmitGuard(); // адаптеры VPN сменились: фильтры пускают приложения только через те, что работают сейчас

            if (e.JustDisconnected && _adapterApps.Count > 0 || report is not null || unprotected.Count > 0)
                OnVpnLost(report, unprotected);
            else if (e.Previous is { IsConnected: false } && e.Current.IsConnected)
            {
                StatusMessage = $"{DateTime.Now:HH:mm:ss} — VPN подключён";
                Toast("VPN подключён", DescribeActive(e.Current), GlyphLock, ToastKind.Success);
                _log.Add(LogKind.VpnUp, $"VPN подключён: {DescribeActive(e.Current)}");
            }
        });
    }

    /// <param name="report">Что закрыл Kill-Switch; null — закрывать было некого.</param>
    /// <param name="unprotected">Работающие приложения без «Закрывать при обрыве»: ждут VPN без сети.</param>
    private void OnVpnLost(KillReport? report, IReadOnlyList<string> unprotected)
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        string subtitle;

        if (report is { Killed.Count: > 0 } or { Failed.Count: > 0 })
            subtitle = (report.Killed.Count > 0 ? "Kill-Switch закрыл: " + string.Join(", ", report.Killed) : "Kill-Switch не смог закрыть приложения") +
                       (report.Failed.Count > 0 ? " · не закрылись: " + string.Join(", ", report.Failed) : string.Empty);
        else if (unprotected.Count > 0)
            subtitle = "Ждут VPN без сети: " + string.Join(", ", unprotected);
        else
            subtitle = "Запущенных приложений не было";

        StatusMessage = $"{time} — VPN отключён. {subtitle}";
        Toast("VPN отключён", subtitle, GlyphUnlock, ToastKind.Danger);

        _log.Add(LogKind.VpnDown, $"VPN отключён: {_monitor.Current.Explanation}" +
                                       (unprotected.Count > 0
                                           ? " · не закрывались (Kill-Switch выключен): " + string.Join(", ", unprotected) +
                                             " — сеть для них закрыта, пока VPN не вернётся"
                                           : string.Empty));
        if (report is { Killed.Count: > 0 })
            _log.Add(LogKind.Closed, $"Kill-Switch закрыл: {string.Join(", ", report.Killed)}");
        if (report is { Failed.Count: > 0 })
            _log.Add(LogKind.Failed, $"Не удалось закрыть, ждут VPN без сети: {string.Join(", ", report.Failed)}");

        // Отдельный диалог — только если что-то закрыть не удалось: это требует действий пользователя.
        if (report is { Failed.Count: > 0 })
        {
            var text = "Эти приложения не удалось закрыть. Пока VPN не вернётся, они без сети. Закройте их вручную:\n\n" + string.Join("\n", report.Failed);
            if (report.Killed.Count > 0)
                text += "\n\nЗакрыто: " + string.Join(", ", report.Killed);
            _dialogs.ShowWarning(text, "Не все приложения закрыты");
        }
    }

    /// <summary>Ядро VPN упало или пропала служба: приложения через подключения лаунчера уже закрыты.</summary>
    private void OnCoreLost(string reason, KillReport report)
    {
        var parts = new List<string>();
        if (report.Killed.Count > 0)
            parts.Add("Закрыты: " + string.Join(", ", report.Killed));
        if (report.Failed.Count > 0)
            parts.Add("Не закрылись: " + string.Join(", ", report.Failed));
        // Закрывать было некого: ядро было нужно копиям приложений, открытым не из лаунчера.
        var subtitle = parts.Count > 0 ? string.Join(" · ", parts) : "Приложения, открытые не из лаунчера, идут напрямую";
        StatusMessage = $"{DateTime.Now:HH:mm:ss} — {reason} {subtitle}";
        Toast("Подключение лаунчера выключилось", subtitle, GlyphUnlock, ToastKind.Danger, "Выгрузить журналы", () => ShowExport(ExportPeriods.Last15));
        _log.Add(LogKind.VpnDown, reason);
        if (report.Killed.Count > 0)
            _log.Add(LogKind.Closed, $"Закрыты без подключения: {string.Join(", ", report.Killed)}");
        ShowFailedKills(report);
        RefreshRunningCounts();
    }

    /// <summary>Ядро упало и запущено заново: приложения были приостановлены и продолжили работу.</summary>
    private void OnCoreRecovered(string reason, IReadOnlyList<string> apps)
    {
        var subtitle = "Продолжили работу: " + string.Join(", ", apps);
        StatusMessage = $"{DateTime.Now:HH:mm:ss} — {reason} Ядро запущено заново. {subtitle}";
        Toast("Ядро VPN перезапущено после сбоя", subtitle, GlyphLock, ToastKind.Info, "Выгрузить журналы", () => ShowExport(ExportPeriods.Last15));
        _log.Add(LogKind.VpnDown, reason);
        _log.Add(LogKind.VpnUp, $"Ядро VPN запущено заново, приложения продолжили работу: {string.Join(", ", apps)}");
        RefreshRunningCounts();
    }

    /// <summary>
    /// Компьютер вышел из сна: сеть могла смениться, пока он спал. Серверы подключений лаунчера проверяются вне очереди.
    /// Любой поток.
    /// </summary>
    public void OnResumedFromSleep()
    {
        TechLog.Info(LogAreas.Monitor, LogCodes.PowerResume, "Компьютер вышел из сна: подключения проверяются вне очереди");
        _network.CheckSoon();
    }

    /// <summary>
    /// Перед запуском сервер не ответил через ядро. В журнал — что сказало ядро и итог отдельной проверки того же
    /// подключения (без адаптера лаунчера): так видно, виноват сервер или работа через адаптер. Если причина на самом
    /// компьютере (нет сети, ядро её не видит), отдельная проверка не нужна: её соединения идут через ту же сеть.
    /// </summary>
    private async Task OnProbeFailedAsync(ProbeFailure failure)
    {
        // Строки ядра пишет технический журнал (LauncherNetwork), в Журнале событий только понятный итог.
        _log.Add(LogKind.Failed, failure.Summary);
        if (failure.LocalCause is { } local)
        {
            _log.Add(LogKind.Info, $"Сервер не проверялся отдельно: {local}");
            return;
        }

        if (_settings.Connections.FirstOrDefault(c => c.Id == failure.ConnectionId) is not { } connection)
            return;
        var check = await CheckConnectionAsync(connection);
        _log.Add(check.Ok ? LogKind.Info : LogKind.Failed, check.Ok
            ? $"Отдельная проверка «{connection.Name}» без адаптера лаунчера: работает ({check.LatencyMs} мс{(check.Country is { } country ? " · " + country : "")})"
            : $"Отдельная проверка «{connection.Name}» без адаптера лаунчера: не работает — {check.Error}");
    }

    /// <summary>Сервер подключения не отвечает: закрытые и ждущие сеть приложения.</summary>
    private void OnConnectionDown(ConnectionDownInfo info)
    {
        var parts = new List<string>();
        if (info.Closed.Killed.Count > 0)
            parts.Add("Закрыты: " + string.Join(", ", info.Closed.Killed));
        if (info.Closed.Failed.Count > 0)
            parts.Add("Не закрылись: " + string.Join(", ", info.Closed.Failed));
        if (info.Waiting.Count > 0)
            parts.Add("Ждут сеть: " + string.Join(", ", info.Waiting));
        var subtitle = parts.Count > 0 ? string.Join(" · ", parts) : "Запущенных приложений не было";

        if (info.NoNetwork)
        {
            StatusMessage = $"{DateTime.Now:HH:mm:ss} — компьютер не подключён к сети, сервер «{info.ConnectionName}» недоступен. {subtitle}";
            Toast("Компьютер не подключён к сети", subtitle, GlyphUnlock, ToastKind.Danger);
            _log.Add(LogKind.VpnDown, $"Компьютер не подключён к сети: сервер подключения «{info.ConnectionName}» недоступен · {subtitle}");
        }
        else
        {
            StatusMessage = $"{DateTime.Now:HH:mm:ss} — сервер «{info.ConnectionName}» не отвечает. {subtitle}";
            Toast($"Сервер «{info.ConnectionName}» не отвечает", subtitle, GlyphUnlock, ToastKind.Danger, "Выгрузить журналы", () => ShowExport(ExportPeriods.Last15));
            _log.Add(LogKind.VpnDown, $"Сервер подключения «{info.ConnectionName}» не отвечает · {subtitle}");
        }

        ShowFailedKills(info.Closed);
        RefreshRunningCounts();
    }

    private void OnConnectionUp(string connectionName)
    {
        StatusMessage = $"{DateTime.Now:HH:mm:ss} — сервер «{connectionName}» снова отвечает";
        Toast("Подключение восстановлено", $"Сервер «{connectionName}» снова отвечает", GlyphLock, ToastKind.Success);
        _log.Add(LogKind.VpnUp, $"Сервер подключения «{connectionName}» снова отвечает");
        RefreshRunningCounts();
    }

    private void ShowFailedKills(KillReport report)
    {
        if (report.Failed.Count == 0)
            return;
        _log.Add(LogKind.Failed, $"Не удалось закрыть: {string.Join(", ", report.Failed)}");
        _dialogs.ShowWarning("Эти приложения не удалось закрыть. Сети у них нет, но лучше закройте их вручную:\n\n" + string.Join("\n", report.Failed),
            "Не все приложения закрыты");
    }

    /// <summary>
    /// Сетевые настройки в силу: у каждого приложения своя защита (закрывать ли вместе с лаунчером, идёт ли оно
    /// через ядро), план ядра и правила адаптеров для монитора. Вызывается при запуске и после изменения настроек.
    /// </summary>
    /// <param name="released">Приложение, которое убрали из лаунчера, пока оно работало (см. LauncherNetwork.KeepRunningAsync).</param>
    private void ApplyNetworkSettings(string trigger = "изменились настройки", Guid? released = null)
    {
        var previous = _adapterApps;
        var adapterApps = new List<AdapterApp>();
        _offlineRuns.IntersectWith(_settings.Apps.Select(a => a.Id));
        _coreProcesses.SetOffline(_offlineRuns);
        var plans = _settings.Apps.Select(entry => (Entry: entry, Plan: PlanFor(entry))).ToList();
        // Условия запуска «Через лаунчер» известны раньше, чем трекер начнёт брать копии под контроль.
        _outsideLaunches = plans.Where(p => p.Plan.OutsideCopies == OutsideCopies.Launcher)
            .ToDictionary(p => p.Entry.Id, p => new OutsideLaunch(p.Plan.Kind, p.Plan.AdapterRule, p.Plan.Connection?.Name));
        foreach (var (entry, plan) in plans)
        {
            // Без лаунчера нет ни ядра, ни фильтров Windows: приложение закрывается вместе с ним, в том числе при его сбое.
            _tracker.SetPolicy(entry, plan.UsesLauncherCore, killOnClose: true, adoptOutside: plan.OutsideCopies == OutsideCopies.Launcher);
            if (plan.Kind == NetworkPlanKind.SystemAdapters)
                adapterApps.Add(new AdapterApp(entry.Id, entry.Name, plan.AdapterRule!, plan.CloseOnDrop));
        }

        _adapterApps = adapterApps;
        CheckRuleChange(previous, adapterApps);
        var ipv6 = CorePlan.DetectIpv6();
        if (ipv6 != _lastIpv6)
        {
            _lastIpv6 = ipv6;
            TechLog.Info(LogAreas.Network, LogCodes.Ipv6Detect, ipv6 ? "IPv6 в Windows есть: ядро получит адрес IPv6" : "IPv6 в Windows нет");
        }
        _network.Update(CorePlan.Build(_settings, ipv6));
        SubmitGuard();
        KeepCoreRunning(trigger, released);
    }

    /// <summary>
    /// Правило VPN поменяли (общее или у приложения), а адаптеры прежние, поэтому монитор ничего не пришлёт. Работающее
    /// приложение, которому по новому правилу VPN не подключён, обрывается так же, как при отключении VPN (и приложение,
    /// которое раньше шло не через системный VPN: оно работало с сетью). Приложение, которому VPN теперь хватает, снова можно
    /// запускать.
    /// </summary>
    private void CheckRuleChange(IReadOnlyList<AdapterApp> previous, IReadOnlyList<AdapterApp> current)
    {
        if (current.Count == 0 || _monitor is null)
            return; // при открытии лаунчера монитора ещё нет, а приложения не запущены

        var adapters = _monitor.Current.Adapters;
        var before = previous.ToDictionary(a => a.Id);
        var lost = new List<AdapterApp>();
        foreach (var app in current)
        {
            if (VpnMonitor.Apply(app.Rule, adapters).IsConnected)
            {
                _tracker.StopBlocking(app.Id);
                continue;
            }

            var was = !before.TryGetValue(app.Id, out var old) || VpnMonitor.Apply(old.Rule, adapters).IsConnected;
            if (was && _tracker.CountFor(app.Id) > 0)
                lost.Add(app);
        }

        if (lost.Count == 0)
            return;
        var close = lost.Where(a => a.CloseOnDrop).Select(a => a.Id).ToList();
        var report = close.Count > 0 ? _tracker.KillApps(close, blockRelaunch: true) : new KillReport(Array.Empty<string>(), Array.Empty<string>());
        var waiting = lost.Where(a => !a.CloseOnDrop).Select(a => a.Name).ToList();
        TechLog.Info(LogAreas.KillSwitch, LogCodes.KsClose, $"По новому правилу VPN не подключён: закрыты {(report.Killed.Count > 0 ? string.Join(", ", report.Killed) : "никто")}" +
            (report.Failed.Count > 0 ? $", не удалось закрыть {string.Join(", ", report.Failed)}" : string.Empty) +
            (waiting.Count > 0 ? $", ждут VPN без сети {string.Join(", ", waiting)}" : string.Empty));
        _dispatcher.BeginInvoke(() => OnRuleLost(report, waiting));
    }

    private void OnRuleLost(KillReport report, IReadOnlyList<string> waiting)
    {
        var parts = new List<string>();
        if (report.Killed.Count > 0)
            parts.Add("Kill-Switch закрыл: " + string.Join(", ", report.Killed));
        if (report.Failed.Count > 0)
            parts.Add("Не закрылись: " + string.Join(", ", report.Failed));
        if (waiting.Count > 0)
            parts.Add("Ждут VPN без сети: " + string.Join(", ", waiting));
        var subtitle = parts.Count > 0 ? string.Join(" · ", parts) : "Запущенных приложений не было";

        StatusMessage = $"{DateTime.Now:HH:mm:ss} — по новому правилу VPN не подключён. {subtitle}";
        Toast("По новому правилу VPN не подключён", subtitle, GlyphUnlock, ToastKind.Danger);
        _log.Add(LogKind.VpnDown, "По новому правилу VPN не подключён · " + subtitle);
        ShowFailedKills(report);
        RefreshRunningCounts();
    }

    /// <summary>
    /// Пока в настройках есть приложения через подключение лаунчера, ядро работает всё время, пока открыт лаунчер: тогда
    /// и копия такого приложения, открытая по ярлыку, сразу идёт как выбрано в «Приложения, открытые не из лаунчера».
    /// Ядро не запустилось — лаунчер работает дальше, причина в журнале и в уведомлении (одно на одну и ту же причину).
    /// </summary>
    private async void KeepCoreRunning(string trigger, Guid? released)
    {
        string? error;
        try
        {
            error = await _network.KeepRunningAsync(trigger, released: released is { } app ? new[] { app } : null);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error is null || error == _keepCoreError)
        {
            _keepCoreError = error;
            return;
        }

        _keepCoreError = error;
        _log.Add(LogKind.Failed, "Ядро VPN не запустилось: " + error + ". Приложения, открытые не из лаунчера, пока идут напрямую. " +
                                 "Приложения из лаунчера запускаются как обычно: ядро включится при запуске.");
        // При открытии лаунчера ошибка бывает известна ещё в конструкторе, до того как окно начнёт показывать уведомления.
        _ = _dispatcher.BeginInvoke(() =>
            Toast("Ядро VPN не запустилось", "Приложения, открытые не из лаунчера, идут напрямую. Подробности в журнале.", GlyphUnlock, ToastKind.Danger));
    }

    // ---------------------------------------------------------------- блокировка трафика мимо VPN

    /// <summary>
    /// Пока приложение готовилось к запуску (подключение, проверка сервера, фильтры), его условия запуска могли поменять или
    /// удалить его подключение. Запускать по прежней подготовке нельзя: ядро и фильтры готовили другое. true — не запускаем,
    /// пользователь предупреждён.
    /// </summary>
    private bool LaunchPlanChanged(AppEntry entry, NetworkPlan prepared)
    {
        var plan = PlanFor(entry);
        if (plan.Kind == prepared.Kind && plan.Connection?.Id == prepared.Connection?.Id && plan.Kind != NetworkPlanKind.MissingConnection)
            return false;

        TechLog.Warn(LogAreas.App, LogCodes.AppReject, $"«{entry.Name}» не запущено: пока шла подготовка, изменились его условия запуска или подключение");
        _dialogs.ShowWarning(plan.Kind == NetworkPlanKind.MissingConnection
                ? $"«{entry.Name}» не запущено: у него больше нет подключения. Выберите подключение в настройках приложения."
                : $"«{entry.Name}» не запущено: пока оно запускалось, у него изменились условия запуска. Запустите его снова.",
            "Приложение не запущено");
        return true;
    }

    /// <summary>
    /// Какие фильтры должны действовать сейчас: работающие (и запускаемые) приложения пускаются в сеть только через свой
    /// VPN — адаптер ядра или адаптеры системного VPN по их правилу (VPN не подключён — никуда). Приложения с «Через лаунчер» —
    /// всё время, пока открыт лаунчер: копия, открытая не из лаунчера, до того как он возьмёт её под контроль, выходит
    /// в сеть только через VPN приложения. Только в UI-потоке.
    /// </summary>
    private GuardRequest BuildGuardRequest()
    {
        var request = new GuardRequest();
        if (_monitor is null)
            return request;

        var adapters = _monitor.Current.Adapters;
        var running = new HashSet<Guid>(_tracker.RunningApps());
        running.UnionWith(_guardPending);
        var dns = new SortedSet<int>();
        foreach (var entry in _settings.Apps.Where(a => running.Contains(a.Id) || _tracker.AdoptsCopies(a.Id)))
        {
            var plan = PlanFor(entry);
            if (plan.Kind is NetworkPlanKind.Offline or NetworkPlanKind.MissingConnection)
            {
                // Ни адаптера ядра, ни адаптеров VPN. Системный прокси другой VPN-программы тоже закрыт: через него трафик ушёл бы в интернет.
                // Без подключения (его удалили или сменили условие, пока приложение работало или запускалось) — тоже никуда.
                if (GuardPaths(entry) is { Count: > 0 } offlinePaths)
                    request.Apps.Add(new GuardRule { Paths = offlinePaths, LoopbackPorts = _proxy.Current?.Ports.ToList() });
                continue;
            }

            if (plan.Kind == NetworkPlanKind.LauncherConnection)
            {
                if (GuardPaths(entry) is { Count: > 0 } paths)
                    request.Apps.Add(new GuardRule { Paths = paths, ViaCore = true, LoopbackPorts = ViaLocalProxy(plan) ? null : _proxy.Current?.Ports.ToList() });
                continue;
            }

            if (plan.Kind != NetworkPlanKind.SystemAdapters)
                continue;
            var snapshot = VpnMonitor.Apply(plan.AdapterRule!, adapters);
            var vpn = snapshot.IsConnected
                ? adapters.Where(a => a.IsUp && a.Index > 0 && snapshot.ActiveVpnNames.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).ToList()
                : new List<VpnAdapterInfo>();
            if (GuardPaths(entry) is { Count: > 0 } appPaths)
                request.Apps.Add(new GuardRule { Paths = appPaths, Interfaces = vpn.Select(a => a.Index).Distinct().ToList() });
            if (_settings.HideDns && running.Contains(entry.Id))
                dns.UnionWith(vpn.Where(a => a.HasDns).Select(a => a.Index));
        }

        if (dns.Count > 0)
            request.DnsInterfaces = dns.ToList();

        return request;
    }

    /// <summary>
    /// Программы приложения: его exe, работающие процессы и замеченные раньше за этот сеанс. До первого запуска известны и
    /// программа приложения Store (из его пакета), и программа, которую запускает Squirrel-ярлык (Update.exe → Discord.exe):
    /// иначе первые секунды после запуска они шли бы без фильтра. Пути настоящие (<see cref="GuardPath"/>): служба работает
    /// в другом сеансе Windows и дисков subst и сетевых дисков пользователя не видит. Только существующие .exe: другой файл
    /// (например, временный установщик обновления в задании приложения) служба не примет, и тогда не применился бы весь набор.
    /// </summary>
    private List<string> GuardPaths(AppEntry entry)
    {
        // Сначала программы самого приложения: если программ больше, чем примет служба, отбрасываются замеченные давно.
        var paths = new List<string>();
        if (entry.IsPackaged)
        {
            if (PackagedApps.ExecutablePath(entry.AppUserModelId) is { } packaged)
                paths.Add(packaged);
        }
        else if (!string.IsNullOrWhiteSpace(entry.ExePath) && File.Exists(entry.ExePath))
        {
            paths.Add(entry.ExePath);
            if (AppFamily.SquirrelTarget(entry) is { } target)
                paths.Add(target);
        }

        paths.AddRange(_tracker.ImagePaths(entry.Id));
        if (_guardPaths.TryGetValue(entry.Id, out var seen))
            paths.AddRange(seen);
        return paths.Where(IsProgram).Select(GuardPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxGuardPaths).ToList();
    }

    /// <summary>Сколько программ одного приложения принимает служба.</summary>
    private const int MaxGuardPaths = 256;

    private static bool IsProgram(string path)
    {
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        // Сетевой путь служба проверит сама (и не ограничит): обращение к недоступному серверу подвесило бы окно.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        try
        {
            if (Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network)
                return true;
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string GuardPath(string path)
    {
        // Сетевой путь как есть: служба его всё равно не проверит, а обращение к недоступному серверу подвесило бы окно.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return path;
        try
        {
            if (Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network)
                return path;
        }
        catch (ArgumentException)
        {
            return path;
        }

        return AppFamily.GetFinalPath(path) ?? path;
    }

    /// <summary>Обновить фильтры в фоне. Только в UI-потоке.</summary>
    private void SubmitGuard()
    {
        if (_guard is null)
            return; // ещё в конструкторе
        try
        {
            _guard.Submit(BuildGuardRequest());
        }
        catch (ObjectDisposedException)
        {
            // лаунчер закрывается
        }
    }

    /// <summary>У приложения новый процесс: если это новая программа, фильтр должен закрыть и её.</summary>
    private void OnAppProcessAdded(Guid appId, string path)
    {
        if (!_guardPaths.TryGetValue(appId, out var seen))
            _guardPaths[appId] = seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(path))
            SubmitGuard();
    }

    /// <summary>
    /// Подключение приложения — прокси другой программы на этом компьютере (v2rayN и т. п.): системный прокси, скорее
    /// всего, ведёт в ту же программу, и закрывать его незачем.
    /// </summary>
    private static bool ViaLocalProxy(NetworkPlan plan) => plan.Connection is { Kind: ConnectionKind.LocalProxy };

    /// <summary>
    /// В Windows включён прокси на этом компьютере, а приложения идут через подключение лаунчера: браузеры и многие
    /// программы отдали бы трафик ему, то есть другой VPN-программе. Путь к нему закрыт фильтрами, лаунчер только
    /// сообщает об этом. Один раз на каждый новый прокси. Только в UI-потоке.
    /// </summary>
    private void WarnAboutSystemProxy()
    {
        var proxy = _proxy.Current;
        if (proxy is null)
        {
            _warnedProxy = null;
            return;
        }

        var running = new HashSet<Guid>(_tracker.RunningApps());
        if (!_settings.Apps.Any(a => running.Contains(a.Id) && PlanFor(a) is { Kind: NetworkPlanKind.LauncherConnection } plan && !ViaLocalProxy(plan)) ||
            _warnedProxy == proxy.Display)
            return;
        _warnedProxy = proxy.Display;

        _log.Add(LogKind.Info, $"В Windows включён системный прокси {proxy.Display} — обычно его включает другая VPN-программа. " +
                               "Приложениям, которые идут через подключение лаунчера, путь к нему закрыт: иначе их трафик ушёл бы мимо подключения. " +
                               "Если такое приложение не открывает сайты, выключите системный прокси в той программе.");
        Toast("Системный прокси закрыт для приложений", $"Прокси {proxy.Display} ведёт мимо подключения. Если сайты не открываются, выключите его в той VPN-программе.",
            GlyphLock, ToastKind.Info);
    }

    private void OnGuardFailed(string error)
    {
        _log.Add(LogKind.Failed, "Блокировка трафика мимо VPN не работает: " + error);
        Toast("Блокировка мимо VPN не работает", error, GlyphUnlock, ToastKind.Danger);
    }

    /// <summary>Включить или выключить автозапуск. false — не вышло (причина показана).</summary>
    private bool TrySetAutoStart(bool enabled)
    {
        try
        {
            AutoStart.Set(enabled);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _dialogs.ShowWarning("Не удалось изменить автозапуск лаунчера: " + ex.Message, "Автозапуск");
            return false;
        }
    }

    /// <summary>
    /// Ответ ядру «чей процесс»: метка приложения через подключение лаунчера, иначе null (напрямую). Процесс вне задания, который
    /// лаунчер отнёс к приложению (<see cref="ProcessTracker.TryGetKin"/>): помощник копии, открытой не из лаунчера, или
    /// программа того же издателя в другой папке получает метку копий, а процесс, запущенный приложением под контролем, но
    /// вне его задания, — метку приложения. Любой поток.
    /// </summary>
    private string? ResolveCoreTag(uint pid)
    {
        if (_tracker.TryGetRoutedOwner((int)pid, out var appId))
            return _network.TagFor(appId);
        if (!_tracker.TryGetKin((int)pid, out appId, out var launched))
            return null;
        return launched ? _network.TagFor(appId) : _network.CopyTagFor(appId);
    }

    /// <summary>Приложения, которые закроются вместе с лаунчером: все работающие (фильтры Windows и ядро действуют, только пока он работает).</summary>
    private List<Guid> AppsClosedOnExit()
    {
        var running = new HashSet<Guid>(_tracker.RunningApps());
        return _settings.Apps.Where(a => running.Contains(a.Id)).Select(a => a.Id).ToList();
    }

    // ---------------------------------------------------------------- подписки

    /// <summary>Обновить подписки, которым пора (раз в сутки, или как задано у подписки).</summary>
    private async Task UpdateSubscriptionsAsync()
    {
        if (_isUpdatingSubscriptions)
            return;
        _isUpdatingSubscriptions = true;
        try
        {
            foreach (var connection in _settings.Connections.Where(c => SubscriptionUpdater.IsDue(c, DateTimeOffset.Now)).ToList())
                await UpdateSubscriptionAsync(connection, userInitiated: false);
        }
        finally
        {
            _isUpdatingSubscriptions = false;
        }
    }

    /// <summary>Скачать подписку. Ошибку при автообновлении пишем в журнал, при запуске приложения — показываем.</summary>
    private async Task UpdateSubscriptionAsync(VpnConnection connection, bool userInitiated)
    {
        try
        {
            var selectedBefore = connection.SelectedServer;
            var content = await Subscriptions.FetchAsync(connection.SubscriptionUrl ?? string.Empty);
            if (!_settings.Connections.Contains(connection))
                return; // пока качали, подключение удалили
            var changed = SubscriptionUpdater.Apply(connection, content, DateTimeOffset.Now);
            // Смена выбранного сервера меняет настройки ядра: ядро перезапустится при следующем запуске приложения.
            TechLog.Info(LogAreas.Subs, LogCodes.SubsUpdate,
                $"Подписка «{connection.Name}» [{CorePlan.ConnectionTag(connection.Id)}] загружена{(userInitiated ? " перед запуском" : string.Empty)}: " +
                $"серверов {content.Links.Count}, пропущено {content.Skipped}, список {(changed ? "изменился" : "тот же")}, " +
                $"выбранный сервер {(connection.SelectedServer == selectedBefore ? "тот же" : "сменился")}");
            SaveSettings();
            // Строка подключения в настройках показывает список серверов и время обновления: и при автообновлении тоже.
            Connections.FirstOrDefault(c => ReferenceEquals(c.Connection, connection))?.Refresh();
            if (changed)
            {
                ApplyNetworkSettings();
                RebuildConnections();
                _log.Add(LogKind.Info, $"Подписка «{connection.Name}» обновлена: серверов {content.Links.Count}" +
                                       (content.Skipped > 0 ? $", пропущено {content.Skipped}" : string.Empty));
            }
        }
        catch (ConnectionParseException ex)
        {
            TechLog.Warn(LogAreas.Subs, LogCodes.SubsFail, $"Подписка «{connection.Name}» не загрузилась: {ex.Message}");
            _log.Add(LogKind.Failed, $"Не удалось обновить подписку «{connection.Name}»: {ex.Message}");
            if (userInitiated)
                _dialogs.ShowWarning(ex.Message, $"Подписка «{connection.Name}» не загрузилась");
        }
    }

    /// <summary>Закрытое Kill-Switch приложение запустилось снова без VPN и было закрыто.</summary>
    private void OnRelaunchBlocked(string name)
    {
        StatusMessage = $"{DateTime.Now:HH:mm:ss} — «{name}» запустилось без VPN и закрыто";
        Toast("Запуск без VPN заблокирован", $"Kill-Switch закрыл «{name}»", GlyphPower, ToastKind.Danger);
        _log.Add(LogKind.Blocked, $"«{name}» запустилось без VPN и сразу закрыто");
    }

    // ---------------------------------------------------------------- «Через лаунчер»

    /// <summary>
    /// «Через лаунчер»: можно ли сейчас запустить приложение, копию которого открыли не из лаунчера. Те же условия, что
    /// у запуска с плитки: VPN по правилу приложения подключён или его подключение работает и сервер не перестал отвечать.
    /// null — можно, иначе причина. Поток наблюдения трекера.
    /// </summary>
    private string? CheckOutsideLaunch(Guid appId)
    {
        if (!_outsideLaunches.TryGetValue(appId, out var app))
            return "условие запуска не известно";

        switch (app.Kind)
        {
            case NetworkPlanKind.Offline:
                return null;

            case NetworkPlanKind.SystemAdapters:
                return _monitor is { } monitor && VpnMonitor.Apply(app.Rule!, monitor.Current.Adapters).IsConnected ? null : "VPN не подключён";

            case NetworkPlanKind.LauncherConnection:
                return _network.StateOf(appId) switch
                {
                    CoreAppState.Protected => null,
                    CoreAppState.Waiting => $"сервер подключения «{app.ConnectionName}» не отвечает",
                    _ => "подключение лаунчера не работает"
                };

            default:
                return "у приложения не выбрано подключение";
        }
    }

    /// <summary>
    /// «Через лаунчер»: приложение не работало, его копию открыли не из лаунчера, и лаунчер взял её под контроль, как запуск
    /// с плитки. Только в UI-потоке.
    /// </summary>
    private void OnOutsideLaunched(Guid appId, bool contained)
    {
        if (_settings.Apps.FirstOrDefault(a => a.Id == appId) is not { } entry)
            return;

        // Как и при запуске с плитки: программу, которую служба не смогла ограничить фильтрами (сетевой диск), не запускаем.
        if (!entry.IsPackaged && _guard.IsUnguarded(GuardPath(entry.ExePath)))
        {
            var report = _tracker.Kill(appId);
            _log.Add(LogKind.Blocked, $"«{entry.Name}» открыто не из лаунчера и закрыто: программа на сетевом диске или недоступна службе лаунчера, " +
                                      $"блокировку трафика мимо VPN для неё не включить ({entry.ExePath})");
            Toast("Запуск заблокирован", $"«{entry.Name}» открыто не из лаунчера и закрыто: блокировку трафика мимо VPN для него не включить",
                GlyphPower, ToastKind.Danger);
            ShowFailedKills(report);
            RefreshRunningCounts();
            return;
        }

        _dns.NoteLaunch(entry.Id);
        var plan = PlanFor(entry);
        var offline = plan.Kind == NetworkPlanKind.Offline;
        var via = plan.UsesLauncherCore ? $" через «{plan.Connection?.Name}»" : offline ? " без сети" : string.Empty;
        TechLog.Info(LogAreas.App, LogCodes.AppStarted, $"«{entry.Name}» открыто не из лаунчера и взято под контроль: {DescribeLaunch(entry)}");
        StatusMessage = $"{DateTime.Now:HH:mm:ss} — запущено не из лаунчера: {entry.Name}{via}";
        Toast($"Запущено: {entry.Name}",
            "Открыто не из лаунчера, " + (offline ? "без сети"
                : plan.UsesLauncherCore ? $"через подключение «{plan.Connection?.Name}»"
                : !plan.CloseOnDrop ? "Kill-Switch выключен"
                : "под защитой Kill-Switch"),
            GlyphPlay, ToastKind.Info);
        _log.Add(LogKind.Launch, $"Запущено не из лаунчера: {entry.Name}{via}" + (contained ? string.Empty : " · контроль неполный"));
        if (plan.UsesLauncherCore)
            WarnAboutSystemProxy();
        CloseIfLostDuringLaunch(entry, plan);
    }

    /// <summary>
    /// Приложение перезапустилось само (например, приложение Microsoft Store после обновления), и лаунчер взял новую
    /// программу под контроль как тот же запуск. Только в UI-потоке.
    /// </summary>
    private void OnAppRestarted(Guid appId, bool contained)
    {
        if (_settings.Apps.FirstOrDefault(a => a.Id == appId) is not { } entry)
            return;

        _dns.NoteLaunch(entry.Id);
        var plan = PlanFor(entry);
        var via = plan.UsesLauncherCore ? $" через «{plan.Connection?.Name}»" : plan.Kind == NetworkPlanKind.Offline ? " без сети" : string.Empty;
        TechLog.Info(LogAreas.App, LogCodes.AppStarted, $"«{entry.Name}» перезапустилось само и осталось под контролем: {DescribeLaunch(entry)}");
        _log.Add(LogKind.Launch, $"Перезапустилось само (например, после обновления) и осталось под защитой: {entry.Name}{via}" +
                                 (contained ? string.Empty : " · контроль неполный"));
        CloseIfLostDuringLaunch(entry, plan);
    }

    /// <summary>«Через лаунчер»: копию открыли не из лаунчера, когда приложение запускать нельзя, и она закрыта.</summary>
    private void OnOutsideRejected(string name, string reason)
    {
        StatusMessage = $"{DateTime.Now:HH:mm:ss} — «{name}» открыто не из лаунчера и закрыто: {reason}";
        Toast("Запуск заблокирован", $"«{name}» открыто не из лаунчера и закрыто: {reason}", GlyphPower, ToastKind.Danger);
        _log.Add(LogKind.Blocked, $"«{name}» открыто не из лаунчера и сразу закрыто: {reason}");
    }

    private void Toast(string title, string subtitle, string glyph, ToastKind kind, string? actionText = null, Action? action = null) =>
        ToastRequested?.Invoke(this, new ToastInfo(title, subtitle, glyph, kind, actionText, action));

    /// <summary>Понятные названия активных VPN, например «OpenVPN (TAP)».</summary>
    private static string DescribeActive(VpnSnapshot snapshot)
    {
        var names = snapshot.ActiveVpnNames
            .Select(n => snapshot.Adapters.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))?.Product ?? n)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count > 0 ? string.Join(", ", names) : "VPN активен";
    }

    private void ApplySnapshot(VpnSnapshot snapshot)
    {
        IsVpnConnected = snapshot.IsConnected;
        VpnSubtitle = snapshot.IsConnected ? DescribeActive(snapshot) : snapshot.Explanation;
        VpnDetailsText = snapshot.ActiveVpnNames.Count > 0
            ? $"{snapshot.Explanation}: {string.Join(", ", snapshot.ActiveVpnNames)}"
            : snapshot.Explanation;
        RefreshStatus();
    }

    private VpnRule BuildRule() => new(_settings.RuleMode, _settings.SelectedAdapters);

    // ---------------------------------------------------------------- приложения

    private AppTileViewModel CreateTile(AppEntry entry)
    {
        // Squirrel-программа могла обновиться и переехать в новую папку, пока лаунчер был закрыт.
        if (!entry.IsPackaged && !File.Exists(entry.ExePath))
            TryRelocate(entry);

        AppTileViewModel? tile = null;
        tile = new AppTileViewModel(
            entry,
            LoadIcon(entry),
            new RelayCommand(() => Launch(tile!)),
            new RelayCommand(() => Remove(tile!)),
            new RelayCommand(() => OpenLocation(tile!)),
            new RelayCommand(() => ToggleDns(tile!)),
            new RelayCommand(() => OpenDnsJournal(tile!)),
            new RelayCommand(() => Kill(tile!)),
            new RelayCommand(() => RestartProtected(tile!)),
            new RelayCommand(() => OpenAppSettings(tile!)),
            new RelayCommand(() => LaunchOffline(tile!)));
        tile.RunningCount = _tracker.CountFor(entry.Id);
        return tile;
    }

    /// <summary>Иконка из .exe (чётче), а для приложений Microsoft Store — та, что Проводник рисует на ярлыке.</summary>
    private static System.Windows.Media.ImageSource? LoadIcon(AppEntry entry)
    {
        if (!entry.IsPackaged && File.Exists(entry.ExePath) && IconExtractor.TryGetIcon(entry.ExePath) is { } exeIcon)
            return exeIcon;

        return entry.ShortcutPath is { } lnk && File.Exists(lnk) ? IconExtractor.TryGetShortcutIcon(lnk) : null;
    }

    /// <summary>
    /// «Добавить приложение»: программы из меню «Пуск» с галочками (уже добавленные отмечены и недоступны)
    /// и «Выбрать в проводнике» для одной программы.
    /// </summary>
    private void OpenAddApps()
    {
        if (CurrentPage == MainPage.AddApps || !Navigate(MainPage.Apps))
            return;

        var list = new StartMenuListViewModel(_findStartMenuApps, exe => IconExtractor.TryGetIcon(exe), IsAdded);
        AddApps = new AddAppsViewModel(list,
            add: apps =>
            {
                AddApps = null;
                AddStartMenuApps(apps);
                GoBack();
            },
            browse: BrowseForApp,
            cancel: () => GoBack());
        CurrentPage = MainPage.AddApps;
        _ = list.LoadAsync();
    }

    /// <summary>Программа из меню «Пуск» уже есть в лаунчере (тот же exe-файл).</summary>
    private bool IsAdded(StartMenuApp app) =>
        _settings.Apps.Any(a => !a.IsPackaged && string.Equals(a.ExePath, app.ExePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Приложение лаунчера с той же программой: тот же exe-файл или та же программа через Squirrel-ярлык.</summary>
    private AppEntry? FindSameProgram(string exePath, string? arguments)
    {
        var family = AppFamily.For(new AppEntry { ExePath = exePath, Arguments = arguments });
        return _settings.Apps.FirstOrDefault(a => !a.IsPackaged &&
                                                  (string.Equals(a.ExePath, exePath, StringComparison.OrdinalIgnoreCase) ||
                                                   family is not null && AppFamily.For(a) is { } other && family.Overlaps(other)));
    }

    /// <summary>«Выбрать в проводнике»: одна программа, и сразу её страница (условие запуска, защита).</summary>
    private void BrowseForApp()
    {
        var path = _dialogs.PickExecutableOrShortcut();
        if (path is null) return;
        AddFromPaths(new[] { path });
    }

    /// <summary>
    /// Файлы, перетащенные в окно (или программа, выбранная в проводнике в «Добавить приложение»). Один — открываем его страницу, чтобы
    /// сразу выбрать условие запуска; несколько — добавляем с основным подключением, настроить можно потом.
    /// </summary>
    public void AddFromPaths(IEnumerable<string> paths)
    {
        var list = paths
            .Where(p => Path.GetExtension(p) is var ext
                        && (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0)
        {
            _dialogs.ShowWarning("Перетащите программу (.exe) или её ярлык (.lnk).", "Не программа");
            return;
        }

        if (list.Count == 1)
        {
            if (BuildEntry(list[0], quiet: false) is { } entry)
                OpenAppPage(entry, isNew: true, LoadIcon(entry));
            return;
        }

        var added = new List<string>();
        foreach (var path in list)
        {
            if (BuildEntry(path, quiet: true) is not { } entry)
                continue;
            _settings.Apps.Add(entry);
            Apps.Add(CreateTile(entry));
            added.Add(entry.Name);
            _log.Add(LogKind.Info, $"Добавлено приложение «{entry.Name}»");
        }

        if (added.Count == 0)
        {
            _dialogs.ShowWarning("Эти программы уже есть в списке или их файлы не удалось прочитать.", "Ничего не добавлено");
            return;
        }

        SaveSettings();
        ApplyNetworkSettings();
        OnPropertyChanged(nameof(HasNoApps));
        Toast(added.Count == 1 ? "Приложение добавлено" : $"Добавлено приложений: {added.Count}",
            string.Join(", ", added), GlyphAdd, ToastKind.Info);
        RebuildConnections(); // подписи «Основное · …» у подключений
        RefreshRunningCounts();

        // Перетащили на страницу «Добавить приложения»: её список уже устарел, возвращаемся к плиткам.
        if (CurrentPage == MainPage.AddApps)
            GoBack();
    }

    /// <summary>
    /// Запись приложения по пути к .exe или ярлыку. null — не программа, не найдена или уже в списке
    /// (при quiet=false причину объясняем окном).
    /// </summary>
    private AppEntry? BuildEntry(string path, bool quiet)
    {
        void Fail(string message, bool warning = false)
        {
            if (quiet) return;
            if (warning) _dialogs.ShowWarning(message, "Уже добавлено");
            else _dialogs.ShowError(message);
        }

        string exePath;
        string? arguments = null;
        string? workingDirectory = null;
        string? appUserModelId = null;
        string name;

        if (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            ShortcutTarget target;
            try
            {
                target = ShortcutResolver.Resolve(path);
            }
            catch (Exception ex)
            {
                Fail($"Не удалось прочитать ярлык:\n{path}\n\n{ex.Message}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(target.TargetPath) && target.AppUserModelId is null)
            {
                Fail("Не удалось понять, что запускает этот ярлык: он не указывает ни на программу, " +
                     "ни на приложение Microsoft Store. Выберите сам .exe.");
                return null;
            }

            exePath = target.TargetPath;
            appUserModelId = target.AppUserModelId;
            arguments = string.IsNullOrWhiteSpace(target.Arguments) ? null : target.Arguments;
            workingDirectory = string.IsNullOrWhiteSpace(target.WorkingDirectory) ? null : target.WorkingDirectory;
            name = Path.GetFileNameWithoutExtension(path);
        }
        else
        {
            exePath = path;
            name = GetFriendlyName(path);
        }

        if (appUserModelId is null && !string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            Fail($"Ярлык указывает не на программу (.exe):\n{exePath}");
            return null;
        }

        if (appUserModelId is null && !File.Exists(exePath))
        {
            Fail($"Файл программы не найден:\n{exePath}");
            return null;
        }

        // Одну программу дважды не добавить даже с другими параметрами запуска: ядро и фильтры Windows узнают приложение
        // по exe-файлу, и копия, открытая мимо лаунчера, пошла бы через подключение одного из них наугад.
        var same = appUserModelId is not null
            ? _settings.Apps.FirstOrDefault(a => string.Equals(a.AppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase))
            : FindSameProgram(exePath, arguments);
        if (same is not null)
        {
            Fail(appUserModelId is null && !string.Equals(same.Arguments ?? "", arguments ?? "", StringComparison.Ordinal)
                ? $"Эта программа уже добавлена: «{same.Name}». Одну программу можно добавить только один раз, даже с другими параметрами запуска."
                : $"Эта программа уже добавлена: «{same.Name}».", warning: true);
            return null;
        }

        return new AppEntry
        {
            Name = name,
            ExePath = exePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            SourcePath = path,
            AppUserModelId = appUserModelId
        };
    }

    /// <summary>
    /// Одна программа, добавленная несколько раз (до 2.3.1 так было можно с другими параметрами запуска): названия
    /// приложений по каждой такой программе. Их не удаляем, а при каждом запуске лаунчера напоминаем убрать лишние.
    /// </summary>
    private List<string> RepeatedApps() =>
        _settings.Apps
            .GroupBy(a => a.IsPackaged ? "store " + a.AppUserModelId : a.ExePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(", ", g.Select(a => $"«{a.Name}»")))
            .ToList();

    private static string GetFriendlyName(string exePath)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(exePath).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) && description.Length <= 40)
                return description;
        }
        catch
        {
            // берём имя файла
        }

        return Path.GetFileNameWithoutExtension(exePath);
    }

    /// <summary>
    /// Запуск по клику на плитке. Порядок: условие запуска (VPN есть или подключение лаунчера включено и сервер отвечает)
    /// → регистрация приложения (задание Windows, метка для ядра) → запуск программы.
    /// </summary>
    /// <param name="dnsReady">Журнал DNS уже запущен (или запустить не вышло) — второй заход после StartDnsThenLaunch.</param>
    private async void Launch(AppTileViewModel tile, bool dnsReady = false)
    {
        await LaunchAsync(tile, dnsReady);
        EndOfflineRuns(); // запуск без сети не удался
    }

    private async Task LaunchAsync(AppTileViewModel tile, bool dnsReady)
    {
        var entry = tile.Entry;
        if (_launching.Contains(entry.Id))
            return;
        if (_updateHandoff)
        {
            _dialogs.ShowWarning("Устанавливается обновление лаунчера: приложение можно будет запустить после него.");
            return;
        }

        // Все строки этого запуска (лаунчер, служба, ядро) с одним id операции. Второй заход после журнала DNS продолжает тот же.
        using var operation = dnsReady && TechLog.CurrentCid is not null ? null : TechLog.Begin(out _);
        var started = Stopwatch.StartNew();
        if (!dnsReady)
            TechLog.Info(LogAreas.App, LogCodes.AppLaunch, $"Запуск «{entry.Name}»: {DescribeLaunch(entry)}");
        _launching.Add(entry.Id);

        NetworkPlan prepared;
        try
        {
            // Приложение добавлено ярлыком: запускаем то, на что ярлык указывает сейчас. Путь уточняется до подготовки сети:
            // по нему ядро узнаёт копии приложения мимо лаунчера, а фильтры Windows — саму программу.
            RefreshFromShortcut(entry);

            if (!entry.IsPackaged && !File.Exists(entry.ExePath) && !TryRelocate(entry))
            {
                TechLog.Warn(LogAreas.App, LogCodes.AppReject, $"«{entry.Name}» не запущено: файл программы не найден ({entry.ExePath})");
                _dialogs.ShowError($"Файл программы не найден:\n{entry.ExePath}");
                return;
            }

            prepared = PlanFor(entry);
            if (!await PrepareNetworkAsync(tile))
                return;
        }
        finally
        {
            _launching.Remove(entry.Id);
            tile.LaunchStage = null;
        }

        if (!Apps.Contains(tile))
            return; // пока подключались, приложение убрали
        if (LaunchPlanChanged(entry, prepared))
            return;

        // Журнал DNS включён, но запись ещё не идёт: сначала запускаем её, чтобы не пропустить первые запросы.
        if (!dnsReady && entry.TrackDns && !_dns.IsActive)
        {
            StartDnsThenLaunch(tile);
            return;
        }

        var plan = PlanFor(entry);
        var offline = plan.Kind == NetworkPlanKind.Offline;
        var guardName = offline ? "запрет сети" : "блокировку трафика мимо VPN";

        // Фильтры ставятся до запуска: с первого соединения программа не выйдет в сеть мимо VPN.
        // Не поставились — приложение не запускается (защита важнее).
        {
            if (!_launching.Add(entry.Id))
                return;
            string? guardError;
            try
            {
                tile.LaunchStage = "Включаю защиту…";
                // Системный прокси читается в фоне (PAC-файл — по сети): сразу после старта лаунчера дожидаемся первого
                // чтения, чтобы путь к нему закрылся до запуска.
                await _proxy.WaitFirstReadAsync(TimeSpan.FromSeconds(3));
                _guardPending.Add(entry.Id);
                guardError = await _guard.ApplyNowAsync(BuildGuardRequest());
            }
            finally
            {
                _launching.Remove(entry.Id);
                tile.LaunchStage = null;
            }

            if (guardError is not null)
            {
                _guardPending.Remove(entry.Id);
                SubmitGuard();
                _log.Add(LogKind.Blocked, $"Запуск «{entry.Name}» отклонён: не удалось включить {guardName} — {guardError}");
                _dialogs.ShowWarning($"«{entry.Name}» не запущено: не удалось включить {guardName}.\n\n{guardError}\n\n" +
                                     "Если ошибка повторяется, переустановите лаунчер.",
                    "Защита не включилась");
                return;
            }

            if (!Apps.Contains(tile))
            {
                _guardPending.Remove(entry.Id);
                SubmitGuard();
                return;
            }

            // Служба не смогла проверить программу (сетевой диск, нет доступа) и не ограничила её: без фильтров не запускаем.
            if (!entry.IsPackaged && _guard.IsUnguarded(GuardPath(entry.ExePath)))
            {
                _guardPending.Remove(entry.Id);
                SubmitGuard();
                _log.Add(LogKind.Blocked, $"Запуск «{entry.Name}» отклонён: программа на сетевом диске или недоступна службе лаунчера, " +
                                          $"{guardName} для неё не включить ({entry.ExePath})");
                _dialogs.ShowWarning($"«{entry.Name}» не запущено: программа лежит на сетевом диске или недоступна службе лаунчера, " +
                                     $"поэтому {guardName} для неё включить нельзя.\n\n{entry.ExePath}\n\n" +
                                     "Скопируйте программу на этот компьютер и добавьте её в лаунчер оттуда.",
                    "Защита не включилась");
                return;
            }

            if (LaunchPlanChanged(entry, prepared))
            {
                _guardPending.Remove(entry.Id);
                SubmitGuard();
                return;
            }
        }

        _dns.NoteLaunch(entry.Id);

        LaunchResult result;
        try
        {
            // Программа запускается внутри задания Windows: под контролем окажутся и все процессы,
            // которые она породит, даже если сама она сразу завершится (как Update.exe у Discord).
            // Приложения Microsoft Store запускает сама Windows, их процесс забирается в задание сразу после старта.
            // Переменные прокси убираются у всех, кому нужен VPN: иначе программа отдала бы трафик прокси.
            result = entry.IsPackaged ? _tracker.LaunchPackaged(entry) : _tracker.Launch(entry, withoutProxy: true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            StatusMessage = $"Запуск «{entry.Name}» отменён";
            TechLog.Info(LogAreas.App, LogCodes.AppReject, $"Запуск «{entry.Name}» отменен в окне UAC");
            return;
        }
        catch (Exception ex)
        {
            _log.Add(LogKind.Failed, $"Не удалось запустить «{entry.Name}»: {ex.Message}");
            TechLog.Error(LogAreas.App, LogCodes.AppReject, $"«{entry.Name}» не запущено: {ex.GetType().Name}: {ex.Message}");
            _dialogs.ShowError($"Не удалось запустить «{entry.Name}»:\n{ex.Message}");
            return;
        }
        finally
        {
            // Процессы уже под контролем (или запуск не удался): дальше фильтры строятся по ним.
            if (_guardPending.Remove(entry.Id))
                SubmitGuard();
        }

        TechLog.Info(LogAreas.App, LogCodes.AppStarted,
            $"«{entry.Name}» запущено за {started.ElapsedMilliseconds} мс: PID {result.ProcessId}, в задании {(result.Contained ? "да" : "нет")}, " +
            $"права администратора {(result.Elevated ? "да" : "нет")}, взято под контроль уже работавших {result.Adopted}");
        if (result.OpenedBefore)
        {
            _log.Add(LogKind.Blocked, $"«{entry.Name}» уже было открыто не из лаунчера: Windows показала его, а не запустила заново");
            _dialogs.ShowWarning($"«{entry.Name}» уже было открыто не из лаунчера, и Windows показала его, а не запустила заново. " +
                                 "Его соединения идут так, как выбрано в «Если открыто не из лаунчера».\n\n" +
                                 $"Закройте «{entry.Name}» полностью и запустите его из лаунчера снова.",
                "Приложение уже открыто");
            return;
        }

        if (result.ProcessId == 0)
        {
            _log.Add(LogKind.Failed, $"«{entry.Name}» запущена, но без контроля: Windows не вернула её процесс");
            _dialogs.ShowWarning($"«{entry.Name}» запущена, но Windows не вернула её процесс, поэтому Kill-Switch не сможет её закрыть.");
            return;
        }

        var via = plan.UsesLauncherCore ? $"через «{plan.Connection?.Name}»" : offline ? "без сети" : null;
        StatusMessage = $"{DateTime.Now:HH:mm:ss} — запущено: {entry.Name} (PID {result.ProcessId})" + (via is null ? string.Empty : " " + via) +
                        (result.Adopted > 0 ? $", взято под контроль уже работавших: {ProcessTracker.ProcessesText(result.Adopted)}" : string.Empty);
        Toast($"Запущено: {entry.Name}",
            offline ? "Без сети: только связь внутри компьютера"
            : plan.UsesLauncherCore ? $"Через подключение «{plan.Connection?.Name}»"
            : !plan.CloseOnDrop ? "Kill-Switch выключен"
            : result.Adopted > 0 ? "Уже открытое приложение тоже под защитой"
            : "Под защитой Kill-Switch",
            GlyphPlay, ToastKind.Info);
        _log.Add(LogKind.Launch, $"Запущено: {entry.Name}" + (via is null ? string.Empty : " " + via) +
                                      (result.Adopted > 0 ? $" · взято под контроль уже работавших: {ProcessTracker.ProcessesText(result.Adopted)}" : string.Empty) +
                                      (result.Contained ? string.Empty : result.Elevated ? " · с правами администратора, контроль неполный" : " · контроль неполный"));
        if (plan.UsesLauncherCore)
            WarnAboutSystemProxy(); // важнее сообщения о запуске: показывается поверх него

        if (!result.Contained && (plan.CloseOnDrop || plan.UsesLauncherCore) && _warnedUncontained.Add(entry.Id))
        {
            _dialogs.ShowWarning(plan.UsesLauncherCore
                    ? $"«{entry.Name}» работает с правами администратора, а лаунчер — без них. Сама программа идёт через подключение, " +
                      "но программы, которые она запустит, могут пойти напрямую, и Kill-Switch может не суметь их закрыть.\n\n" +
                      "Чтобы защита работала надёжно, запускайте лаунчер от имени администратора."
                    : result.Elevated
                    ? $"«{entry.Name}» работает с правами администратора, а лаунчер — без них. Kill-Switch попробует закрыть её " +
                      "при обрыве VPN, но Windows может не разрешить это, и дочерние процессы могут остаться.\n\n" +
                      "Чтобы защита работала надёжно, запускайте лаунчер от имени администратора."
                    : $"Windows не позволила поместить «{entry.Name}» в задание лаунчера. Kill-Switch закроет сам процесс, " +
                      "но процессы, которые он запустит, могут остаться.",
                "Неполный контроль");
        }

        CloseIfLostDuringLaunch(entry, plan);
    }

    /// <summary>
    /// Закрываем окно гонки: VPN мог упасть между проверкой и стартом процесса, и тогда событие монитора уже отработало,
    /// когда приложения ещё не было в списке.
    /// </summary>
    private void CloseIfLostDuringLaunch(AppEntry entry, NetworkPlan plan)
    {
        if (plan.Kind == NetworkPlanKind.SystemAdapters && plan.CloseOnDrop &&
            !VpnMonitor.Apply(plan.AdapterRule!, _monitor.Evaluate().Adapters).IsConnected)
        {
            var report = _tracker.KillApps(new[] { entry.Id }, blockRelaunch: true);
            OnVpnLost(report, Array.Empty<string>());
        }
        else if (plan.UsesLauncherCore && !_network.IsCoreRunning)
        {
            // Ядро упало, пока программа запускалась: событие о падении могло прийти раньше, чем она появилась.
            var report = _tracker.KillApps(new[] { entry.Id }, blockRelaunch: true);
            OnCoreLost("Ядро VPN остановилось во время запуска.", report);
        }
    }

    /// <summary>
    /// Условие запуска. Под системным VPN — VPN по правилу приложения подключён. Через подключение лаунчера — ядро
    /// работает с текущими настройками, сервер отвечает, копий мимо лаунчера нет (или пользователь согласился их закрыть).
    /// false — запускать нельзя, причина уже показана.
    /// </summary>
    private async Task<bool> PrepareNetworkAsync(AppTileViewModel tile)
    {
        var entry = tile.Entry;
        var plan = PlanFor(entry);
        if (plan.Kind == NetworkPlanKind.Offline)
            return true; // VPN не нужен

        if (plan.Kind == NetworkPlanKind.SystemAdapters)
        {
            // Статус проверяем заново прямо сейчас, а не берём из кэша.
            if (!VpnMonitor.Apply(plan.AdapterRule!, _monitor.Evaluate().Adapters).IsConnected)
            {
                _log.Add(LogKind.Blocked, $"Запуск «{entry.Name}» отклонён: VPN не подключён");
                _dialogs.ShowWarning("Для запуска этого приложения необходимо подключиться к VPN.", "Нет подключения к VPN");
                return false;
            }

            return await CloseOutsideCopiesAsync(entry, viaCore: false);
        }

        // Подписку ещё ни разу не скачали: без списка серверов подключиться не к чему.
        if (plan.Connection is { Kind: ConnectionKind.Subscription, SubscriptionServers.Count: 0 } subscription)
        {
            tile.LaunchStage = "Загружаю подписку…";
            await UpdateSubscriptionAsync(subscription, userInitiated: true);
        }

        ApplyNetworkSettings();

        tile.LaunchStage = "Подключаю…";
        string? error;
        try
        {
            error = await _network.PrepareLaunchAsync(entry.Id);
        }
        catch (Exception ex)
        {
            // Непредвиденная ошибка связи с ядром: не роняем лаунчер, а не запускаем приложение.
            error = "Не удалось включить подключение: " + ex.Message;
        }

        if (error is not null)
        {
            _log.Add(LogKind.Blocked, $"Запуск «{entry.Name}» отклонён: {error}");
            if (_dialogs.ShowWarningWithLogs(error, "Не удалось подключиться"))
                ShowExport(ExportPeriods.Last15);
            return false;
        }

        // Копию мимо лаунчера закрываем, только когда ясно, что приложение запустится: иначе пользователь остался бы
        // и без копии, и без приложения.
        if (!await CloseOutsideCopiesAsync(entry, viaCore: true))
        {
            _network.OnProcessesChanged(); // ядро включали ради этого запуска: выключится, если больше не нужно
            return false;
        }

        tile.LaunchStage = "Запускаю…";
        return true;
    }

    /// <summary>
    /// Копия, запущенная мимо лаунчера, не под его защитой, а новая копия обычно просто передаёт ей управление. Её
    /// предлагается закрыть и запустить приложение под защитой. У приложения под системным VPN такая копия — браузер,
    /// открытый по ссылке другим приложением лаунчера: остальные копии лаунчер при запуске берёт под контроль сам.
    /// false — не запускаем (пользователь отказался или копию закрыть не удалось).
    /// </summary>
    private async Task<bool> CloseOutsideCopiesAsync(AppEntry entry, bool viaCore)
    {
        if (_tracker.RefreshOutside(entry.Id) == 0)
            return true;

        var traffic = !viaCore
            ? "Эта копия открыта другим приложением (например, по ссылке), и Kill-Switch за ней не следит."
            : _network.OutsideBlocked(entry.Id) switch
            {
                true => "Эта копия запущена мимо лаунчера, и у неё нет интернета.",
                false => "Эта копия запущена мимо лаунчера: её соединения идут через подключение приложения, но Kill-Switch за ней не следит.",
                null => "Эта копия запущена мимо лаунчера, и её соединения идут напрямую."
            };
        if (!_dialogs.Confirm($"«{entry.Name}» уже запущено без защиты",
                traffic + " Закрыть её и запустить под защитой? Несохранённые данные в ней пропадут.",
                "Перезапустить под защитой"))
            return false;

        var failed = _tracker.KillOutside(entry.Id);
        if (failed.Count > 0)
        {
            _dialogs.ShowWarning("Не удалось закрыть копию, запущенную мимо лаунчера. Закройте её вручную:\n\n" + string.Join("\n", failed),
                "Копия не закрыта");
            return false;
        }

        _log.Add(LogKind.Closed, $"Закрыта копия «{entry.Name}», запущенная мимо лаунчера");
        await Task.Delay(500); // дать Windows освободить файлы и порты закрытой копии
        return true;
    }

    /// <summary>
    /// Перечитывает ярлык, из которого добавлено приложение. Если ярлык удалён или не читается,
    /// остаётся то, что было сохранено при прошлом запуске.
    /// </summary>
    private void RefreshFromShortcut(AppEntry entry)
    {
        if (entry.ShortcutPath is not { } lnk || !File.Exists(lnk))
            return;

        ShortcutTarget target;
        try
        {
            target = ShortcutResolver.Resolve(lnk);
        }
        catch (Exception)
        {
            return;
        }

        string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

        if (target.AppUserModelId is not null)
        {
            if (string.Equals(entry.AppUserModelId, target.AppUserModelId, StringComparison.Ordinal) &&
                entry.Arguments == Clean(target.Arguments))
                return;

            entry.AppUserModelId = target.AppUserModelId;
            entry.ExePath = string.Empty;
            entry.Arguments = Clean(target.Arguments);
            entry.WorkingDirectory = null;
        }
        else if (string.Equals(Path.GetExtension(target.TargetPath), ".exe", StringComparison.OrdinalIgnoreCase) &&
                 File.Exists(target.TargetPath))
        {
            if (!entry.IsPackaged &&
                string.Equals(entry.ExePath, target.TargetPath, StringComparison.OrdinalIgnoreCase) &&
                entry.Arguments == Clean(target.Arguments) &&
                entry.WorkingDirectory == Clean(target.WorkingDirectory))
                return;

            entry.AppUserModelId = null;
            entry.ExePath = target.TargetPath;
            entry.Arguments = Clean(target.Arguments);
            entry.WorkingDirectory = Clean(target.WorkingDirectory);
        }
        else
        {
            return; // ярлык ведёт непонятно куда: запускаем сохранённое
        }

        SaveSettings();
        ApplyNetworkSettings("ярлык приложения ведёт в другое место"); // копии мимо лаунчера узнаются по новому пути
    }

    /// <summary>
    /// Squirrel-программы (Discord, Slack…) при обновлении переезжают из app-1.0.9 в app-1.0.10, а старую папку удаляют.
    /// Если выбранный .exe пропал по этой причине, находим новую версию и запоминаем путь.
    /// </summary>
    private bool TryRelocate(AppEntry entry)
    {
        var current = AppFamily.FindCurrentSquirrelExe(entry.ExePath);
        if (current is null)
            return false;

        var oldDir = Path.GetDirectoryName(entry.ExePath)?.TrimEnd('\\');
        if (entry.WorkingDirectory is { } workDir &&
            string.Equals(workDir.TrimEnd('\\'), oldDir, StringComparison.OrdinalIgnoreCase))
            entry.WorkingDirectory = Path.GetDirectoryName(current);

        entry.ExePath = current;
        SaveSettings();
        ApplyNetworkSettings("программа переехала в папку новой версии"); // копии мимо лаунчера узнаются по новому пути
        return true;
    }

    private void Remove(AppTileViewModel tile)
    {
        var id = tile.Entry.Id;
        if (_launching.Contains(id) || _dnsLaunching.Contains(id))
        {
            _dialogs.ShowWarning($"«{tile.Name}» сейчас запускается. Уберите его, когда запуск закончится.", "Приложение запускается");
            return;
        }

        // Убранное приложение — обычная программа: работающее продолжает работать, но уже напрямую и без Kill-Switch.
        var running = _tracker.CountFor(id) > 0;
        if (!_dialogs.Confirm($"Убрать «{tile.Name}»?",
                "Приложение пропадёт из лаунчера. Сама программа удалена не будет." +
                (running ? " Сейчас оно запущено и продолжит работать как обычная программа: напрямую, без защиты лаунчера." : string.Empty),
                "Убрать", destructive: true))
            return;

        // Ядро по прежним настройкам ещё ведёт соединения работающего приложения через VPN: его настройки ядра меняются сразу.
        running = _tracker.CountFor(id) > 0;
        _settings.Apps.Remove(tile.Entry);
        _tracker.Forget(id);
        _guardPaths.Remove(id);
        SaveSettings();
        ApplyNetworkSettings("приложение убрано из лаунчера", released: running ? id : null);
        _log.Add(LogKind.Info, $"Убрано из лаунчера: «{tile.Name}»" + (running ? " (продолжает работать как обычная программа)" : string.Empty));
        _dialogs.CloseDnsJournal(tile.Entry.Id);
        _dns.Forget(tile.Entry.Id);
        Apps.Remove(tile);
        OnPropertyChanged(nameof(HasNoApps));
        RebuildConnections(); // подписи «Основное · …» у подключений
    }

    /// <summary>«Завершить все процессы»: всё, что лаунчер контролирует у приложения, закрывается сразу.</summary>
    private void Kill(AppTileViewModel tile)
    {
        if (!_dialogs.Confirm($"Завершить «{tile.Name}»?",
                "Все процессы приложения будут закрыты сразу, как при срабатывании Kill-Switch. Несохранённые данные в нём пропадут.",
                "Завершить", destructive: true))
            return;

        var report = _tracker.Kill(tile.Entry.Id);
        RefreshRunningCounts();

        if (report.Killed.Count > 0)
        {
            StatusMessage = $"{DateTime.Now:HH:mm:ss} — завершено: {string.Join(", ", report.Killed)}";
            _log.Add(LogKind.Closed, $"Завершено вручную: {string.Join(", ", report.Killed)}");
            Toast("Приложение завершено", report.Killed[0], GlyphStop, ToastKind.Info);
        }
        else if (report.Failed.Count == 0)
        {
            Toast("Нечего завершать", $"«{tile.Name}» уже не работает", GlyphStop, ToastKind.Info);
        }

        if (report.Failed.Count > 0)
        {
            _log.Add(LogKind.Failed, $"Не удалось завершить: {string.Join(", ", report.Failed)}");
            _dialogs.ShowWarning("Эти процессы закрыть не удалось. Закройте их вручную:\n\n" + string.Join("\n", report.Failed),
                "Не все процессы завершены");
        }
    }

    private void OpenLocation(AppTileViewModel tile)
    {
        try
        {
            // У приложений Microsoft Store нет доступной папки с .exe: показываем ярлык.
            var entry = tile.Entry;
            var target = !entry.IsPackaged && File.Exists(entry.ExePath) ? entry.ExePath : entry.ShortcutPath ?? entry.ExePath;
            Process.Start("explorer.exe", $"/select,\"{target}\"");
        }
        catch (Exception ex)
        {
            _dialogs.ShowError(ex.Message);
        }
    }

    private void RefreshRunningCounts()
    {
        EndOfflineRuns();
        foreach (var tile in Apps)
        {
            var id = tile.Entry.Id;
            tile.RunningCount = _tracker.CountFor(id);
            tile.NetState = _offlineRuns.Contains(id) ? CoreAppState.None : _network.StateOf(id);

            var outside = _tracker.OutsideFor(id);
            var blocked = outside > 0 ? _network.OutsideBlocked(id) : null;
            if (outside > 0 && tile.OutsideCount == 0)
            {
                Toast(blocked is null ? $"«{tile.Name}» работает без защиты" : $"«{tile.Name}» запущено мимо лаунчера",
                    blocked switch { true => "Без интернета", false => "Через подключение приложения", null => "Запущено мимо лаунчера" } +
                    " · «Перезапустить под защитой» в меню плитки",
                    GlyphUnlock, ToastKind.Danger);
                _log.Add(LogKind.Blocked, $"«{tile.Name}» запущено мимо лаунчера: " + blocked switch
                {
                    true => "у этой копии нет интернета",
                    false => "её соединения идут через подключение приложения",
                    null => "его соединения идут напрямую"
                });
            }

            tile.OutsideBlocked = blocked;
            tile.OutsideCount = outside;
        }

        OnPropertyChanged(nameof(RunningSummary));
        RefreshStatus();
    }

    /// <summary>«Перезапустить под защитой»: закрыть копию мимо лаунчера и запустить приложение из лаунчера.</summary>
    private void RestartProtected(AppTileViewModel tile) => Launch(tile);

    /// <summary>
    /// «Запустить без сети»: разовый запуск, VPN не нужен. Пока приложение работает, фильтр Windows не пускает его
    /// программы никуда, кроме связи внутри компьютера. Запрет действует на программу целиком, поэтому приложение,
    /// уже запущенное с сетью, сначала нужно закрыть.
    /// </summary>
    private void LaunchOffline(AppTileViewModel tile)
    {
        var id = tile.Entry.Id;
        if (_launching.Contains(id) || _dnsLaunching.Contains(id))
            return;
        if (!_offlineRuns.Contains(id) && (_tracker.CountFor(id) > 0 || _guardPending.Contains(id)))
        {
            _dialogs.ShowWarning($"«{tile.Name}» уже запущено с сетью. Закройте его и запустите без сети.", "Приложение уже запущено");
            return;
        }

        if (_offlineRuns.Add(id))
        {
            ApplyNetworkSettings("запуск без сети");
            RefreshStatus();
        }

        Launch(tile);
    }

    /// <summary>Приложения, запущенные без сети, закрылись (или не запустились): дальше они идут по своим настройкам.</summary>
    private void EndOfflineRuns()
    {
        var ended = _offlineRuns
            .Where(id => _tracker.CountFor(id) == 0 && !_launching.Contains(id) && !_guardPending.Contains(id) && !_dnsLaunching.Contains(id))
            .ToList();
        if (ended.Count == 0)
            return;

        _offlineRuns.ExceptWith(ended);
        ApplyNetworkSettings("приложение без сети закрыто");
        RefreshStatus();
    }

    // ---------------------------------------------------------------- сводка в шапке и состояние плиток

    /// <summary>Крупная строка сводки: «Защищены 2 из 3», «Ничего не запущено».</summary>
    public string SummaryTitle
    {
        get => _summaryTitle;
        private set => SetField(ref _summaryTitle, value);
    }

    /// <summary>Мелкая строка: через что идут приложения или что не так.</summary>
    public string SummarySubtitle
    {
        get => _summarySubtitle;
        private set => SetField(ref _summarySubtitle, value);
    }

    public SummaryTone SummaryTone
    {
        get => _summaryTone;
        private set => SetField(ref _summaryTone, value);
    }

    /// <summary>Открыт список приложений под сводкой.</summary>
    public bool IsSummaryOpen
    {
        get => _isSummaryOpen;
        set => SetField(ref _isSummaryOpen, value);
    }

    /// <summary>Работает ядро лаунчера, а на компьютере включён ещё и другой VPN на весь компьютер.</summary>
    public string? OtherVpnWarning
    {
        get => _otherVpnWarning;
        private set
        {
            if (SetField(ref _otherVpnWarning, value))
                OnPropertyChanged(nameof(HasOtherVpnWarning));
        }
    }

    public bool HasOtherVpnWarning => OtherVpnWarning is not null;

    /// <summary>Состояние каждой плитки и сводка в шапке. Вызывается при любом изменении процессов, VPN и настроек.</summary>
    private void RefreshStatus()
    {
        var snapshot = _monitor?.Current;
        var connections = new HashSet<string>(StringComparer.CurrentCulture);
        var anyAdapters = false;
        var anyCore = false;

        var offline = 0;
        // Почему приложения ждут сеть: от этого зависит подпись в шапке.
        var serverDown = false;
        var vpnOff = false;
        var noConnection = false;

        foreach (var tile in Apps)
        {
            var plan = PlanFor(tile.Entry);
            anyAdapters |= plan.Kind == NetworkPlanKind.SystemAdapters;
            anyCore |= plan.UsesLauncherCore;
            tile.IsOfflineMode = tile.Entry.Network.Mode == AppNetworkMode.Offline;
            tile.IsOffline = plan.Kind == NetworkPlanKind.Offline;

            // Подпись под названием: только если условие отличается от общего или с ним что-то не так.
            tile.IsSubtitleWarning = plan.Kind == NetworkPlanKind.MissingConnection;
            tile.Subtitle = plan.Kind switch
            {
                NetworkPlanKind.Offline => "Без сети",
                NetworkPlanKind.MissingConnection => "Нет подключения",
                NetworkPlanKind.LauncherConnection when plan.Source == NetworkPlanSource.App => plan.Connection!.Name,
                NetworkPlanKind.SystemAdapters when plan.Source == NetworkPlanSource.App => "Свои адаптеры",
                _ => null
            };

            if (tile.IsUnprotected)
            {
                tile.Status = AppNetStatus.Unprotected;
                tile.StatusText = "Работает без защиты: запущено мимо лаунчера";
            }
            else if (!tile.IsRunning)
            {
                tile.Status = AppNetStatus.Idle;
                tile.StatusText = plan.Kind switch
                {
                    NetworkPlanKind.Offline => "Не запущено · без сети",
                    NetworkPlanKind.MissingConnection => "Не запущено · подключение не выбрано",
                    NetworkPlanKind.LauncherConnection => $"Не запущено · через «{plan.Connection!.Name}»",
                    _ => "Не запущено · системный VPN"
                };
            }
            else if (plan.Kind == NetworkPlanKind.Offline)
            {
                tile.Status = AppNetStatus.Protected;
                tile.StatusText = "Работает без сети";
                offline++;
            }
            else if (plan.Kind == NetworkPlanKind.MissingConnection)
            {
                // Подключение удалили или сменили условие, пока приложение работало: фильтры Windows держат его без сети.
                tile.Status = AppNetStatus.Waiting;
                tile.StatusText = "Без сети: подключение не выбрано";
                noConnection = true;
            }
            else if (plan.UsesLauncherCore)
            {
                var name = plan.Connection?.Name ?? "подключение";
                if (tile.NetState == CoreAppState.Protected)
                {
                    tile.Status = AppNetStatus.Protected;
                    tile.StatusText = $"Работает через «{name}»";
                    connections.Add(name);
                }
                else
                {
                    tile.Status = AppNetStatus.Waiting;
                    tile.StatusText = $"Ждёт сеть: сервер «{name}» не отвечает";
                    serverDown = true;
                }
            }
            else if (snapshot is not null && VpnMonitor.Apply(plan.AdapterRule!, snapshot.Adapters).IsConnected)
            {
                tile.Status = AppNetStatus.Protected;
                tile.StatusText = "Работает · VPN подключён";
            }
            else
            {
                // Kill-Switch у приложения выключен: фильтры Windows держат его без сети, пока VPN не вернётся.
                tile.Status = AppNetStatus.Waiting;
                tile.StatusText = "Ждёт VPN без сети: VPN отключён";
                vpnOff = true;
            }
        }

        var running = Apps.Count(t => t.IsRunning || t.IsUnprotected);
        var protectedCount = Apps.Count(t => t.Status == AppNetStatus.Protected);
        var danger = Apps.Any(t => t.Status == AppNetStatus.Unprotected);
        var waiting = Apps.Any(t => t.Status == AppNetStatus.Waiting);

        if (running == 0)
        {
            SummaryTitle = Apps.Count == 0 ? "Нет приложений" : "Ничего не запущено";
            SummarySubtitle = anyAdapters ? (IsVpnConnected ? $"VPN подключён · {VpnSubtitle}" : "VPN не подключён")
                : anyCore ? "VPN включится сам при запуске"
                : Apps.Count > 0 ? "VPN не нужен: приложения без сети"
                : "Добавьте приложение";
            SummaryTone = anyAdapters ? (IsVpnConnected ? SummaryTone.Ok : SummaryTone.Danger) : SummaryTone.Idle;
        }
        else
        {
            SummaryTitle = $"Защищены {protectedCount} из {running}";
            SummarySubtitle = danger ? "Есть приложения без защиты"
                : waiting ? (serverDown && !vpnOff && !noConnection ? "Сервер не отвечает: приложения ждут сеть"
                    : vpnOff && !serverDown && !noConnection ? "VPN отключён: приложения ждут сеть"
                    : noConnection && !serverDown && !vpnOff ? "Нет подключения: приложения без сети"
                    : "Приложения ждут сеть")
                : connections.Count > 0 ? "Через " + string.Join(", ", connections.Select(c => $"«{c}»"))
                : offline == running ? "Без сети"
                : "VPN подключён";
            SummaryTone = danger ? SummaryTone.Danger : waiting ? SummaryTone.Warn : SummaryTone.Ok;
        }

        OtherVpnWarning = _network.IsCoreRunning && snapshot is { ActiveVpnNames.Count: > 0 }
            ? $"Включён ещё один VPN на весь компьютер ({DescribeActive(snapshot)}). Два VPN могут мешать друг другу."
            : null;
    }

    // ---------------------------------------------------------------- подключения лаунчера

    /// <summary>Привести список подключений к настройкам: строки не пересоздаются, обновляются подписи «кто пользуется».</summary>
    private void RebuildConnections()
    {
        for (var i = Connections.Count - 1; i >= 0; i--)
        {
            if (!_settings.Connections.Contains(Connections[i].Connection))
                Connections.RemoveAt(i);
        }

        for (var i = 0; i < _settings.Connections.Count; i++)
        {
            var connection = _settings.Connections[i];
            var index = -1;
            for (var j = 0; j < Connections.Count; j++)
            {
                if (ReferenceEquals(Connections[j].Connection, connection)) { index = j; break; }
            }

            if (index < 0)
                Connections.Insert(i, new ConnectionItemViewModel(connection, this));
            else if (index != i)
                Connections.Move(index, i);
        }

        foreach (var item in Connections)
        {
            var users = _settings.Apps
                .Where(a => NetworkPlan.For(a, _settings).Connection?.Id == item.Id)
                .Select(a => a.Name)
                .ToList();
            item.IsDefault = _settings.NetworkMode == GlobalNetworkMode.LauncherConnection && _settings.ConnectionId == item.Id;
            item.UsedBy = users.Count > 0 ? "Приложения: " + string.Join(", ", users) : "Не используется";
        }

        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(NeedsMainConnection));
        AppPage?.SyncConnections(Connections);
    }

    /// <summary>
    /// Сохранить новое подключение. Оно становится основным, если основного нет (первое подключение или основное удалили)
    /// или makeDefault (мастер первого запуска).
    /// </summary>
    private Task AddConnectionAsync(VpnConnection connection, bool makeDefault)
    {
        connection.Name = ConnectionFactory.UniqueName(connection.Name, _settings.Connections);
        _settings.Connections.Add(connection);

        if (makeDefault || _settings.Connections.All(c => c.Id != _settings.ConnectionId))
        {
            _settings.NetworkMode = GlobalNetworkMode.LauncherConnection;
            _settings.ConnectionId = connection.Id;
        }

        SaveSettings();
        ApplyNetworkSettings();
        RebuildConnections();
        RefreshRunningCounts();
        _log.Add(LogKind.Info, $"Добавлено подключение «{connection.Name}» ({ConnectionFactory.Describe(connection)})");
        Toast("Подключение добавлено", connection.Name, GlyphAdd, ToastKind.Success);
        return Task.CompletedTask;
    }

    /// <summary>Проверка подключения: задержка, IP и страна. Ошибки не бросает.</summary>
    private async Task<ConnectionCheck> CheckConnectionAsync(VpnConnection connection)
    {
        try
        {
            return await _checkConnection(connection);
        }
        catch (ConnectionParseException ex)
        {
            return new ConnectionCheck(false, null, null, null, null, ex.Message, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            return new ConnectionCheck(false, null, null, null, null, "Проверка не удалась: " + ex.Message, Array.Empty<string>());
        }
    }

    async void IConnectionActions.Check(ConnectionItemViewModel item)
    {
        if (item.IsChecking)
            return;
        item.IsChecking = true;
        var result = await CheckConnectionAsync(item.Connection);
        item.SetResult(result);
        _log.Add(result.Ok ? LogKind.Info : LogKind.Failed,
            $"Проверка подключения «{item.Name}»: {(result.Ok ? item.CheckText : result.Error)}");
        // Строки ядра проверки (через что ушли соединения) пишет технический журнал (ConnectionTester).
    }

    void IConnectionActions.MakeDefault(ConnectionItemViewModel item)
    {
        if (_settings.ConnectionId == item.Id)
            return;

        // Действует сразу, и для работающих приложений: ядро перезапускается с новым маршрутом (ApplyNetworkSettings).
        _settings.NetworkMode = GlobalNetworkMode.LauncherConnection;
        _settings.ConnectionId = item.Id;
        SaveSettings();
        ApplyNetworkSettings();
        RebuildConnections();
        RefreshRunningCounts();
        _log.Add(LogKind.Info, $"Основное подключение: «{item.Name}»");
        Toast("Основное подключение", item.Name, GlyphCheck, ToastKind.Success);
    }

    void IConnectionActions.Rename(ConnectionItemViewModel item, string name)
    {
        var old = item.Name;
        item.Connection.Name = ConnectionFactory.UniqueName(name, _settings.Connections, item.Id);
        SaveSettings();
        ApplyNetworkSettings();
        item.Refresh();
        RebuildConnections();
        RefreshRunningCounts();
        _log.Add(LogKind.Info, $"Подключение «{old}» переименовано в «{item.Name}»");
    }

    void IConnectionActions.Delete(ConnectionItemViewModel item)
    {
        var id = item.Id;
        var users = _settings.Apps.Where(a => NetworkPlan.For(a, _settings).Connection?.Id == id).ToList();
        // Запускаемые тоже заняты: их ядро и фильтры уже готовятся под это подключение.
        var running = new HashSet<Guid>(_tracker.RunningApps());
        running.UnionWith(_launching);
        running.UnionWith(_dnsLaunching);
        running.UnionWith(_guardPending);
        var busy = users.Where(a => running.Contains(a.Id)).Select(a => a.Name).ToList();
        if (busy.Count > 0)
        {
            _dialogs.ShowWarning($"Через «{item.Name}» сейчас работают или запускаются: {string.Join(", ", busy)}. Закройте их, потом удаляйте подключение.",
                "Подключение занято");
            return;
        }

        var message = users.Count > 0
            ? $"Им пользуются: {string.Join(", ", users.Select(a => a.Name))}. Они не запустятся, пока им не выберут другое подключение."
            : "Ссылка или подписка будут удалены из лаунчера.";
        if (_settings.ConnectionId == id && _settings.Connections.Count > 1)
            message += " Это основное подключение: потом сделайте основным другое.";
        if (!_dialogs.Confirm($"Удалить «{item.Name}»?", message, "Удалить", destructive: true))
            return;

        _settings.Connections.Remove(item.Connection);
        foreach (var app in _settings.Apps.Where(a => a.Network.ConnectionId == id))
            app.Network.ConnectionId = null;
        if (_settings.ConnectionId == id)
            _settings.ConnectionId = null;

        if (_draftUpdateConnectionId == id)
        {
            _draftUpdateConnectionId = _settings.Updates.ConnectionId == id ? null : _settings.Updates.ConnectionId;
            OnPropertyChanged(nameof(UpdateConnectionId));
        }

        SaveSettings();
        ApplyNetworkSettings();
        RebuildConnections();
        OnDraftChanged();
        RefreshRunningCounts();
        _log.Add(LogKind.Info, $"Удалено подключение «{item.Name}»");
        Toast("Подключение удалено", item.Name, GlyphInfo, ToastKind.Info);
    }

    async void IConnectionActions.UpdateSubscription(ConnectionItemViewModel item)
    {
        if (item.IsUpdating)
            return;
        item.IsUpdating = true;
        try
        {
            await UpdateSubscriptionAsync(item.Connection, userInitiated: true);
        }
        finally
        {
            item.IsUpdating = false;
        }

        item.Refresh();
        RebuildConnections();
    }

    void IConnectionActions.SelectServer(ConnectionItemViewModel item, string serverKey)
    {
        var connection = item.Connection;
        var server = connection.SubscriptionServers.FirstOrDefault(s => Subscriptions.ServerKey(s) == serverKey);
        if (server is null || Subscriptions.ServerKey(connection.SelectedServer ?? string.Empty) == serverKey)
            return;

        connection.SelectedServer = serverKey;
        SaveSettings();
        ApplyNetworkSettings();
        item.Refresh();
        var name = ConnectionFactory.ServerName(server);
        _log.Add(LogKind.Info, $"Подписка «{connection.Name}»: выбран сервер «{name}»");

        var running = AffectedRunningApps(a => NetworkPlan.For(a, _settings).Connection?.Id == connection.Id);
        Toast("Сервер выбран", running.Count > 0 ? $"{name} · подействует при следующем запуске" : name, GlyphCheck, ToastKind.Success);
    }

    // ---------------------------------------------------------------- страница приложения

    private void OpenAppSettings(AppTileViewModel tile) => OpenAppPage(tile.Entry, isNew: false, tile.Icon);

    private void OpenAppPage(AppEntry entry, bool isNew, System.Windows.Media.ImageSource? icon)
    {
        if (CurrentPage == MainPage.App && AppPage?.Entry == entry)
            return;
        if (!TryLeavePage())
            return;

        var add = new AddConnectionViewModel(() => _settings.Connections, c => AddConnectionAsync(c, makeDefault: false), _dialogs);
        AppPage = new AppSettingsViewModel(entry, isNew, _settings, icon, Connections, add,
            page => SaveAppPage(page, navigate: true),
            _ =>
            {
                CloseAppPage();
                CurrentPage = MainPage.Apps;
            });
        CurrentPage = MainPage.App;
        if (AppPage.IsAddTab && AppPage.IsOwnConnection)
            add.OfferClipboard();
    }

    private void CloseAppPage()
    {
        AppPage?.Detach();
        AppPage = null;
    }

    /// <summary>«Сохранить» на странице приложения (для нового — «Добавить»). Черновик уже проверен.</summary>
    private void SaveAppPage(AppSettingsViewModel page, bool navigate)
    {
        var entry = page.Entry;
        var oldNetwork = System.Text.Json.JsonSerializer.Serialize(entry.Network);
        page.ApplyTo(entry);

        if (page.IsNew)
        {
            _settings.Apps.Add(entry);
            SaveSettings();
            ApplyNetworkSettings();
            Apps.Add(CreateTile(entry));
            OnPropertyChanged(nameof(HasNoApps));
            StatusMessage = $"Добавлено: {entry.Name} → {(entry.IsPackaged ? "Microsoft Store" : entry.ExePath)}";
            Toast("Приложение добавлено", entry.Name, GlyphAdd, ToastKind.Info);
            _log.Add(LogKind.Info, $"Добавлено приложение «{entry.Name}» · {DescribeApp(entry)}");
        }
        else
        {
            SaveSettings();
            ApplyNetworkSettings();
            UnblockWithoutKillSwitch();
            var tile = Apps.FirstOrDefault(t => t.Entry == entry);
            tile?.RefreshName();

            var networkChanged = oldNetwork != System.Text.Json.JsonSerializer.Serialize(entry.Network);
            if (networkChanged)
                _log.Add(LogKind.Info, $"Настройки «{entry.Name}»: {DescribeApp(entry)}");
            Toast("Настройки сохранены", entry.Name, GlyphCheck, ToastKind.Success);
        }

        RefreshDnsState();
        RebuildConnections();
        RefreshRunningCounts();

        if (navigate)
        {
            CloseAppPage();
            CurrentPage = MainPage.Apps;
        }
    }

    /// <summary>
    /// Приложения, которые больше не закрываются при обрыве («Закрывать при обрыве» выключили в общих настройках или у
    /// самого приложения): закрытые раньше можно снова запускать как угодно. У кого своё «Вкл», запрет остаётся.
    /// </summary>
    private void UnblockWithoutKillSwitch()
    {
        foreach (var entry in _settings.Apps.Where(a => !PlanFor(a).CloseOnDrop))
            _tracker.StopBlocking(entry.Id);
    }

    /// <summary>Условие запуска приложения словами, для журнала.</summary>
    private string DescribeApp(AppEntry entry) => entry.Network.Mode switch
    {
        AppNetworkMode.OwnConnection => $"своё подключение «{_settings.Connections.FirstOrDefault(c => c.Id == entry.Network.ConnectionId)?.Name}»",
        AppNetworkMode.Offline => "без сети",
        _ => "основное подключение"
    } + entry.Network.OutsideCopies switch
    {
        OutsideCopiesOverride.Vpn => ", если открыто не из лаунчера: через VPN",
        OutsideCopiesOverride.Block => ", если открыто не из лаунчера: без интернета",
        OutsideCopiesOverride.Allow => ", если открыто не из лаунчера: напрямую",
        OutsideCopiesOverride.Launcher => ", если открыто не из лаунчера: через лаунчер",
        _ => string.Empty
    };

    /// <summary>
    /// Системного VPN в лаунчере больше нет, и загрузка перевела настройки на основное подключение: один раз сказать, какие
    /// приложения теперь идут через него. Без подключений такие приложения не запустятся: открываем раздел «Подключения».
    /// </summary>
    internal void ReportSystemVpnRemoved()
    {
        if (_movedFromSystemVpn is not { } moved)
            return;
        _movedFromSystemVpn = null;

        var main = _settings.Connections.FirstOrDefault(c => c.Id == _settings.ConnectionId);
        _log.Add(LogKind.Info, "Системного VPN в лаунчере больше нет: " + (main is not null
            ? $"приложения, которые шли через него, идут через основное подключение «{main.Name}»"
            : "приложения, которые шли через него, ждут основное подключение") +
            (moved.Apps.Count > 0 ? $" ({string.Join(", ", moved.Apps)})" : string.Empty));
        if (moved.Apps.Count == 0)
            return;

        var apps = string.Join(", ", moved.Apps);
        if (main is not null)
        {
            _dialogs.ShowInfo(
                "Лаунчер больше не работает с VPN-программами, включёнными на весь компьютер. Приложения, которые шли через " +
                $"системный VPN ({apps}), теперь идут через основное подключение лаунчера «{main.Name}».\n\n" +
                "Сделать основным другое подключение можно в настройках, раздел «Подключения». Своё подключение для приложения " +
                "выбирается в его настройках.",
                "Системного VPN больше нет");
            return;
        }

        OpenSettings(SettingsSection.Connections);
        _dialogs.ShowWarning(
            "Лаунчер больше не работает с VPN-программами, включёнными на весь компьютер. Приложения, которые шли через " +
            $"системный VPN ({apps}), теперь идут через подключение лаунчера, а подключений пока нет, поэтому они не запустятся.\n\n" +
            "Добавьте ссылку на сервер или адрес подписки от вашего VPN-сервиса.",
            "Системного VPN больше нет");
    }

    // ---------------------------------------------------------------- мастер первого запуска

    private void StartWelcome()
    {
        var add = new AddConnectionViewModel(() => _settings.Connections, c => AddConnectionAsync(c, makeDefault: true), _dialogs);
        Welcome = new WelcomeViewModel(add, CheckConnectionAsync,
            new StartMenuListViewModel(_findStartMenuApps, exe => IconExtractor.TryGetIcon(exe), IsAdded),
            finish: AddFromStartMenu,
            skip: () => GoBack());
        CurrentPage = MainPage.Welcome;
        Welcome.Start();
    }

    private void FinishWelcome()
    {
        Welcome?.Detach();
        Welcome = null;
        if (_settings.WelcomeDone)
            return;
        _settings.WelcomeDone = true;
        SaveSettings();
    }

    private void AddFromStartMenu(IReadOnlyList<StartMenuApp> apps)
    {
        AddStartMenuApps(apps);
        GoBack();
        RebuildConnections();
        RefreshRunningCounts();
    }

    /// <summary>Отмеченные программы из меню «Пуск» (мастер первого запуска и «Добавить приложение»).</summary>
    private void AddStartMenuApps(IReadOnlyList<StartMenuApp> apps)
    {
        var added = new List<string>();
        foreach (var app in apps)
        {
            if (BuildEntry(app.ShortcutPath, quiet: true) is not { } entry)
                continue;
            _settings.Apps.Add(entry);
            Apps.Add(CreateTile(entry));
            added.Add(entry.Name);
        }

        if (added.Count > 0)
        {
            SaveSettings();
            ApplyNetworkSettings();
            OnPropertyChanged(nameof(HasNoApps));
            _log.Add(LogKind.Info, $"Добавлены приложения: {string.Join(", ", added)}");
            Toast(added.Count == 1 ? "Приложение добавлено" : "Приложения добавлены", string.Join(", ", added), GlyphAdd, ToastKind.Info);
            RebuildConnections();
            RefreshRunningCounts();
        }
    }

    // ---------------------------------------------------------------- журнал DNS

    private async void ToggleDns(AppTileViewModel tile)
    {
        var entry = tile.Entry;
        var on = !entry.TrackDns;
        SetDnsTracking(tile, on);

        if (!on)
        {
            _dns.StopIfIdle();
            StatusMessage = $"Журнал DNS выключен: {entry.Name}";
            _log.Add(LogKind.Info, $"Журнал DNS выключен: {entry.Name}");
            return;
        }

        _log.Add(LogKind.Info, $"Журнал DNS включён: {entry.Name}");

        // Пользователь только что сам включил журнал, поэтому запрос прав (если он нужен) уместен сейчас.
        if (await TryStartDnsAsync())
        {
            StatusMessage = $"Журнал DNS включён: {entry.Name}";
            Toast("Журнал DNS включён", $"Запросы «{entry.Name}» записываются", GlyphGlobe, ToastKind.Info);
        }
        else if (entry.TrackDns)
        {
            SetDnsTracking(tile, false); // без записи галочка вводила бы в заблуждение
        }
    }

    private void SetDnsTracking(AppTileViewModel tile, bool on)
    {
        tile.Entry.TrackDns = on;
        SaveSettings();
        _dns.SetTracking(tile.Entry.Id, tile.Entry.Name, on);
        RefreshDnsState();
    }

    /// <summary>Запуск записи DNS. false — не вышло (пользователь отказал в UAC или ошибка), причина уже показана.</summary>
    private async Task<bool> TryStartDnsAsync()
    {
        try
        {
            await _dns.StartAsync();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            StatusMessage = "Журнал DNS не запущен: нет разрешения Windows";
            Toast("Журнал DNS не запущен", "Для записи DNS нужны права администратора", GlyphGlobe, ToastKind.Danger);
            return false;
        }
        catch (Exception ex)
        {
            _log.Add(LogKind.Failed, $"Не удалось запустить журнал DNS: {ex.Message}");
            TechLog.Warn(LogAreas.Dns, LogCodes.DnsJournal, $"Помощник журнала DNS не запустился: {ex.GetType().Name}: {ex.Message}");
            _dialogs.ShowError($"Не удалось запустить журнал DNS:\n{ex.Message}");
            return false;
        }
        finally
        {
            RefreshDnsState();
        }
    }

    private async void StartDnsThenLaunch(AppTileViewModel tile)
    {
        if (!_dnsLaunching.Add(tile.Entry.Id))
            return;

        try
        {
            // Если запустить запись не вышло, приложение всё равно запускается: журнал — не защита.
            await TryStartDnsAsync();
        }
        finally
        {
            _dnsLaunching.Remove(tile.Entry.Id);
        }

        if (Apps.Contains(tile))
            Launch(tile, dnsReady: true);
    }

    private void OpenDnsJournal(AppTileViewModel tile) =>
        _dialogs.ShowDnsJournal(tile, _dns.Journal(tile.Entry.Id, tile.Entry.Name));

    private void RefreshDnsState()
    {
        var active = _dns.IsActive;
        foreach (var tile in Apps)
        {
            tile.IsDnsTracking = tile.Entry.TrackDns;
            tile.IsDnsActive = active && tile.Entry.TrackDns;
            tile.HasDnsJournal = _dns.FindJournal(tile.Entry.Id) is { IsEmpty: false };
        }
    }

    private void OnDnsSourceLost()
    {
        if (!_dns.HasTracked)
            return;

        StatusMessage = "Журнал DNS остановлен";
        _log.Add(LogKind.Failed, "Журнал DNS остановлен: помощник записи закрылся. Запись продолжится при следующем запуске приложения");
        TechLog.Warn(LogAreas.Dns, LogCodes.DnsJournal, "Помощник журнала DNS закрылся");
        Toast("Журнал DNS остановлен", "Запись продолжится при следующем запуске приложения", GlyphGlobe, ToastKind.Danger);
    }

    // ---------------------------------------------------------------- трей

    /// <summary>
    /// Кнопка «Закрыть», Alt+F4 или «Закрыть окно» на панели задач: true — свернуть в трей, false — закрыть лаунчер,
    /// null — передумали. Спрашивает, только если выбор не запомнен.
    /// </summary>
    public bool? DecideHide()
    {
        switch (_settings.HideBehavior)
        {
            case HideBehavior.Tray:
                return true;
            case HideBehavior.Exit:
                return false;
        }

        var choice = _dialogs.AskHide(out var remember);
        if (choice is { } toTray && remember)
        {
            _settings.HideBehavior = toTray ? HideBehavior.Tray : HideBehavior.Exit;
            SaveSettings();

            // В панели настроек сразу видно новое значение (если там не было своих несохранённых правок).
            if (_draftHideBehavior == HideBehavior.Ask)
            {
                _draftHideBehavior = _settings.HideBehavior;
                OnPropertyChanged(nameof(HideBehavior));
            }

            OnDraftChanged();
        }

        return choice;
    }

    /// <summary>Подсказку «лаунчер работает в трее» показываем один раз: true — показать сейчас.</summary>
    public bool TakeTrayHint()
    {
        if (_settings.TrayHintShown)
            return false;

        _settings.TrayHintShown = true;
        SaveSettings();
        return true;
    }

    // ---------------------------------------------------------------- закрытие и сохранение

    /// <summary>При закрытии лаунчера будет вопрос о запущенных приложениях.</summary>
    public bool NeedsCloseConfirmation => !_closingForUpdate && AppsClosedOnExit().Count > 0;

    /// <summary>
    /// Вызывается при закрытии окна. Без работающего лаунчера Kill-Switch не действует,
    /// поэтому при включённой опции запущенные приложения закрываются вместе с ним.
    /// </summary>
    public bool ConfirmClose()
    {
        // Закрываемся ради установки обновления: всё уже подтверждено и сделано.
        if (_closingForUpdate)
            return true;

        var closing = AppsClosedOnExit();
        if (closing.Count == 0)
            return true;

        if (!_dialogs.Confirm("Закрыть лаунчер?",
                $"Лаунчер защищает запущенные приложения ({closing.Count}). Без него защита не работает, поэтому они будут принудительно закрыты.",
                "Закрыть всё", destructive: true))
            return false;

        // Сначала приложения, потом подключение (ядро выключится в Dispose): иначе они успели бы пойти напрямую. Список
        // заново: пока был открыт вопрос, могло запуститься ещё одно приложение.
        var report = _tracker.KillApps(AppsClosedOnExit(), blockRelaunch: false);
        if (report.Killed.Count > 0)
            _log.Add(LogKind.Closed, $"При закрытии лаунчера закрыты: {string.Join(", ", report.Killed)}");
        if (report.Failed.Count == 0)
            return true;

        _log.Add(LogKind.Failed, $"При закрытии лаунчера не удалось закрыть: {string.Join(", ", report.Failed)}");
        // Без лаунчера они работали бы без фильтров и подключения: пусть пользователь решит, пока защита ещё есть.
        return _dialogs.Confirm("Не все приложения закрыты",
            "Эти приложения не удалось закрыть (обычно так бывает с программами, запущенными с правами администратора):\n\n" +
            string.Join("\n", report.Failed) + "\n\nБез лаунчера они будут работать без защиты. Закройте их вручную или закройте лаунчер всё равно.",
            "Закрыть лаунчер", destructive: true);
    }

    private void SaveSettings()
    {
        LogSettingsChanges();
        try
        {
            _storage.Save(_settings);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError($"Не удалось сохранить настройки в {_storage.FilePath}:\n{ex.Message}");
        }
    }

    public void Dispose()
    {
        _log.Add(LogKind.Info, "Лаунчер закрыт");
        _logTimer?.Stop();
        _monitor.StatusChanged -= OnVpnStatusChanged;
        _monitor.Dispose();
        _subscriptionTimer.Stop();
        DisposeUpdates();
        _dns.Dispose();

        // Сначала приложения (Windows закроет те, что закрываются вместе с лаунчером), потом ядро и фильтры.
        _tracker.Dispose();
        _network.Dispose();
        _guard.Dispose(); // служба снимает фильтры (скрытие DNS — когда закроются приложения, которые работают и без лаунчера)
        _proxy.Dispose();
    }
}
