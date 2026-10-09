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

namespace KillSwitchLauncher.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int ErrorCancelled = 1223; // пользователь отменил запрос UAC

    private readonly SettingsStorage _storage;
    private readonly IDialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private readonly ProcessTracker _tracker = new();
    private readonly UpdateService _updates;
    private readonly ActivityLog _log;

    // Обновления: результат последней проверки и состояние кнопок в настройках.
    private UpdateCheckResult? _updateCheck;
    private bool _isUpdateBusy;
    private string _updateStatusText = "Ещё не проверялось";
    private bool _closingForUpdate;

    // Предупреждение «не удалось взять под полный контроль» показываем один раз на приложение за сеанс.
    private readonly HashSet<Guid> _warnedUncontained = new();
    private readonly VpnMonitor _monitor;

    // Читается из фонового потока монитора, поэтому volatile.
    private volatile bool _killSwitchEnabled;

    // Глифы Segoe Fluent Icons / Segoe MDL2 Assets
    private const string GlyphLock = "\uE72E";
    private const string GlyphUnlock = "\uE785";
    private const string GlyphPlay = "\uE768";
    private const string GlyphAdd = "\uE710";
    private const string GlyphPower = "\uE7E8";
    private const string GlyphCheck = "\uE73E";
    private const string GlyphInfo = "\uE946";

    private bool _isVpnConnected;
    private string _vpnDetailsText = "Проверка сетевых адаптеров…";
    private string _vpnSubtitle = "Проверка…";
    private string _statusMessage = "Готово";
    private bool _isRulesPanelOpen;

    // Черновик настроек: переключатели в панели меняют только его, а в силу он вступает по кнопке «Применить».
    private bool _draftKillSwitch;
    private VpnRuleMode _draftRuleMode;
    private HideBehavior _draftHideBehavior;
    private readonly List<string> _draftSelected = new();

    // Только вид списка, не сохраняется: при каждом запуске выключено.
    private bool _showAllAdapters;
    private bool _isRemovedExpanded;

    /// <param name="log">Журнал событий; по умолчанию — в папке настроек. Тесты передают свой.</param>
    /// <param name="updates">Обновления; по умолчанию — обычные пути. Тесты передают свои.</param>
    public MainViewModel(SettingsStorage storage, IDialogService dialogs, Dispatcher dispatcher,
        ActivityLog? log = null, UpdateService? updates = null)
    {
        _log = log ?? new ActivityLog();
        _updates = updates ?? new UpdateService();
        _storage = storage;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _settings = storage.Load();
        _killSwitchEnabled = _settings.KillSwitchEnabled;

        // Адаптеры, выбранные до появления списка «удалённых», тоже должны в нём быть.
        foreach (var name in _settings.SelectedAdapters.Where(n => FindKnown(n) is null))
            _settings.KnownAdapters.Add(new KnownAdapter { Name = name, Description = name, Product = name, IsVpnLike = true });

        ResetDraft();

        AddAppCommand = new RelayCommand(AddApp);
        ApplySettingsCommand = new RelayCommand(ApplySettings);
        CancelSettingsCommand = new RelayCommand(() =>
        {
            ResetDraft();
            StatusMessage = "Изменения настроек отменены";
        });
        RefreshAdaptersCommand = new RelayCommand(() => ApplySnapshot(_monitor!.Evaluate()));
        ToggleRulesPanelCommand = new RelayCommand(() => IsRulesPanelOpen = !IsRulesPanelOpen);
        OpenSettingsCommand = new RelayCommand(() => IsRulesPanelOpen = true);
        UpdateActionCommand = new RelayCommand(OnUpdateAction);
        ShowChangelogCommand = new RelayCommand(ShowChangelog);
        ShowLogCommand = new RelayCommand(() => _dialogs.ShowActivityLog(_log));

        foreach (var entry in _settings.Apps)
            Apps.Add(CreateTile(entry));

        // Без лаунчера Kill-Switch не работает: при включённой защите приложения закроются и при его аварийном завершении.
        _tracker.SetKillOnClose(_settings.KillSwitchEnabled);
        _tracker.Changed += (_, _) => _dispatcher.BeginInvoke(RefreshRunningCounts);
        _tracker.RelaunchBlocked += (_, name) => _dispatcher.BeginInvoke(() => OnRelaunchBlocked(name));

        _monitor = new VpnMonitor(BuildRule());
        _monitor.StatusChanged += OnVpnStatusChanged;
        _monitor.Start();
        ApplySnapshot(_monitor.Current);

        _log.Add(LogKind.Info, $"Лаунчер {UpdateService.Format(UpdateService.CurrentVersion)} запущен · " +
                                    (_monitor.Current.IsConnected ? $"VPN подключён ({DescribeActive(_monitor.Current)})" : "VPN не подключён") +
                                    (_settings.KillSwitchEnabled ? string.Empty : " · Kill-Switch выключен"));

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
    public ICommand ToggleRulesPanelCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand UpdateActionCommand { get; }
    public ICommand ShowChangelogCommand { get; }
    public ICommand ShowLogCommand { get; }

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

    public bool IsRulesPanelOpen
    {
        get => _isRulesPanelOpen;
        set
        {
            if (!SetField(ref _isRulesPanelOpen, value)) return;

            // Закрыли панель, не нажав «Применить»: изменения не вступают в силу.
            if (!value && HasPendingChanges)
            {
                ResetDraft();
                StatusMessage = "Изменения настроек не применены";
                Toast("Изменения не применены", "Настройки остались прежними", GlyphInfo, ToastKind.Info);
            }
        }
    }

    /// <summary>В панели есть изменения, которые ещё не применены.</summary>
    public bool HasPendingChanges =>
        _draftKillSwitch != _settings.KillSwitchEnabled ||
        _draftRuleMode != _settings.RuleMode ||
        _draftHideBehavior != _settings.HideBehavior ||
        !new HashSet<string>(_draftSelected, StringComparer.OrdinalIgnoreCase).SetEquals(_settings.SelectedAdapters);

    public bool KillSwitchEnabled
    {
        get => _draftKillSwitch;
        set
        {
            if (_draftKillSwitch == value) return;

            // Выключение защиты — только после явного подтверждения, чтобы не выключить её случайным кликом.
            if (!value && _settings.KillSwitchEnabled && !_dialogs.Confirm(
                    "Выключить Kill-Switch?",
                    "При обрыве VPN запущенные приложения продолжат работать без защиты. Это может привести к утечке данных.",
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
        }
    }

    /// <summary>Группа «Другие адаптеры» видна только при «Показывать все адаптеры».</summary>
    public bool ShowOtherGroup => ShowAllAdapters && OtherAdapters.Count > 0;

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
        OnPropertyChanged(nameof(NeedsAdapterSelection));
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    /// <summary>Сбросить черновик к действующим настройкам.</summary>
    private void ResetDraft()
    {
        _draftKillSwitch = _settings.KillSwitchEnabled;
        _draftRuleMode = _settings.RuleMode;
        _draftHideBehavior = _settings.HideBehavior;
        _draftSelected.Clear();
        _draftSelected.AddRange(_settings.SelectedAdapters);

        OnPropertyChanged(nameof(KillSwitchEnabled));
        OnPropertyChanged(nameof(RuleMode));
        OnPropertyChanged(nameof(HideBehavior));
        OnPropertyChanged(nameof(IsAdapterSelectionEnabled));
        OnDraftChanged();

        if (_monitor is not null)
            SyncAdapters(_monitor.Current);
    }

    private void ApplySettings()
    {
        if (!HasPendingChanges) return;

        var killSwitchChanged = _draftKillSwitch != _settings.KillSwitchEnabled;
        var ruleChanged = _draftRuleMode != _settings.RuleMode ||
                          !new HashSet<string>(_draftSelected, StringComparer.OrdinalIgnoreCase).SetEquals(_settings.SelectedAdapters);
        _settings.KillSwitchEnabled = _draftKillSwitch;
        _settings.RuleMode = _draftRuleMode;
        _settings.HideBehavior = _draftHideBehavior;
        _settings.SelectedAdapters = new List<string>(_draftSelected);
        _killSwitchEnabled = _draftKillSwitch;
        _tracker.SetKillOnClose(_settings.KillSwitchEnabled);
        if (!_settings.KillSwitchEnabled)
            _tracker.StopBlocking();
        SaveSettings();

        OnPropertyChanged(nameof(IsKillSwitchOff));
        OnPropertyChanged(nameof(HasPendingChanges));

        if (killSwitchChanged)
            _log.Add(LogKind.Info, _settings.KillSwitchEnabled ? "Kill-Switch включён" : "Kill-Switch выключен");
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
        KillReport? report = null;

        // Убиваем процессы прямо здесь, в фоновом потоке, не дожидаясь UI:
        // окно может быть занято (модальный диалог и т.п.), а реагировать надо немедленно.
        if (e.JustDisconnected && _killSwitchEnabled)
            report = _tracker.KillAll(blockRelaunch: true);
        else if (e.Current.IsConnected)
            _tracker.StopBlocking(); // VPN снова есть: закрытые приложения можно запускать

        _dispatcher.BeginInvoke(() =>
        {
            ApplySnapshot(e.Current);

            if (e.JustDisconnected)
                OnVpnLost(report);
            else if (e.Previous is { IsConnected: false } && e.Current.IsConnected)
            {
                StatusMessage = $"{DateTime.Now:HH:mm:ss} — VPN подключён";
                Toast("VPN подключён", DescribeActive(e.Current), GlyphLock, ToastKind.Success);
                _log.Add(LogKind.VpnUp, $"VPN подключён: {DescribeActive(e.Current)}");
            }
        });
    }

    private void OnVpnLost(KillReport? report)
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        string subtitle;

        if (report is null)
            subtitle = "Kill-Switch выключен, приложения продолжают работу";
        else if (report.Killed.Count == 0 && report.Failed.Count == 0)
            subtitle = "Запущенных приложений не было";
        else
            subtitle = $"Kill-Switch закрыл приложений: {report.Killed.Count}" +
                       (report.Failed.Count > 0 ? $", не удалось: {report.Failed.Count}" : string.Empty);

        StatusMessage = $"{time} — VPN отключён. {subtitle}";
        Toast("VPN отключён", subtitle, GlyphUnlock, ToastKind.Danger);

        _log.Add(LogKind.VpnDown, $"VPN отключён: {_monitor.Current.Explanation}" +
                                       (report is null ? " · Kill-Switch выключен, приложения не закрывались" : string.Empty));
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

    /// <summary>Закрытое Kill-Switch приложение запустилось снова без VPN и было закрыто.</summary>
    private void OnRelaunchBlocked(string name)
    {
        StatusMessage = $"{DateTime.Now:HH:mm:ss} — «{name}» запустилось без VPN и закрыто";
        Toast("Запуск без VPN заблокирован", $"Kill-Switch закрыл «{name}»", GlyphPower, ToastKind.Danger);
        _log.Add(LogKind.Blocked, $"«{name}» запустилось без VPN и сразу закрыто");
    }

    private void Toast(string title, string subtitle, string glyph, ToastKind kind) =>
        ToastRequested?.Invoke(this, new ToastInfo(title, subtitle, glyph, kind));

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
            new RelayCommand(() => OpenLocation(tile!)));
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

    private void AddApp()
    {
        var path = _dialogs.PickExecutableOrShortcut();
        if (path is null) return;

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
                _dialogs.ShowError($"Не удалось прочитать ярлык:\n{path}\n\n{ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(target.TargetPath) && target.AppUserModelId is null)
            {
                _dialogs.ShowError("Не удалось понять, что запускает этот ярлык: он не указывает ни на программу, " +
                                   "ни на приложение Microsoft Store. Выберите сам .exe.");
                return;
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
            _dialogs.ShowError($"Ярлык указывает не на программу (.exe):\n{exePath}");
            return;
        }

        if (appUserModelId is null && !File.Exists(exePath))
        {
            _dialogs.ShowError($"Файл программы не найден:\n{exePath}");
            return;
        }

        var duplicate = appUserModelId is not null
            ? _settings.Apps.Any(a => string.Equals(a.AppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase))
            : _settings.Apps.Any(a => !a.IsPackaged
                                      && string.Equals(a.ExePath, exePath, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(a.Arguments ?? "", arguments ?? "", StringComparison.Ordinal));
        if (duplicate)
        {
            _dialogs.ShowWarning($"«{name}» уже есть в списке.", "Уже добавлено");
            return;
        }

        var entry = new AppEntry
        {
            Name = name,
            ExePath = exePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            SourcePath = path,
            AppUserModelId = appUserModelId
        };

        _settings.Apps.Add(entry);
        SaveSettings();
        Apps.Add(CreateTile(entry));
        OnPropertyChanged(nameof(HasNoApps));
        StatusMessage = $"Добавлено: {name} → {(appUserModelId is null ? exePath : "Microsoft Store")}";
        Toast("Приложение добавлено", name, GlyphAdd, ToastKind.Info);
        _log.Add(LogKind.Info, $"Добавлено приложение «{name}»");
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

    private void Launch(AppTileViewModel tile)
    {
        // Статус проверяем заново прямо сейчас, а не берём из кэша.
        if (!_monitor.Evaluate().IsConnected)
        {
            _log.Add(LogKind.Blocked, $"Запуск «{tile.Entry.Name}» отклонён: VPN не подключён");
            _dialogs.ShowWarning("Для запуска этого приложения необходимо подключиться к VPN.", "Нет подключения к VPN");
            return;
        }

        var entry = tile.Entry;

        // Приложение добавлено ярлыком: запускаем то, на что ярлык указывает сейчас.
        RefreshFromShortcut(entry);

        if (!entry.IsPackaged && !File.Exists(entry.ExePath) && !TryRelocate(entry))
        {
            _dialogs.ShowError($"Файл программы не найден:\n{entry.ExePath}");
            return;
        }

        LaunchResult result;
        try
        {
            // Программа запускается внутри задания Windows: под контролем окажутся и все процессы,
            // которые она породит, даже если сама она сразу завершится (как Update.exe у Discord).
            // Приложения Microsoft Store запускает сама Windows, их процесс забирается в задание сразу после старта.
            result = entry.IsPackaged ? _tracker.LaunchPackaged(entry) : _tracker.Launch(entry);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            StatusMessage = $"Запуск «{entry.Name}» отменён";
            return;
        }
        catch (Exception ex)
        {
            _log.Add(LogKind.Failed, $"Не удалось запустить «{entry.Name}»: {ex.Message}");
            _dialogs.ShowError($"Не удалось запустить «{entry.Name}»:\n{ex.Message}");
            return;
        }

        if (result.ProcessId == 0)
        {
            _log.Add(LogKind.Failed, $"«{entry.Name}» запущена, но без контроля: Windows не вернула её процесс");
            _dialogs.ShowWarning($"«{entry.Name}» запущена, но Windows не вернула её процесс, поэтому Kill-Switch не сможет её закрыть.");
            return;
        }

        StatusMessage = $"{DateTime.Now:HH:mm:ss} — запущено: {entry.Name} (PID {result.ProcessId})" +
                        (result.Adopted > 0 ? $", взято под контроль уже работавших: {ProcessTracker.ProcessesText(result.Adopted)}" : string.Empty);
        Toast($"Запущено: {entry.Name}",
            !_killSwitchEnabled ? "Kill-Switch выключен"
            : result.Adopted > 0 ? "Уже открытое приложение тоже под защитой"
            : "Под защитой Kill-Switch",
            GlyphPlay, ToastKind.Info);
        _log.Add(LogKind.Launch, $"Запущено: {entry.Name}" +
                                      (result.Adopted > 0 ? $" · взято под контроль уже работавших: {ProcessTracker.ProcessesText(result.Adopted)}" : string.Empty) +
                                      (result.Contained ? string.Empty : result.Elevated ? " · с правами администратора, контроль неполный" : " · контроль неполный"));

        if (!result.Contained && _killSwitchEnabled && _warnedUncontained.Add(entry.Id))
        {
            _dialogs.ShowWarning(result.Elevated
                    ? $"«{entry.Name}» работает с правами администратора, а лаунчер — без них. Kill-Switch попробует закрыть её " +
                      "при обрыве VPN, но Windows может не разрешить это, и дочерние процессы могут остаться.\n\n" +
                      "Чтобы защита работала надёжно, запускайте лаунчер от имени администратора."
                    : $"Windows не позволила поместить «{entry.Name}» в задание лаунчера. Kill-Switch закроет сам процесс, " +
                      "но процессы, которые он запустит, могут остаться.",
                "Неполный контроль");
        }

        // Закрываем окно гонки: VPN мог упасть между проверкой и стартом процесса,
        // и тогда событие монитора уже отработало, когда приложения ещё не было в списке.
        if (_killSwitchEnabled && !_monitor.Evaluate().IsConnected)
        {
            var report = _tracker.KillAll(blockRelaunch: true);
            OnVpnLost(report);
        }
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
        _log.Add(LogKind.Info, $"Убрано из лаунчера: «{tile.Name}»");
        _tracker.StopBlocking(tile.Entry.Id);
        Apps.Remove(tile);
        OnPropertyChanged(nameof(HasNoApps));
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
            tile.RunningCount = _tracker.CountFor(tile.Entry.Id);
        OnPropertyChanged(nameof(RunningSummary));
    }

    // ---------------------------------------------------------------- обновления

    private async void OnStartupIdle()
    {
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
        var running = _tracker.Count;
        if (running > 0 && _killSwitchEnabled && !_dialogs.Confirm($"Обновить до версии {version}?",
                $"Лаунчер перезапустится. Запущенные приложения ({running}) будут закрыты: без лаунчера Kill-Switch не работает.",
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
            if (_killSwitchEnabled)
                _tracker.KillAll(blockRelaunch: false);

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
    public bool NeedsCloseConfirmation => !_closingForUpdate && _killSwitchEnabled && _tracker.Count > 0;

    /// <summary>
    /// Вызывается при закрытии окна. Без работающего лаунчера Kill-Switch не действует,
    /// поэтому при включённой опции запущенные приложения закрываются вместе с ним.
    /// </summary>
    public bool ConfirmClose()
    {
        // Закрываемся ради установки обновления: всё уже подтверждено и сделано.
        if (_closingForUpdate)
            return true;

        var running = _tracker.Count;
        if (running == 0 || !_killSwitchEnabled)
            return true;

        if (!_dialogs.Confirm("Закрыть лаунчер?",
                $"Лаунчер контролирует запущенные приложения ({running}). Без него Kill-Switch не работает, поэтому они будут принудительно закрыты.",
                "Закрыть всё", destructive: true))
            return false;

        var report = _tracker.KillAll(blockRelaunch: false);
        if (report.Killed.Count > 0)
            _log.Add(LogKind.Closed, $"При закрытии лаунчера закрыты: {string.Join(", ", report.Killed)}");
        if (report.Failed.Count > 0)
            _log.Add(LogKind.Failed, $"При закрытии лаунчера не удалось закрыть: {string.Join(", ", report.Failed)}");
        return true;
    }

    private void SaveSettings()
    {
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
        _monitor.StatusChanged -= OnVpnStatusChanged;
        _monitor.Dispose();
        _tracker.Dispose();
    }
}
