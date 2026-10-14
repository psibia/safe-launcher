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

    // Обновления: результат последней проверки и состояние кнопок в настройках.
    private UpdateCheckResult? _updateCheck;
    private Dictionary<string, string> _settingsSnapshot = new();
    private bool? _lastIpv6;
    private DispatcherTimer? _logTimer;
    private bool _isUpdateBusy;
    private string _updateStatusText = "Ещё не проверялось";
    private bool _closingForUpdate;

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

    // Приложения под системным VPN: их правила адаптеров и защита. Снимок для фонового потока монитора.
    private volatile IReadOnlyList<AdapterApp> _adapterApps = Array.Empty<AdapterApp>();

    // Подписки обновляются при запуске и потом раз в час проверяется, не пора ли.
    private readonly DispatcherTimer _subscriptionTimer;
    private bool _isUpdatingSubscriptions;

    private sealed record AdapterApp(Guid Id, string Name, VpnRule Rule, bool CloseOnDrop);

    // Проверка подключений: отдельная копия ядра с правами пользователя. Тесты подставляют свою.
    private readonly Func<VpnConnection, Task<ConnectionCheck>> _checkConnection;

    // Страницы окна: плитки, настройки, приложение, мастер первого запуска.
    private MainPage _page = MainPage.Apps;
    private SettingsSection _section = SettingsSection.Connection;
    private AppSettingsViewModel? _appPage;
    private WelcomeViewModel? _welcome;

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
    private bool _draftRequireVpn;
    private bool _draftBlockBypass;
    private bool _draftHideDns;
    private bool _draftStartWithWindows;
    private GlobalNetworkMode _draftNetworkMode;
    private Guid? _draftConnectionId;
    private VpnRuleMode _draftRuleMode;
    private HideBehavior _draftHideBehavior;
    private readonly List<string> _draftSelected = new();

    // Только вид списка, не сохраняется: при каждом запуске выключено.
    private bool _showAllAdapters;
    private bool _isRemovedExpanded;

    /// <param name="log">Журнал событий; по умолчанию — в папке настроек. Тесты передают свой.</param>
    /// <param name="updates">Обновления; по умолчанию — обычные пути. Тесты передают свои.</param>
    /// <param name="dnsSource">Источник событий журнала DNS; по умолчанию — Windows. Тесты передают свой.</param>
    /// <param name="coreBackend">Ядро VPN; по умолчанию — служба «Безопасный лаунчер — сеть». Тесты передают своё.</param>
    /// <param name="checkConnection">Проверка подключения; по умолчанию — отдельной копией ядра. Тесты передают свою.</param>
    /// <param name="guardBackend">Фильтры Windows; по умолчанию — через службу. Тесты передают свои.</param>
    /// <param name="systemProxy">Чтение системного прокси; по умолчанию — из реестра. Тесты передают своё.</param>
    public MainViewModel(SettingsStorage storage, IDialogService dialogs, Dispatcher dispatcher,
        ActivityLog? log = null, UpdateService? updates = null,
        Func<Action<DnsQueryEvent>, Task<IDnsEventSource>>? dnsSource = null,
        Func<ICoreBackend>? coreBackend = null,
        Func<VpnConnection, Task<ConnectionCheck>>? checkConnection = null,
        Func<IGuardBackend>? guardBackend = null,
        Func<LocalProxy?>? systemProxy = null)
    {
        _checkConnection = checkConnection ?? (c => new ConnectionTester().CheckAsync(ConnectionOutbound.Resolve(c).Outbound));
        _log = log ?? new ActivityLog();
        _updates = updates ?? new UpdateService();
        _dns = new DnsMonitor(_tracker.TryGetOwner, dnsSource);
        _storage = storage;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _settings = storage.Load();
        ApplyLogSettings();
        _network = new LauncherNetwork(_tracker, coreBackend ?? (() => new ServiceCoreBackend(ResolveCoreTag)));
        _guard = new VpnGuard(guardBackend ?? (() => new ServiceGuardBackend()));
        _proxy = new SystemProxyWatcher(systemProxy);

        // Адаптеры, выбранные до появления списка «удалённых», тоже должны в нём быть.
        foreach (var name in _settings.SelectedAdapters.Where(n => FindKnown(n) is null))
            _settings.KnownAdapters.Add(new KnownAdapter { Name = name, Description = name, Product = name, IsVpnLike = true });

        ResetDraft();
        AddConnection = new AddConnectionViewModel(() => _settings.Connections, c => AddConnectionAsync(c, makeDefault: false), _dialogs);
        RebuildConnections();

        AddAppCommand = new RelayCommand(AddApp);
        ApplySettingsCommand = new RelayCommand(() => ApplySettings());
        CancelSettingsCommand = new RelayCommand(() =>
        {
            ResetDraft();
            StatusMessage = "Изменения настроек отменены";
        });
        RefreshAdaptersCommand = new RelayCommand(() => ApplySnapshot(_monitor!.Evaluate()));
        OpenSettingsCommand = new RelayCommand(p => OpenSettings(p is string section && Enum.TryParse<SettingsSection>(section, out var s) ? s : null));
        BackCommand = new RelayCommand(() => GoBack());
        ShowSectionCommand = new RelayCommand(p =>
        {
            if (p is string section && Enum.TryParse<SettingsSection>(section, out var s))
                Section = s;
        });
        ToggleSummaryCommand = new RelayCommand(() => IsSummaryOpen = !IsSummaryOpen);
        UpdateActionCommand = new RelayCommand(OnUpdateAction);
        ShowChangelogCommand = new RelayCommand(ShowChangelog);
        ShowLogCommand = new RelayCommand(() => _dialogs.ShowActivityLog(_log, this));
        CreateLogCommands();
        ShowHideDnsInfoCommand = new RelayCommand(() => _dialogs.ShowInfo(HideDnsExplanation, "Скрытие DNS-запросов"));

        foreach (var entry in _settings.Apps)
        {
            Apps.Add(CreateTile(entry));

            // Журнал DNS остаётся включённым между запусками, но запись начнётся, только когда приложение
            // запустят из лаунчера: окно UAC не должно появляться при каждом входе в Windows.
            if (entry.TrackDns)
                _dns.SetTracking(entry.Id, entry.Name, true);
        }

        _dns.StateChanged += (_, _) => _dispatcher.BeginInvoke(RefreshDnsState);
        _dns.SourceLost += (_, _) => _dispatcher.BeginInvoke(OnDnsSourceLost);

        // Без лаунчера защита не работает: приложения, которые закрываются вместе с ним (ClosesWithLauncher),
        // закроются и при его аварийном завершении.
        ApplyNetworkSettings();
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
        _network.CoreLost += (reason, report) => _dispatcher.BeginInvoke(() => OnCoreLost(reason, report));
        _network.ConnectionDown += info => _dispatcher.BeginInvoke(() => OnConnectionDown(info));
        _network.ConnectionUp += (_, name) => _dispatcher.BeginInvoke(() => OnConnectionUp(name));
        _network.ProbeFailed += failure => _dispatcher.BeginInvoke(() => _ = OnProbeFailedAsync(failure));
        _network.StateChanged += () => _dispatcher.BeginInvoke(RefreshRunningCounts);

        _subscriptionTimer = new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => _ = UpdateSubscriptionsAsync(), _dispatcher);

        _monitor = new VpnMonitor(BuildRule());
        _monitor.StatusChanged += OnVpnStatusChanged;
        _monitor.Start();
        ApplySnapshot(_monitor.Current);

        // Запись автозапуска указывает на эту копию лаунчера (после переустановки в другую папку — тоже).
        if (_settings.StartWithWindows)
            TrySetAutoStart(true);

        RefreshRunningCounts();
        if (!_settings.WelcomeDone && _settings.Apps.Count == 0 && _settings.Connections.Count == 0)
            StartWelcome();

        _log.Add(LogKind.Info, $"Лаунчер {UpdateService.Format(UpdateService.CurrentVersion)} запущен · " +
                                    (_monitor.Current.IsConnected ? $"VPN подключён ({DescribeActive(_monitor.Current)})" : "VPN не подключён") +
                                    (_settings.KillSwitchEnabled ? string.Empty : " · Kill-Switch выключен"));
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

    /// <summary>Адаптеры VPN и выбранные в правилах, которые сейчас есть в системе.</summary>
    public ObservableCollection<VpnAdapterViewModel> Adapters { get; } = new();

    /// <summary>Остальные адаптеры системы: отдельная группа, видна при «Показывать все адаптеры».</summary>
    public ObservableCollection<VpnAdapterViewModel> OtherAdapters { get; } = new();

    /// <summary>Адаптеры, которые лаунчер уже видел, но сейчас их в системе нет.</summary>
    public ObservableCollection<VpnAdapterViewModel> RemovedAdapters { get; } = new();

    public ICommand AddAppCommand { get; }
    public ICommand ApplySettingsCommand { get; }
    public ICommand CancelSettingsCommand { get; }
    public ICommand RefreshAdaptersCommand { get; }

    /// <summary>Открыть настройки; параметр — раздел (имя из <see cref="SettingsSection"/>), иначе тот, что был открыт.</summary>
    public ICommand OpenSettingsCommand { get; }

    /// <summary>«← Приложения» и Esc: вернуться к плиткам (с вопросом, если есть несохранённое).</summary>
    public ICommand BackCommand { get; }

    /// <summary>Раздел настроек в списке слева; параметр — имя из <see cref="SettingsSection"/>.</summary>
    public ICommand ShowSectionCommand { get; }

    public ICommand ToggleSummaryCommand { get; }

    /// <summary>Подключения лаунчера: раздел «Подключения лаунчера» и списки выбора подключения.</summary>
    public ObservableCollection<ConnectionItemViewModel> Connections { get; } = new();

    /// <summary>Форма «Добавить подключение» в настройках.</summary>
    public AddConnectionViewModel AddConnection { get; }
    public ICommand UpdateActionCommand { get; }
    public ICommand ShowChangelogCommand { get; }
    public ICommand ShowLogCommand { get; }

    /// <summary>«Как это работает и на что влияет» у «Скрывать DNS-запросы».</summary>
    public ICommand ShowHideDnsInfoCommand { get; }

    /// <summary>Окно должно закрыться (лаунчер обновляется). Всегда в UI-потоке.</summary>
    public event EventHandler? ShutdownRequested;

    // ---------------------------------------------------------------- обновления: свойства

    public string AppVersionText => $"v{UpdateService.Format(UpdateService.CurrentVersion)}";

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetField(ref _updateStatusText, value);
    }

    public bool IsUpdateAvailable => _updateCheck?.IsUpdateAvailable == true;

    /// <summary>Для кнопки «Проверить»: видна, пока обновления нет.</summary>
    public bool IsUpdateNotAvailable => !IsUpdateAvailable;

    /// <summary>Кнопки обновления недоступны, пока идёт проверка или загрузка.</summary>
    public bool CanUseUpdates => !_isUpdateBusy;

    private bool IsUpdateBusy
    {
        get => _isUpdateBusy;
        set
        {
            if (_isUpdateBusy == value) return;
            _isUpdateBusy = value;
            OnPropertyChanged(nameof(CanUseUpdates));
        }
    }

    public bool IsVpnConnected
    {
        get => _isVpnConnected;
        private set
        {
            if (SetField(ref _isVpnConnected, value))
                OnPropertyChanged(nameof(VpnStatusText));
        }
    }

    public string VpnStatusText => IsVpnConnected ? "VPN подключён" : "VPN отключён";

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
            IsSummaryOpen = false;
            if (value == MainPage.Settings && Section == SettingsSection.Connections)
                AddConnection.OfferClipboard();
        }
    }

    public bool IsAppsPage => CurrentPage == MainPage.Apps;
    public bool IsAppPage => CurrentPage == MainPage.App;
    public bool IsWelcomePage => CurrentPage == MainPage.Welcome;

    /// <summary>Открыта страница настроек. Закрытие с неприменёнными изменениями спрашивает, применить ли их.</summary>
    public bool IsSettingsOpen
    {
        get => CurrentPage == MainPage.Settings;
        set
        {
            if (value)
                Navigate(MainPage.Settings);
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

    /// <summary>Вернуться к плиткам. false — пользователь решил остаться на странице.</summary>
    public bool GoBack() => Navigate(MainPage.Apps);

    /// <summary>Открыть настройки в нужном разделе (или в том, что был открыт).</summary>
    public void OpenSettings(SettingsSection? section = null)
    {
        if (!Navigate(MainPage.Settings))
            return;
        if (section is { } s)
            Section = s;
        else if (IsUpdateAvailable)
            Section = SettingsSection.About; // есть обновление: сразу к нему
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

            default:
                return true;
        }
    }

    /// <summary>На странице есть изменения, которые ещё не применены.</summary>
    public bool HasPendingChanges =>
        _draftKillSwitch != _settings.KillSwitchEnabled ||
        _draftRequireVpn != _settings.RequireVpn ||
        _draftNetworkMode != _settings.NetworkMode ||
        _draftNetworkMode == GlobalNetworkMode.LauncherConnection && _draftConnectionId != _settings.ConnectionId ||
        _draftRuleMode != _settings.RuleMode ||
        _draftHideBehavior != _settings.HideBehavior ||
        _draftBlockBypass != _settings.BlockBypass ||
        _draftHideDns != _settings.HideDns ||
        _draftStartWithWindows != _settings.StartWithWindows ||
        !new HashSet<string>(_draftSelected, StringComparer.OrdinalIgnoreCase).SetEquals(_settings.SelectedAdapters) ||
        HasLogDraftChanges;

    /// <summary>«Не запускать без VPN» для приложений «как в общих» (черновик).</summary>
    public bool RequireVpn
    {
        get => _draftRequireVpn;
        set
        {
            if (_draftRequireVpn == value) return;
            _draftRequireVpn = value;
            OnPropertyChanged();
            OnDraftChanged();
        }
    }

    /// <summary>Общее условие запуска: системный VPN или подключение лаунчера (черновик).</summary>
    public GlobalNetworkMode NetworkMode
    {
        get => _draftNetworkMode;
        set
        {
            if (_draftNetworkMode == value) return;
            _draftNetworkMode = value;

            // Единственное подключение выбираем сами, чтобы не пришлось искать его в списке.
            if (value == GlobalNetworkMode.LauncherConnection && _draftConnectionId is null && _settings.Connections.Count == 1)
            {
                _draftConnectionId = _settings.Connections[0].Id;
                OnPropertyChanged(nameof(DraftConnectionId));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLauncherMode));
            OnPropertyChanged(nameof(IsSystemMode));
            OnDraftChanged();
        }
    }

    public bool IsLauncherMode => NetworkMode == GlobalNetworkMode.LauncherConnection;
    public bool IsSystemMode => NetworkMode == GlobalNetworkMode.SystemVpn;

    /// <summary>Подключение в общих настройках (черновик).</summary>
    public Guid? DraftConnectionId
    {
        get => _draftConnectionId;
        set
        {
            if (_draftConnectionId == value) return;
            _draftConnectionId = value;
            OnPropertyChanged();
            OnDraftChanged();
        }
    }

    /// <summary>Выбрано подключение лаунчера, а само подключение — нет.</summary>
    public bool NeedsConnectionSelection =>
        _draftNetworkMode == GlobalNetworkMode.LauncherConnection &&
        (_draftConnectionId is not { } id || _settings.Connections.All(c => c.Id != id));

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
                    _draftBlockBypass
                        ? "При обрыве VPN запущенные приложения не будут закрываться. Пока VPN не вернётся, они останутся без сети: " +
                          "это делает «Блокировать трафик мимо VPN»."
                        : "При обрыве VPN запущенные приложения продолжат работать без защиты. Это может привести к утечке данных.",
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

    /// <summary>Для заметного предупреждения на главном экране, раз сам переключатель спрятан в настройках.</summary>
    public bool IsKillSwitchOff => !_settings.KillSwitchEnabled;

    /// <summary>«Блокировать трафик мимо VPN» (черновик). Выключение — только после подтверждения.</summary>
    public bool BlockBypass
    {
        get => _draftBlockBypass;
        set
        {
            if (_draftBlockBypass == value) return;
            if (!value && _settings.BlockBypass && !_dialogs.Confirm(
                    "Выключить блокировку трафика мимо VPN?",
                    "Без неё программа приложения сможет сама выбрать обычное подключение к интернету в обход VPN. Так делают, " +
                    "например, браузеры и мессенджеры для звонков (WebRTC): собеседник или сайт узнает ваш настоящий адрес.\n\n" +
                    "Выключайте, только если из-за блокировки приложению не хватает сети: например, ему нужны принтер или другие " +
                    "устройства в домашней сети.",
                    "Выключить",
                    destructive: true))
            {
                _dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(BlockBypass)));
                return;
            }

            _draftBlockBypass = value;
            OnPropertyChanged();
            OnDraftChanged();
        }
    }

    /// <summary>«Скрывать DNS-запросы от провайдера» (черновик). Влияет на весь компьютер: включение — после объяснения.</summary>
    public bool HideDns
    {
        get => _draftHideDns;
        set
        {
            if (_draftHideDns == value) return;
            if (value && !_settings.HideDns && !_dialogs.Confirm("Скрывать DNS-запросы от провайдера?", HideDnsExplanation, "Включить"))
            {
                _dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(HideDns)));
                return;
            }

            _draftHideDns = value;
            OnPropertyChanged();
            OnDraftChanged();
        }
    }

    /// <summary>Что делает «Скрывать DNS-запросы» и на что влияет: показывается перед включением.</summary>
    internal const string HideDnsExplanation =
        "Зачем. Чтобы открыть сайт, программа сначала спрашивает его адрес по имени (DNS). В Windows эти вопросы за все " +
        "программы задаёт одна системная служба, поэтому по ним нельзя понять, какая программа спрашивает. Без этой " +
        "настройки трафик приложений идёт через VPN, но имена сайтов, которые они открывают, может видеть провайдер.\n\n" +
        "Как работает. Пока работает приложение лаунчера:\n" +
        "• с подключением лаунчера — на эти вопросы отвечает лаунчер условными адресами (198.18.x.x), а настоящий адрес " +
        "ищется при соединении: для приложений лаунчера — через VPN, для остальных программ — как обычно;\n" +
        "• с системным VPN — эти вопросы Windows и сами приложения лаунчера задают только через VPN (если у VPN указан " +
        "свой DNS-сервер). Другие программы, которые ищут адреса сами, не ограничиваются: так ваша VPN-программа " +
        "находит свой сервер.\n\n" +
        "На что влияет. Это настройка на весь компьютер, а не только на приложения лаунчера:\n" +
        "• другие программы тоже получают условные адреса или ищут имена через VPN, пока работает приложение лаунчера, " +
        "если спрашивают их у Windows (так делает большинство программ);\n" +
        "• команды ping и nslookup покажут адреса 198.18.x.x — это нормально;\n" +
        "• если у VPN нет своего DNS-сервера, лаунчер DNS не ограничивает, чтобы имена не перестали находиться;\n" +
        "• с системным VPN: если связь VPN оборвалась, а его адаптер остался включённым, имена сайтов не будут находиться " +
        "на всём компьютере, пока работает приложение лаунчера. Если ваша VPN-программа после обрыва не может " +
        "переподключиться, закройте это приложение или выключите настройку.\n\n" +
        "После закрытия приложений лаунчер очищает кэш DNS Windows, и всё работает как без него.";

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

    public VpnRuleMode RuleMode
    {
        get => _draftRuleMode;
        set
        {
            if (_draftRuleMode == value) return;
            _draftRuleMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAdapterSelectionEnabled));

            // Переключились на «Один из»/«Все», а ничего не отмечено: отмечаем активные сейчас VPN,
            // иначе после применения VPN тут же стал бы «отключённым» и сработал бы Kill-Switch.
            if (value != VpnRuleMode.AnyVpn && _draftSelected.Count == 0)
            {
                _draftSelected.AddRange(_monitor.Current.Adapters
                    .Where(a => a.IsVpnLike && a.IsUp)
                    .Select(a => a.Name));
                SyncAdapters(_monitor.Current);
            }

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

    /// <summary>Режим «Один из»/«Все», но ни один адаптер не отмечен: показываем предупреждение.</summary>
    public bool NeedsAdapterSelection => _draftRuleMode != VpnRuleMode.AnyVpn && _draftSelected.Count == 0;

    /// <summary>Правило для системного VPN словами (черновик): «Любой активный VPN».</summary>
    public string RuleDescription
    {
        get
        {
            var text = AppSettingsViewModel.DescribeRule(_draftRuleMode, _draftSelected.Count);
            return char.ToUpper(text[0]) + text[1..];
        }
    }

    /// <summary>Переключатели адаптеров имеют смысл только в режимах «выбранные».</summary>
    public bool IsAdapterSelectionEnabled => _draftRuleMode != VpnRuleMode.AnyVpn;

    /// <summary>Показывать все адаптеры, а не только похожие на VPN. Влияет только на список, сразу и без сохранения.</summary>
    public bool ShowAllAdapters
    {
        get => _showAllAdapters;
        set
        {
            if (!SetField(ref _showAllAdapters, value)) return;
            RememberAdapters(_monitor.Current);
            SyncAdapters(_monitor.Current);
            OnPropertyChanged(nameof(ShowOtherGroup));
            OnPropertyChanged(nameof(AllAdaptersToggleText));
        }
    }

    /// <summary>Группа «Другие адаптеры» видна только при «Показывать все адаптеры».</summary>
    public bool ShowOtherGroup => ShowAllAdapters && OtherAdapters.Count > 0;

    /// <summary>Кнопка под списком: «Развернуть: все адаптеры (ещё 5)» / «Свернуть».</summary>
    public string AllAdaptersToggleText => ShowAllAdapters
        ? "Свернуть"
        : OtherAdapters.Count > 0 ? $"Развернуть: все адаптеры (ещё {OtherAdapters.Count})" : "Развернуть: все адаптеры";

    public bool HasRemovedAdapters => RemovedAdapters.Count > 0;

    /// <summary>Раскрыт ли список «удалённых» адаптеров (по умолчанию свёрнут).</summary>
    public bool IsRemovedExpanded
    {
        get => _isRemovedExpanded;
        set
        {
            if (SetField(ref _isRemovedExpanded, value))
                OnPropertyChanged(nameof(RemovedToggleText));
        }
    }

    public string RemovedToggleText => $"{(IsRemovedExpanded ? "Скрыть" : "Показать")} ({RemovedAdapters.Count})";

    // ---------------------------------------------------------------- применение настроек

    private void OnDraftChanged()
    {
        OnPropertyChanged(nameof(RuleDescription));
        OnPropertyChanged(nameof(NeedsAdapterSelection));
        OnPropertyChanged(nameof(NeedsConnectionSelection));
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    /// <summary>Сбросить черновик к действующим настройкам.</summary>
    private void ResetDraft()
    {
        _draftKillSwitch = _settings.KillSwitchEnabled;
        _draftRequireVpn = _settings.RequireVpn;
        _draftBlockBypass = _settings.BlockBypass;
        _draftHideDns = _settings.HideDns;
        _draftStartWithWindows = _settings.StartWithWindows;
        _draftNetworkMode = _settings.NetworkMode;
        _draftConnectionId = _settings.ConnectionId;
        _draftRuleMode = _settings.RuleMode;
        _draftHideBehavior = _settings.HideBehavior;
        _draftSelected.Clear();
        _draftSelected.AddRange(_settings.SelectedAdapters);
        ResetLogDraft();

        OnPropertyChanged(nameof(KillSwitchEnabled));
        OnPropertyChanged(nameof(RequireVpn));
        OnPropertyChanged(nameof(BlockBypass));
        OnPropertyChanged(nameof(HideDns));
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(NetworkMode));
        OnPropertyChanged(nameof(IsLauncherMode));
        OnPropertyChanged(nameof(IsSystemMode));
        OnPropertyChanged(nameof(DraftConnectionId));
        OnPropertyChanged(nameof(RuleMode));
        OnPropertyChanged(nameof(HideBehavior));
        OnPropertyChanged(nameof(IsAdapterSelectionEnabled));
        OnDraftChanged();

        if (_monitor is not null)
            SyncAdapters(_monitor.Current);
    }

    /// <summary>Применить черновик. false — не применено (что исправить, уже показано).</summary>
    private bool ApplySettings()
    {
        if (!HasPendingChanges) return true;

        if (NeedsConnectionSelection)
        {
            Section = SettingsSection.Connection;
            _dialogs.ShowWarning(_settings.Connections.Count == 0
                    ? "Чтобы приложения шли через подключение лаунчера, сначала добавьте подключение в разделе «Подключения лаунчера»."
                    : "Выберите подключение, через которое пойдут приложения.",
                "Подключение не выбрано");
            return false;
        }

        if (!ConfirmLogDraft())
            return false;

        var killSwitchChanged = _draftKillSwitch != _settings.KillSwitchEnabled;
        var ruleChanged = _draftRuleMode != _settings.RuleMode ||
                          !new HashSet<string>(_draftSelected, StringComparer.OrdinalIgnoreCase).SetEquals(_settings.SelectedAdapters);
        var modeChanged = _draftNetworkMode != _settings.NetworkMode ||
                          _draftNetworkMode == GlobalNetworkMode.LauncherConnection && _draftConnectionId != _settings.ConnectionId;
        var requireChanged = _draftRequireVpn != _settings.RequireVpn;
        var bypassChanged = _draftBlockBypass != _settings.BlockBypass;
        var hideDnsChanged = _draftHideDns != _settings.HideDns;
        var autoStartChanged = _draftStartWithWindows != _settings.StartWithWindows;
        var running = AffectedRunningApps(a => a.Network.Mode == AppNetworkMode.Inherit);
        var coreRunning = AffectedRunningApps(a => NetworkPlan.For(a, _settings).UsesLauncherCore);
        if (autoStartChanged && !TrySetAutoStart(_draftStartWithWindows))
        {
            _draftStartWithWindows = _settings.StartWithWindows;
            OnPropertyChanged(nameof(StartWithWindows));
            autoStartChanged = false;
        }

        _settings.KillSwitchEnabled = _draftKillSwitch;
        _settings.RequireVpn = _draftRequireVpn;
        _settings.BlockBypass = _draftBlockBypass;
        _settings.HideDns = _draftHideDns;
        _settings.StartWithWindows = _draftStartWithWindows;
        _settings.NetworkMode = _draftNetworkMode;
        if (_draftNetworkMode == GlobalNetworkMode.LauncherConnection)
            _settings.ConnectionId = _draftConnectionId;
        _settings.RuleMode = _draftRuleMode;
        _settings.HideBehavior = _draftHideBehavior;
        _settings.SelectedAdapters = new List<string>(_draftSelected);
        ApplyLogDraft();
        ApplyNetworkSettings();
        if (!_settings.KillSwitchEnabled)
            _tracker.StopBlocking();
        SaveSettings();

        OnPropertyChanged(nameof(IsKillSwitchOff));
        OnDraftChanged();
        RebuildConnections();

        if (killSwitchChanged)
            _log.Add(LogKind.Info, _settings.KillSwitchEnabled ? "Kill-Switch включён" : "Kill-Switch выключен");
        if (requireChanged)
            _log.Add(LogKind.Info, _settings.RequireVpn ? "«Не запускать без VPN» включено" : "«Не запускать без VPN» выключено");
        if (bypassChanged)
            _log.Add(LogKind.Info, _settings.BlockBypass ? "Блокировка трафика мимо VPN включена" : "Блокировка трафика мимо VPN выключена");
        if (hideDnsChanged)
            _log.Add(LogKind.Info, _settings.HideDns ? "Скрытие DNS-запросов включено (на весь компьютер, пока работают приложения лаунчера)" : "Скрытие DNS-запросов выключено");
        if (autoStartChanged)
            _log.Add(LogKind.Info, _settings.StartWithWindows ? "Лаунчер будет запускаться вместе с Windows" : "Автозапуск лаунчера выключен");
        if (modeChanged)
            _log.Add(LogKind.Info, $"Общее условие запуска: {DescribeGlobal()}");
        if (ruleChanged)
            _log.Add(LogKind.Info, $"Новое правило: {DescribeRule()}");

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
            Toast("Настройки применены", DescribeRule(), GlyphCheck, ToastKind.Success);
        }

        // Новое правило проверяется сразу; если VPN по нему не подключён, сработает обычная логика обрыва.
        _monitor.SetRule(BuildRule());
        RefreshRunningCounts();

        if (modeChanged && running.Count > 0)
            _dialogs.ShowWarning($"Работающие приложения ({string.Join(", ", running)}) продолжат идти так же, как раньше. " +
                                 "Новое условие запуска подействует, когда их перезапустят из лаунчера.", "Нужен перезапуск");
        else if (hideDnsChanged && coreRunning.Count > 0)
            _dialogs.ShowWarning($"Для приложений через подключение лаунчера ({string.Join(", ", coreRunning)}) новое значение " +
                                 "«Скрывать DNS-запросы» подействует, когда их перезапустят из лаунчера.", "Нужен перезапуск");
        return true;
    }

    /// <summary>Как сейчас настроено общее условие запуска, словами.</summary>
    private string DescribeGlobal() => _settings.NetworkMode == GlobalNetworkMode.LauncherConnection
        ? $"подключение лаунчера «{_settings.Connections.FirstOrDefault(c => c.Id == _settings.ConnectionId)?.Name ?? "не выбрано"}»"
        : "системный VPN";

    /// <summary>Работающие приложения, которых касается изменение (их сеть поменяется только после перезапуска).</summary>
    private List<string> AffectedRunningApps(Func<AppEntry, bool> affected)
    {
        var running = new HashSet<Guid>(_tracker.RunningApps());
        return _settings.Apps.Where(a => running.Contains(a.Id) && affected(a)).Select(a => a.Name).ToList();
    }

    private string DescribeRule() => _settings.RuleMode switch
    {
        VpnRuleMode.AnyOfSelected => $"VPN: один из выбранных ({_settings.SelectedAdapters.Count})",
        VpnRuleMode.AllOfSelected => $"VPN: все выбранные ({_settings.SelectedAdapters.Count})",
        _ => "VPN: любой активный"
    };

    // ---------------------------------------------------------------- VPN и Kill-Switch

    /// <summary>Вызывается в фоновом потоке монитора.</summary>
    private void OnVpnStatusChanged(object? sender, VpnStatusChangedEventArgs e)
    {
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
    /// <param name="unprotected">Работающие приложения без «Закрывать при обрыве»: остались без VPN.</param>
    private void OnVpnLost(KillReport? report, IReadOnlyList<string> unprotected)
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        string subtitle;

        if (report is { Killed.Count: > 0 } or { Failed.Count: > 0 })
            subtitle = (report.Killed.Count > 0 ? "Kill-Switch закрыл: " + string.Join(", ", report.Killed) : "Kill-Switch не смог закрыть приложения") +
                       (report.Failed.Count > 0 ? " · не закрылись: " + string.Join(", ", report.Failed) : string.Empty);
        else if (unprotected.Count > 0)
            subtitle = (_settings.BlockBypass ? "Ждут VPN без сети: " : "Работают без VPN: ") + string.Join(", ", unprotected);
        else
            subtitle = "Запущенных приложений не было";

        StatusMessage = $"{time} — VPN отключён. {subtitle}";
        Toast("VPN отключён", subtitle, GlyphUnlock, ToastKind.Danger);

        _log.Add(LogKind.VpnDown, $"VPN отключён: {_monitor.Current.Explanation}" +
                                       (unprotected.Count > 0
                                           ? " · не закрывались (Kill-Switch выключен): " + string.Join(", ", unprotected) +
                                             (_settings.BlockBypass ? " — сеть для них закрыта, пока VPN не вернётся" : string.Empty)
                                           : string.Empty));
        if (report is { Killed.Count: > 0 })
            _log.Add(LogKind.Closed, $"Kill-Switch закрыл: {string.Join(", ", report.Killed)}");
        if (report is { Failed.Count: > 0 })
            _log.Add(LogKind.Failed, $"Не удалось закрыть, работают без VPN: {string.Join(", ", report.Failed)}");

        // Отдельный диалог — только если что-то закрыть не удалось: это требует действий пользователя.
        if (report is { Failed.Count: > 0 })
        {
            var text = "Эти приложения продолжают работать без VPN. Закройте их вручную:\n\n" + string.Join("\n", report.Failed);
            if (report.Killed.Count > 0)
                text += "\n\nЗакрыто: " + string.Join(", ", report.Killed);
            _dialogs.ShowWarning(text, "Не все приложения закрыты");
        }
    }

    /// <summary>Ядро VPN упало или пропала служба: приложения через подключения лаунчера уже закрыты.</summary>
    private void OnCoreLost(string reason, KillReport report)
    {
        var subtitle = report.Killed.Count > 0
            ? "Закрыты: " + string.Join(", ", report.Killed)
            : "Запущенных приложений не было";
        StatusMessage = $"{DateTime.Now:HH:mm:ss} — {reason} {subtitle}";
        Toast("Подключение лаунчера выключилось", subtitle, GlyphUnlock, ToastKind.Danger, "Выгрузить журналы", () => ShowExport(ExportPeriods.Last15));
        _log.Add(LogKind.VpnDown, reason);
        if (report.Killed.Count > 0)
            _log.Add(LogKind.Closed, $"Закрыты без подключения: {string.Join(", ", report.Killed)}");
        ShowFailedKills(report);
        RefreshRunningCounts();
    }

    /// <summary>
    /// Перед запуском сервер не ответил через ядро. В журнал — что сказало ядро и итог отдельной проверки того же
    /// подключения (без адаптера лаунчера): так видно, виноват сервер или работа через адаптер.
    /// </summary>
    private async Task OnProbeFailedAsync(ProbeFailure failure)
    {
        // Строки ядра пишет технический журнал (LauncherNetwork), в Журнале событий только понятный итог.
        _log.Add(LogKind.Failed, failure.Summary);

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
        if (info.Waiting.Count > 0)
            parts.Add("Ждут сеть: " + string.Join(", ", info.Waiting));
        var subtitle = parts.Count > 0 ? string.Join(" · ", parts) : "Запущенных приложений не было";

        StatusMessage = $"{DateTime.Now:HH:mm:ss} — сервер «{info.ConnectionName}» не отвечает. {subtitle}";
        Toast($"Сервер «{info.ConnectionName}» не отвечает", subtitle, GlyphUnlock, ToastKind.Danger, "Выгрузить журналы", () => ShowExport(ExportPeriods.Last15));
        _log.Add(LogKind.VpnDown, $"Сервер подключения «{info.ConnectionName}» не отвечает · {subtitle}");
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
        _dialogs.ShowWarning("Эти приложения продолжают работать без защиты. Закройте их вручную:\n\n" + string.Join("\n", report.Failed),
            "Не все приложения закрыты");
    }

    /// <summary>
    /// Сетевые настройки в силу: у каждого приложения своя защита (закрывать ли вместе с лаунчером, идёт ли оно
    /// через ядро), план ядра и правила адаптеров для монитора. Вызывается при запуске и после изменения настроек.
    /// </summary>
    private void ApplyNetworkSettings()
    {
        var adapterApps = new List<AdapterApp>();
        foreach (var entry in _settings.Apps)
        {
            var plan = NetworkPlan.For(entry, _settings);
            _tracker.SetPolicy(entry, plan.UsesLauncherCore, ClosesWithLauncher(plan));
            if (plan.Kind == NetworkPlanKind.SystemAdapters)
                adapterApps.Add(new AdapterApp(entry.Id, entry.Name, plan.AdapterRule!, plan.CloseOnDrop));
        }

        _adapterApps = adapterApps;
        var ipv6 = CorePlan.DetectIpv6();
        if (ipv6 != _lastIpv6)
        {
            _lastIpv6 = ipv6;
            TechLog.Info(LogAreas.Network, LogCodes.Ipv6Detect, ipv6 ? "IPv6 в Windows есть: ядро получит адрес IPv6" : "IPv6 в Windows нет");
        }
        _network.Update(CorePlan.Build(_settings, ipv6));
        SubmitGuard();
    }

    // ---------------------------------------------------------------- блокировка трафика мимо VPN

    /// <summary>Нужны ли приложению с такими условиями фильтры Windows (иначе его запуск от них не зависит).</summary>
    private bool NeedsGuard(NetworkPlan plan) =>
        plan.Kind == NetworkPlanKind.LauncherConnection && _settings.BlockBypass ||
        plan.Kind == NetworkPlanKind.SystemAdapters && plan.RequireVpn && (_settings.BlockBypass || _settings.HideDns);

    /// <summary>
    /// Какие фильтры должны действовать сейчас: работающие (и запускаемые) приложения пускаются в сеть только через свой
    /// VPN — адаптер ядра или адаптеры системного VPN по их правилу (VPN не подключён — никуда). Приложения под системным VPN
    /// без «Не запускать без VPN» не ограничиваются: им разрешено работать и без VPN. Только в UI-потоке.
    /// </summary>
    private GuardRequest BuildGuardRequest()
    {
        var request = new GuardRequest();
        if (_monitor is null || !_settings.BlockBypass && !_settings.HideDns)
            return request;

        var adapters = _monitor.Current.Adapters;
        var running = new HashSet<Guid>(_tracker.RunningApps());
        running.UnionWith(_guardPending);
        var dns = new SortedSet<int>();
        var dnsPaths = new List<string>();
        foreach (var entry in _settings.Apps.Where(a => running.Contains(a.Id)))
        {
            var plan = NetworkPlan.For(entry, _settings);
            if (plan.Kind == NetworkPlanKind.LauncherConnection)
            {
                if (_settings.BlockBypass && GuardPaths(entry) is { Count: > 0 } paths)
                    request.Apps.Add(new GuardRule { Paths = paths, ViaCore = true, LoopbackPorts = ViaLocalProxy(plan) ? null : _proxy.Current?.Ports.ToList() });
                continue;
            }

            if (plan.Kind != NetworkPlanKind.SystemAdapters || !plan.RequireVpn)
                continue;
            var snapshot = VpnMonitor.Apply(plan.AdapterRule!, adapters);
            var vpn = snapshot.IsConnected
                ? adapters.Where(a => a.IsUp && a.Index > 0 && snapshot.ActiveVpnNames.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).ToList()
                : new List<VpnAdapterInfo>();
            if (_settings.BlockBypass && GuardPaths(entry) is { Count: > 0 } appPaths)
                request.Apps.Add(new GuardRule { Paths = appPaths, Interfaces = vpn.Select(a => a.Index).Distinct().ToList() });
            if (_settings.HideDns)
            {
                dns.UnionWith(vpn.Where(a => a.HasDns).Select(a => a.Index));
                // Без блокировки мимо VPN фильтра у приложения нет: его собственный DNS закрывает правило DNS.
                if (!_settings.BlockBypass)
                    dnsPaths.AddRange(GuardPaths(entry));
            }
        }

        if (dns.Count > 0)
        {
            request.DnsInterfaces = dns.ToList();
            if (dnsPaths.Count > 0)
                request.DnsPaths = dnsPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return request;
    }

    /// <summary>
    /// Программы приложения: его exe, работающие процессы и замеченные раньше за этот сеанс. Пути настоящие
    /// (<see cref="GuardPath"/>): служба работает в другом сеансе Windows и дисков subst и сетевых дисков пользователя не видит.
    /// </summary>
    private List<string> GuardPaths(AppEntry entry)
    {
        var paths = new List<string>(_tracker.ImagePaths(entry.Id));
        if (_guardPaths.TryGetValue(entry.Id, out var seen))
            paths.AddRange(seen);
        if (!entry.IsPackaged && !string.IsNullOrWhiteSpace(entry.ExePath) && File.Exists(entry.ExePath))
            paths.Add(entry.ExePath);
        return paths.Select(GuardPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
    /// программы отдали бы трафик ему, то есть другой VPN-программе. С «Блокировать трафик мимо VPN» путь к нему закрыт,
    /// без неё — только предупреждение. Один раз на каждый новый прокси. Только в UI-потоке.
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
        if (!_settings.Apps.Any(a => running.Contains(a.Id) && NetworkPlan.For(a, _settings) is { Kind: NetworkPlanKind.LauncherConnection } plan && !ViaLocalProxy(plan)) ||
            _warnedProxy == proxy.Display)
            return;
        _warnedProxy = proxy.Display;

        if (_settings.BlockBypass)
        {
            _log.Add(LogKind.Info, $"В Windows включён системный прокси {proxy.Display} — обычно его включает другая VPN-программа. " +
                                   "Приложениям, которые идут через подключение лаунчера, путь к нему закрыт: иначе их трафик ушёл бы мимо подключения. " +
                                   "Если такое приложение не открывает сайты, выключите системный прокси в той программе.");
            Toast("Системный прокси закрыт для приложений", $"Прокси {proxy.Display} ведёт мимо подключения. Если сайты не открываются, выключите его в той VPN-программе.",
                GlyphLock, ToastKind.Info);
        }
        else
        {
            _log.Add(LogKind.Info, $"В Windows включён системный прокси {proxy.Display} — обычно его включает другая VPN-программа. " +
                                   "Приложения, которые идут через подключение лаунчера, могут отдавать трафик ему, то есть мимо подключения. " +
                                   "Выключите системный прокси в той программе или включите «Блокировать трафик мимо VPN».");
            Toast("Системный прокси ведёт мимо подключения", $"Приложения могут ходить через прокси {proxy.Display}. Выключите его в той VPN-программе.",
                GlyphUnlock, ToastKind.Danger);
        }
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

    /// <summary>Ответ ядру «чей процесс»: метка приложения через подключение лаунчера, иначе null (напрямую). Любой поток.</summary>
    private string? ResolveCoreTag(uint pid) =>
        _tracker.TryGetRoutedOwner((int)pid, out var appId) ? _network.TagFor(appId) : null;

    /// <summary>Приложения, которые закроются вместе с лаунчером (см. <see cref="ClosesWithLauncher"/>).</summary>
    private List<Guid> AppsClosedOnExit()
    {
        var running = new HashSet<Guid>(_tracker.RunningApps());
        return _settings.Apps
            .Where(a => running.Contains(a.Id) && ClosesWithLauncher(NetworkPlan.For(a, _settings)))
            .Select(a => a.Id)
            .ToList();
    }

    /// <summary>
    /// Приложение закрывается вместе с лаунчером, в том числе при его сбое: через подключение лаунчера, с «Закрывать при
    /// обрыве» или под «Блокировать трафик мимо VPN» — фильтры Windows действуют, только пока лаунчер работает.
    /// </summary>
    private bool ClosesWithLauncher(NetworkPlan plan) =>
        plan.UsesLauncherCore || plan.CloseOnDrop ||
        plan.Kind == NetworkPlanKind.SystemAdapters && plan.RequireVpn && _settings.BlockBypass;

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
            if (changed)
            {
                ApplyNetworkSettings();
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
        RememberAdapters(snapshot);
        SyncAdapters(snapshot);
        RefreshStatus();
    }

    private VpnRule BuildRule() => new(_settings.RuleMode, _settings.SelectedAdapters);

    private KnownAdapter? FindKnown(string name) =>
        _settings.KnownAdapters.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool IsSelectedAnywhere(string name) =>
        _draftSelected.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        _settings.SelectedAdapters.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Показывать ли адаптер в списках: VPN, выбранные, а при «Показывать все адаптеры» — все.</summary>
    private bool IsListed(string name, bool isVpnLike) => ShowAllAdapters || isVpnLike || IsSelectedAnywhere(name);

    /// <summary>
    /// Запоминаем каждый замеченный адаптер, который попадает в список. Монитор присылает снимок при любом
    /// изменении набора адаптеров, поэтому ловятся и те, что существуют только пока VPN подключён.
    /// </summary>
    private void RememberAdapters(VpnSnapshot snapshot)
    {
        var changed = false;
        foreach (var a in snapshot.Adapters)
        {
            if (!IsListed(a.Name, a.IsVpnLike)) continue;

            var known = FindKnown(a.Name);
            if (known is null)
            {
                _settings.KnownAdapters.Add(new KnownAdapter { Name = a.Name, Description = a.Description, Product = a.Product, IsVpnLike = a.IsVpnLike });
                changed = true;
            }
            else if (known.Description != a.Description || known.Product != a.Product || known.IsVpnLike != a.IsVpnLike)
            {
                known.Description = a.Description;
                known.Product = a.Product;
                known.IsVpnLike = a.IsVpnLike;
                changed = true;
            }
        }

        if (changed)
            SaveSettings();
    }

    private readonly record struct AdapterRow(string Name, string Description, string Product, bool IsVpnLike, bool IsUp);

    /// <summary>
    /// Раскладывает адаптеры по трём группам и обновляет списки на месте: существующие строки не пересоздаются,
    /// поэтому переключатели не перерисовываются и не дёргаются, а новые строки просто добавляются.
    /// </summary>
    private void SyncAdapters(VpnSnapshot snapshot)
    {
        // Основная группа: похожие на VPN и уже выбранные в действующих правилах.
        // Отметка в черновике группу не меняет, чтобы строка не «прыгала» сразу после клика.
        bool IsPrimary(string name, bool isVpnLike) =>
            isVpnLike || _settings.SelectedAdapters.Contains(name, StringComparer.OrdinalIgnoreCase);

        var present = snapshot.Adapters
            .Select(a => new AdapterRow(a.Name, a.Description, a.Product, a.IsVpnLike, a.IsUp))
            .ToList();

        var presentNames = new HashSet<string>(snapshot.Adapters.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        var removed = _settings.KnownAdapters
            .Where(k => !presentNames.Contains(k.Name) && IsListed(k.Name, k.IsVpnLike))
            .OrderBy(k => k.Product, StringComparer.CurrentCultureIgnoreCase)
            .Select(k => new AdapterRow(k.Name, k.Description, k.Product, k.IsVpnLike, false))
            .ToList();

        SyncList(Adapters, present.Where(r => IsPrimary(r.Name, r.IsVpnLike)).ToList(), isPresent: true);
        SyncList(OtherAdapters, present.Where(r => !IsPrimary(r.Name, r.IsVpnLike)).ToList(), isPresent: true);
        SyncList(RemovedAdapters, removed, isPresent: false);

        OnPropertyChanged(nameof(ShowOtherGroup));
        OnPropertyChanged(nameof(AllAdaptersToggleText));
        OnPropertyChanged(nameof(HasRemovedAdapters));
        OnPropertyChanged(nameof(RemovedToggleText));
    }

    private void SyncList(ObservableCollection<VpnAdapterViewModel> target, List<AdapterRow> rows, bool isPresent)
    {
        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!rows.Any(r => Same(r.Name, target[i].Name)))
                target.RemoveAt(i);
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var selected = _draftSelected.Contains(r.Name, StringComparer.OrdinalIgnoreCase);

            var index = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (Same(target[j].Name, r.Name)) { index = j; break; }
            }

            if (index < 0)
            {
                target.Insert(i, new VpnAdapterViewModel(r.Name, r.Description, r.Product, r.IsVpnLike, r.IsUp, isPresent,
                    selected, OnAdapterSelectionChanged, isPresent ? null : ForgetAdapter));
                continue;
            }

            if (index != i)
                target.Move(index, i);

            var vm = target[i];
            vm.IsUp = r.IsUp;
            vm.Description = r.Description;
            vm.Product = r.Product;
            vm.SetSelectedSilently(selected);
        }
    }

    private void OnAdapterSelectionChanged(VpnAdapterViewModel adapter)
    {
        _draftSelected.RemoveAll(n => string.Equals(n, adapter.Name, StringComparison.OrdinalIgnoreCase));
        if (adapter.IsSelected)
            _draftSelected.Add(adapter.Name);

        OnDraftChanged();
    }

    /// <summary>Убрать адаптер из списка «удалённых». Если он появится снова, лаунчер запомнит его заново.</summary>
    private void ForgetAdapter(VpnAdapterViewModel adapter)
    {
        _settings.KnownAdapters.RemoveAll(k => string.Equals(k.Name, adapter.Name, StringComparison.OrdinalIgnoreCase));
        SaveSettings();

        // Был выбран в правилах: из черновика тоже убираем, в силу это вступит по «Применить».
        _draftSelected.RemoveAll(n => string.Equals(n, adapter.Name, StringComparison.OrdinalIgnoreCase));
        OnDraftChanged();

        SyncAdapters(_monitor.Current);
        if (RemovedAdapters.Count == 0)
            IsRemovedExpanded = false;
    }

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
            new RelayCommand(() => OpenAppSettings(tile!)));
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

    /// <summary>«Добавить приложение»: выбрать файл и открыть страницу нового приложения (условие запуска, защита).</summary>
    private void AddApp()
    {
        var path = _dialogs.PickExecutableOrShortcut();
        if (path is null) return;
        AddFromPaths(new[] { path });
    }

    /// <summary>
    /// Файлы, перетащенные в окно (или выбранные в «Добавить приложение»). Один — открываем его страницу, чтобы
    /// сразу выбрать условие запуска; несколько — добавляем «как в общих», настроить можно потом.
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
        RefreshRunningCounts();
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

        var duplicate = appUserModelId is not null
            ? _settings.Apps.Any(a => string.Equals(a.AppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase))
            : _settings.Apps.Any(a => !a.IsPackaged
                                      && string.Equals(a.ExePath, exePath, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(a.Arguments ?? "", arguments ?? "", StringComparison.Ordinal));
        if (duplicate)
        {
            Fail($"«{name}» уже есть в списке.", warning: true);
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
        var entry = tile.Entry;
        if (_launching.Contains(entry.Id))
            return;

        // Все строки этого запуска (лаунчер, служба, ядро) с одним id операции. Второй заход после журнала DNS продолжает тот же.
        using var operation = dnsReady && TechLog.CurrentCid is not null ? null : TechLog.Begin(out _);
        var started = Stopwatch.StartNew();
        if (!dnsReady)
            TechLog.Info(LogAreas.App, LogCodes.AppLaunch, $"Запуск «{entry.Name}»: {DescribeLaunch(entry)}");
        _launching.Add(entry.Id);

        try
        {
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

        // Журнал DNS включён, но запись ещё не идёт: сначала запускаем её, чтобы не пропустить первые запросы.
        if (!dnsReady && entry.TrackDns && !_dns.IsActive)
        {
            StartDnsThenLaunch(tile);
            return;
        }

        // Приложение добавлено ярлыком: запускаем то, на что ярлык указывает сейчас.
        RefreshFromShortcut(entry);

        if (!entry.IsPackaged && !File.Exists(entry.ExePath) && !TryRelocate(entry))
        {
            TechLog.Warn(LogAreas.App, LogCodes.AppReject, $"«{entry.Name}» не запущено: файл программы не найден ({entry.ExePath})");
            _dialogs.ShowError($"Файл программы не найден:\n{entry.ExePath}");
            return;
        }

        var plan = NetworkPlan.For(entry, _settings);

        // Фильтры ставятся до запуска: с первого соединения программа не выйдет в сеть мимо VPN.
        // Не поставились — приложение не запускается (защита важнее).
        if (NeedsGuard(plan))
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
                _log.Add(LogKind.Blocked, $"Запуск «{entry.Name}» отклонён: не удалось включить блокировку трафика мимо VPN — {guardError}");
                _dialogs.ShowWarning($"«{entry.Name}» не запущено: не удалось включить защиту от трафика мимо VPN.\n\n{guardError}\n\n" +
                                     "Если ошибка повторяется, переустановите лаунчер. Запускать без этой защиты можно, выключив " +
                                     "«Блокировать трафик мимо VPN» в настройках, раздел «Защита».",
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
            if (_settings.BlockBypass && !entry.IsPackaged && _guard.IsUnguarded(GuardPath(entry.ExePath)))
            {
                _guardPending.Remove(entry.Id);
                SubmitGuard();
                _log.Add(LogKind.Blocked, $"Запуск «{entry.Name}» отклонён: программа на сетевом диске или недоступна службе лаунчера, " +
                                          $"блокировку трафика мимо VPN для неё не включить ({entry.ExePath})");
                _dialogs.ShowWarning($"«{entry.Name}» не запущено: программа лежит на сетевом диске или недоступна службе лаунчера, " +
                                     $"поэтому блокировку трафика мимо VPN для неё включить нельзя.\n\n{entry.ExePath}\n\n" +
                                     "Скопируйте программу на этот компьютер или выключите «Блокировать трафик мимо VPN» в настройках, раздел «Защита».",
                    "Защита не включилась");
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
            result = entry.IsPackaged ? _tracker.LaunchPackaged(entry) : _tracker.Launch(entry, withoutProxy: plan.UsesLauncherCore || plan.RequireVpn);
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
        if (result.ProcessId == 0)
        {
            _log.Add(LogKind.Failed, $"«{entry.Name}» запущена, но без контроля: Windows не вернула её процесс");
            _dialogs.ShowWarning($"«{entry.Name}» запущена, но Windows не вернула её процесс, поэтому Kill-Switch не сможет её закрыть.");
            return;
        }

        var via = plan.UsesLauncherCore ? $"через «{plan.Connection?.Name}»" : null;
        StatusMessage = $"{DateTime.Now:HH:mm:ss} — запущено: {entry.Name} (PID {result.ProcessId})" + (via is null ? string.Empty : " " + via) +
                        (result.Adopted > 0 ? $", взято под контроль уже работавших: {ProcessTracker.ProcessesText(result.Adopted)}" : string.Empty);
        Toast($"Запущено: {entry.Name}",
            plan.UsesLauncherCore ? $"Через подключение «{plan.Connection?.Name}»"
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

        // Закрываем окно гонки: VPN мог упасть между проверкой и стартом процесса,
        // и тогда событие монитора уже отработало, когда приложения ещё не было в списке.
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
    /// Условие запуска. Под системным VPN — VPN по правилу приложения подключён. Через подключение лаунчера — копий мимо
    /// лаунчера нет (или пользователь согласился их закрыть), ядро работает с текущими настройками, сервер отвечает.
    /// false — запускать нельзя, причина уже показана.
    /// </summary>
    private async Task<bool> PrepareNetworkAsync(AppTileViewModel tile)
    {
        var entry = tile.Entry;
        var plan = NetworkPlan.For(entry, _settings);

        if (plan.Kind == NetworkPlanKind.SystemAdapters)
        {
            // Статус проверяем заново прямо сейчас, а не берём из кэша.
            if (plan.RequireVpn && !VpnMonitor.Apply(plan.AdapterRule!, _monitor.Evaluate().Adapters).IsConnected)
            {
                _log.Add(LogKind.Blocked, $"Запуск «{entry.Name}» отклонён: VPN не подключён");
                _dialogs.ShowWarning("Для запуска этого приложения необходимо подключиться к VPN.", "Нет подключения к VPN");
                return false;
            }

            return true;
        }

        // Подписку ещё ни разу не скачали: без списка серверов подключиться не к чему.
        if (plan.Connection is { Kind: ConnectionKind.Subscription, SubscriptionServers.Count: 0 } subscription)
        {
            tile.LaunchStage = "Загружаю подписку…";
            await UpdateSubscriptionAsync(subscription, userInitiated: true);
        }

        ApplyNetworkSettings();

        // Копия, запущенная мимо лаунчера, уже ходит напрямую, а новая копия обычно просто передаёт ей управление.
        var outside = _tracker.RefreshOutside(entry.Id);
        if (outside > 0)
        {
            if (!_dialogs.Confirm($"«{entry.Name}» уже запущено без защиты",
                    "Эта копия запущена мимо лаунчера, и её соединения идут напрямую. Закрыть её и запустить под защитой? " +
                    "Несохранённые данные в ней пропадут.",
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
        }

        tile.LaunchStage = plan.RequireVpn ? "Подключаю…" : "Включаю подключение…";
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

        tile.LaunchStage = "Запускаю…";
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
        return true;
    }

    private void Remove(AppTileViewModel tile)
    {
        if (!_dialogs.Confirm($"Убрать «{tile.Name}»?", "Приложение пропадёт из лаунчера. Сама программа удалена не будет.", "Убрать",
                destructive: true))
            return;

        _settings.Apps.Remove(tile.Entry);
        SaveSettings();
        ApplyNetworkSettings();
        _log.Add(LogKind.Info, $"Убрано из лаунчера: «{tile.Name}»");
        _tracker.StopBlocking(tile.Entry.Id);
        _dialogs.CloseDnsJournal(tile.Entry.Id);
        _dns.Forget(tile.Entry.Id);
        Apps.Remove(tile);
        OnPropertyChanged(nameof(HasNoApps));
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
        foreach (var tile in Apps)
        {
            var id = tile.Entry.Id;
            tile.RunningCount = _tracker.CountFor(id);
            tile.NetState = _network.StateOf(id);

            var outside = _tracker.OutsideFor(id);
            if (outside > 0 && tile.OutsideCount == 0)
            {
                Toast($"«{tile.Name}» работает без защиты", "Запущено мимо лаунчера · «Перезапустить под защитой» в меню плитки",
                    GlyphUnlock, ToastKind.Danger);
                _log.Add(LogKind.Blocked, $"«{tile.Name}» запущено мимо лаунчера: его соединения идут напрямую");
            }

            tile.OutsideCount = outside;
        }

        OnPropertyChanged(nameof(RunningSummary));
        RefreshStatus();
    }

    /// <summary>«Перезапустить под защитой»: закрыть копию мимо лаунчера и запустить приложение из лаунчера.</summary>
    private void RestartProtected(AppTileViewModel tile) => Launch(tile);

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

        foreach (var tile in Apps)
        {
            var plan = NetworkPlan.For(tile.Entry, _settings);
            anyAdapters |= plan.Kind == NetworkPlanKind.SystemAdapters;
            anyCore |= plan.UsesLauncherCore;

            // Подпись под названием: только если условие отличается от общего или с ним что-то не так.
            tile.IsSubtitleWarning = plan.Kind == NetworkPlanKind.MissingConnection;
            tile.Subtitle = plan.Kind switch
            {
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
                    NetworkPlanKind.MissingConnection => "Не запущено · подключение не выбрано",
                    NetworkPlanKind.LauncherConnection => $"Не запущено · через «{plan.Connection!.Name}»",
                    _ => "Не запущено · системный VPN"
                };
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
                }
            }
            else if (snapshot is not null && VpnMonitor.Apply(plan.AdapterRule!, snapshot.Adapters).IsConnected)
            {
                tile.Status = AppNetStatus.Protected;
                tile.StatusText = "Работает · VPN подключён";
            }
            else
            {
                tile.Status = AppNetStatus.Unprotected;
                tile.StatusText = "Работает без VPN: Kill-Switch у приложения выключен";
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
                : "Добавьте приложение";
            SummaryTone = anyAdapters ? (IsVpnConnected ? SummaryTone.Ok : SummaryTone.Danger) : SummaryTone.Idle;
        }
        else
        {
            SummaryTitle = $"Защищены {protectedCount} из {running}";
            SummarySubtitle = danger ? "Есть приложения без защиты"
                : waiting ? "Сервер не отвечает: приложения ждут сеть"
                : connections.Count > 0 ? "Через " + string.Join(", ", connections.Select(c => $"«{c}»"))
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
            var isDefault = _settings.NetworkMode == GlobalNetworkMode.LauncherConnection && _settings.ConnectionId == item.Id;
            var users = _settings.Apps
                .Where(a => NetworkPlan.For(a, _settings).Connection?.Id == item.Id)
                .Select(a => a.Name)
                .ToList();
            item.IsDefault = isDefault;
            item.UsedBy = (isDefault ? "Основное" : string.Empty) +
                          (users.Count > 0 ? (isDefault ? " · " : string.Empty) + string.Join(", ", users)
                              : isDefault ? string.Empty : "Не используется");
        }

        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(NeedsConnectionSelection));
        AppPage?.SyncConnections(Connections);
    }

    /// <summary>Сохранить новое подключение. makeDefault — сделать его общим (мастер первого запуска).</summary>
    private Task AddConnectionAsync(VpnConnection connection, bool makeDefault)
    {
        connection.Name = ConnectionFactory.UniqueName(connection.Name, _settings.Connections);
        _settings.Connections.Add(connection);

        if (makeDefault)
        {
            _settings.NetworkMode = GlobalNetworkMode.LauncherConnection;
            _settings.ConnectionId = connection.Id;
            ResetDraft();
        }
        else if (_settings.Connections.Count == 1 && _draftConnectionId is null)
        {
            // Первое подключение: сразу предлагаем его в общем выборе (вступит в силу по «Применить»).
            _draftConnectionId = connection.Id;
            OnPropertyChanged(nameof(DraftConnectionId));
            OnDraftChanged();
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
        var running = new HashSet<Guid>(_tracker.RunningApps());
        var busy = users.Where(a => running.Contains(a.Id)).Select(a => a.Name).ToList();
        if (busy.Count > 0)
        {
            _dialogs.ShowWarning($"Через «{item.Name}» сейчас работают: {string.Join(", ", busy)}. Закройте их, потом удаляйте подключение.",
                "Подключение занято");
            return;
        }

        var message = users.Count > 0
            ? $"Им пользуются: {string.Join(", ", users.Select(a => a.Name))}. Они не запустятся, пока им не выберут другое подключение."
            : "Ссылка или подписка будут удалены из лаунчера.";
        if (!_dialogs.Confirm($"Удалить «{item.Name}»?", message, "Удалить", destructive: true))
            return;

        _settings.Connections.Remove(item.Connection);
        foreach (var app in _settings.Apps.Where(a => a.Network.ConnectionId == id))
            app.Network.ConnectionId = null;
        if (_settings.ConnectionId == id)
            _settings.ConnectionId = null;
        if (_draftConnectionId == id)
        {
            _draftConnectionId = _settings.ConnectionId;
            OnPropertyChanged(nameof(DraftConnectionId));
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
        AppPage = new AppSettingsViewModel(entry, isNew, _settings, icon, AdapterOptions(), Connections, add, _dialogs,
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

    /// <summary>Адаптеры для «Свои адаптеры»: похожие на VPN, которые есть сейчас, и те, что лаунчер уже видел.</summary>
    private List<(string Name, string Product, string Details, bool IsUp)> AdapterOptions()
    {
        var result = new List<(string, string, string, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _monitor.Current.Adapters.Where(a => a.IsVpnLike))
        {
            if (seen.Add(a.Name))
                result.Add((a.Name, a.Product, string.Equals(a.Name, a.Description, StringComparison.OrdinalIgnoreCase) ? a.Name : $"{a.Name} · {a.Description}", a.IsUp));
        }

        foreach (var k in _settings.KnownAdapters.Where(k => k.IsVpnLike))
        {
            if (seen.Add(k.Name))
                result.Add((k.Name, k.Product, "нет в системе", false));
        }

        return result;
    }

    /// <summary>«Сохранить» на странице приложения (для нового — «Добавить»). Черновик уже проверен.</summary>
    private void SaveAppPage(AppSettingsViewModel page, bool navigate)
    {
        var entry = page.Entry;
        var wasTracking = entry.TrackDns;
        var oldNetwork = System.Text.Json.JsonSerializer.Serialize(entry.Network);
        page.ApplyTo(entry);

        if (page.IsNew)
        {
            _settings.Apps.Add(entry);
            SaveSettings();
            ApplyNetworkSettings();
            Apps.Add(CreateTile(entry));
            OnPropertyChanged(nameof(HasNoApps));
            if (entry.TrackDns)
                _dns.SetTracking(entry.Id, entry.Name, true);
            StatusMessage = $"Добавлено: {entry.Name} → {(entry.IsPackaged ? "Microsoft Store" : entry.ExePath)}";
            Toast("Приложение добавлено", entry.Name, GlyphAdd, ToastKind.Info);
            _log.Add(LogKind.Info, $"Добавлено приложение «{entry.Name}» · {DescribeApp(entry)}");
        }
        else
        {
            SaveSettings();
            ApplyNetworkSettings();
            var tile = Apps.FirstOrDefault(t => t.Entry == entry);
            tile?.RefreshName();
            if (wasTracking != entry.TrackDns)
            {
                _dns.SetTracking(entry.Id, entry.Name, entry.TrackDns);
                if (!entry.TrackDns)
                    _dns.StopIfIdle();
                _log.Add(LogKind.Info, $"Журнал DNS {(entry.TrackDns ? "включён" : "выключен")}: {entry.Name}");
            }

            var networkChanged = oldNetwork != System.Text.Json.JsonSerializer.Serialize(entry.Network);
            if (networkChanged)
                _log.Add(LogKind.Info, $"Настройки «{entry.Name}»: {DescribeApp(entry)}");
            Toast("Настройки сохранены",
                networkChanged && _tracker.CountFor(entry.Id) > 0 ? "Новые условия подействуют при следующем запуске" : entry.Name,
                GlyphCheck, ToastKind.Success);
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

    /// <summary>Условие запуска приложения словами, для журнала.</summary>
    private string DescribeApp(AppEntry entry) => entry.Network.Mode switch
    {
        AppNetworkMode.OwnConnection => $"своё подключение «{_settings.Connections.FirstOrDefault(c => c.Id == entry.Network.ConnectionId)?.Name}»",
        AppNetworkMode.OwnAdapters => $"свои адаптеры, {AppSettingsViewModel.DescribeRule(entry.Network.RuleMode, entry.Network.SelectedAdapters.Count)}",
        _ => "как в общих"
    };

    // ---------------------------------------------------------------- мастер первого запуска

    private void StartWelcome()
    {
        var add = new AddConnectionViewModel(() => _settings.Connections, c => AddConnectionAsync(c, makeDefault: true), _dialogs);
        Welcome = new WelcomeViewModel(add, CheckConnectionAsync,
            () => Task.Run(StartMenuApps.Find),
            exe => IconExtractor.TryGetIcon(exe),
            useSystemVpn: () =>
            {
                _settings.NetworkMode = GlobalNetworkMode.SystemVpn;
                SaveSettings();
                ResetDraft();
            },
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
            Toast("Приложения добавлены", string.Join(", ", added), GlyphAdd, ToastKind.Info);
        }

        GoBack();
        RebuildConnections();
        RefreshRunningCounts();
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

    // ---------------------------------------------------------------- обновления

    private async void OnStartupIdle()
    {
        OnStartupLogs();

        // Итог обновления, запущенного в прошлый раз (лаунчер перезапустился после установщика).
        var outcome = _updates.TakePendingOutcome();
        if (outcome is not null)
        {
            var target = UpdateService.Format(outcome.Target);
            if (outcome.Succeeded)
            {
                StatusMessage = $"Обновление установлено: версия {target}";
                _log.Add(LogKind.Update, $"Обновление до версии {target} установлено");
                Toast("Обновление установлено", $"Безопасный лаунчер {target}", GlyphCheck, ToastKind.Success);
            }
            else
            {
                StatusMessage = $"Не удалось обновить до версии {target}";
                _log.Add(LogKind.Failed, $"Не удалось обновить до версии {target}, осталась {UpdateService.Format(UpdateService.CurrentVersion)}");
                Toast("Не удалось обновить", $"Осталась версия {UpdateService.Format(UpdateService.CurrentVersion)}", GlyphInfo, ToastKind.Danger);
            }
        }

        // Подписки обновляются при каждом запуске (если пора) и потом раз в час проверяется, не пора ли снова.
        _subscriptionTimer.Start();
        _ = UpdateSubscriptionsAsync();

        // Не перекрываем уведомление об итоге обновления.
        await Task.Delay(outcome is null ? 1500 : 4500);
        await CheckForUpdatesAsync(userInitiated: false);
    }

    /// <summary>Проверить обновления. При запуске (userInitiated = false) сообщаем только о найденном обновлении.</summary>
    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (IsUpdateBusy) return;
        IsUpdateBusy = true;
        UpdateStatusText = "Проверка…";

        try
        {
            _updateCheck = await _updates.CheckAsync(CancellationToken.None);
            TechLog.Info(LogAreas.Update, LogCodes.UpdateCheck,
                $"Проверка обновлений{(userInitiated ? " (вручную)" : string.Empty)}: установлена {UpdateService.Format(_updateCheck.Current)}, " +
                $"последняя {(_updateCheck.Latest is { } latest ? UpdateService.Format(latest.Version) : "не найдена")}");
            OnPropertyChanged(nameof(IsUpdateAvailable));
            OnPropertyChanged(nameof(IsUpdateNotAvailable));

            if (_updateCheck.IsUpdateAvailable)
            {
                var version = UpdateService.Format(_updateCheck.Latest!.Version);
                UpdateStatusText = $"Доступна v{version}";

                // При запуске — только значок на кнопке «Настройки», без уведомления.
                if (userInitiated)
                    Toast("Доступно обновление", $"Версия {version} · нажмите «Обновить»", GlyphInfo, ToastKind.Info);
            }
            else
            {
                UpdateStatusText = "Последняя версия";
                if (userInitiated)
                    Toast("Обновлений нет", "Установлена последняя версия", GlyphCheck, ToastKind.Success);
            }
        }
        catch (Exception ex)
        {
            TechLog.Warn(LogAreas.Update, LogCodes.UpdateFail, "Не удалось проверить обновления: " + ex.Message);
            UpdateStatusText = "Не удалось проверить";
            if (userInitiated)
                Toast("Не удалось проверить обновления", ShortReason(ex), GlyphInfo, ToastKind.Danger);
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    /// <summary>Одна кнопка: «Проверить», а если обновление найдено — «Обновить».</summary>
    private async void OnUpdateAction()
    {
        if (IsUpdateAvailable)
            InstallUpdate();
        else
            await CheckForUpdatesAsync(userInitiated: true);
    }

    /// <summary>«Что нового»: при доступном обновлении — описание новой версии, иначе — установленной.</summary>
    private async void ShowChangelog()
    {
        if (_updateCheck is null)
            await CheckForUpdatesAsync(userInitiated: false);

        if (_updateCheck is null)
        {
            _dialogs.ShowWarning("Не удалось загрузить описание изменений. Проверьте подключение к интернету и попробуйте ещё раз.", "Что нового");
            return;
        }

        var canUpdate = _updateCheck.IsUpdateAvailable;
        var release = canUpdate ? _updateCheck.Latest : _updateCheck.CurrentRelease;
        var version = UpdateService.Format(release?.Version ?? UpdateService.CurrentVersion);

        if (release is null)
        {
            _dialogs.ShowWarning($"Описание изменений для версии {version} пока не опубликовано.", "Что нового");
            return;
        }

        var date = release.PublishedAt is { } published ? published.ToLocalTime().ToString("d MMMM yyyy", new System.Globalization.CultureInfo("ru-RU")) : null;
        var subtitle = (canUpdate ? "Доступно обновление" : "Установленная версия") + (date is null ? string.Empty : $" · {date}");

        if (_dialogs.ShowChangelog($"Что нового в версии {version}", subtitle, release.Notes, canUpdate))
            InstallUpdate();
    }

    /// <summary>Скачать установщик с лоадером, запустить его и закрыть лаунчер. Установщик сам запустит новую версию.</summary>
    private void InstallUpdate()
    {
        if (IsUpdateBusy || _updateCheck?.Latest is not { } release || !_updateCheck.IsUpdateAvailable)
            return;

        var version = UpdateService.Format(release.Version);
        var closing = AppsClosedOnExit();
        if (closing.Count > 0 && !_dialogs.Confirm($"Обновить до версии {version}?",
                $"Лаунчер перезапустится. Запущенные приложения ({closing.Count}) будут закрыты: без лаунчера защита не работает.",
                "Обновить"))
            return;

        IsUpdateBusy = true;
        try
        {
            string? setupPath = null;
            var outcome = _dialogs.RunWithProgress("Загрузка обновления", $"Безопасный лаунчер {version}",
                async (progress, token) => setupPath = await _updates.DownloadAsync(release, progress, token));

            if (outcome.Cancelled)
            {
                StatusMessage = "Загрузка обновления отменена";
                return;
            }

            if (!outcome.Completed || setupPath is null)
            {
                _dialogs.ShowError($"Не удалось загрузить обновление:\n{outcome.Error?.Message}", "Обновление");
                return;
            }

            try
            {
                _updates.StartSetup(setupPath, release);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                StatusMessage = "Установка обновления отменена";
                return;
            }
            catch (Exception ex)
            {
                _dialogs.ShowError($"Не удалось запустить установщик:\n{ex.Message}", "Обновление");
                return;
            }

            // Установщик запущен: закрываем приложения (как при обычном закрытии с Kill-Switch) и выходим.
            _tracker.KillApps(AppsClosedOnExit(), blockRelaunch: false);

            _closingForUpdate = true;
            StatusMessage = $"Устанавливается версия {version}…";
            _log.Add(LogKind.Update, $"Запущена установка версии {version}, лаунчер перезапустится");
            ShutdownRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    private static string ShortReason(Exception ex) => ex switch
    {
        HttpRequestException => "Нет связи с GitHub",
        TaskCanceledException => "GitHub не ответил вовремя",
        _ => ex.Message.Length <= 60 ? ex.Message : ex.Message[..57] + "…"
    };

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

        // Сначала приложения, потом подключение (ядро выключится в Dispose): иначе они успели бы пойти напрямую.
        var report = _tracker.KillApps(closing, blockRelaunch: false);
        if (report.Killed.Count > 0)
            _log.Add(LogKind.Closed, $"При закрытии лаунчера закрыты: {string.Join(", ", report.Killed)}");
        if (report.Failed.Count > 0)
            _log.Add(LogKind.Failed, $"При закрытии лаунчера не удалось закрыть: {string.Join(", ", report.Failed)}");
        return true;
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
        _dns.Dispose();

        // Сначала приложения (Windows закроет те, что закрываются вместе с лаунчером), потом ядро и фильтры.
        _tracker.Dispose();
        _network.Dispose();
        _guard.Dispose(); // служба снимает фильтры: всё как без лаунчера
        _proxy.Dispose();
    }
}
