using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.ViewModels;

/// <summary>Адаптер в списке «Свои адаптеры» на странице приложения.</summary>
public sealed class AdapterChoiceViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    public AdapterChoiceViewModel(string name, string product, string details, bool isUp, bool isSelected, Action changed)
    {
        Name = name;
        Product = product;
        Details = details;
        IsUp = isUp;
        _isSelected = isSelected;
        _changed = changed;
    }

    public string Name { get; }
    public string Product { get; }
    public string Details { get; }
    public bool IsUp { get; }
    public string StateText => IsUp ? "Активен" : "Не активен";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
                _changed();
        }
    }
}

/// <summary>
/// Страница приложения: название, условие запуска («Как в общих», «Свои адаптеры», «Своё подключение» с вкладками
/// «Выбрать» и «Добавить подключение»), своя защита, исключения, журнал DNS и параметры запуска. Правит черновик:
/// в приложение изменения попадают по «Сохранить» (для нового приложения — «Добавить»).
/// </summary>
public sealed class AppSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly Action<AppSettingsViewModel> _save;
    private readonly Action<AppSettingsViewModel> _close;
    private readonly string _initial;

    private string _name;
    private AppNetworkMode _mode;
    private VpnRuleMode _ruleMode;
    private Guid? _connectionId;
    private bool _isAddTab;
    private ProtectionOverride _requireVpn;
    private ProtectionOverride _closeOnDrop;
    private string _domainsText;
    private bool _trackDns;
    private string _arguments;
    private string? _error;

    public AppSettingsViewModel(AppEntry entry, bool isNew, AppSettings settings, ImageSource? icon,
        IEnumerable<(string Name, string Product, string Details, bool IsUp)> adapters,
        IEnumerable<ConnectionItemViewModel> connections, AddConnectionViewModel addConnection, IDialogService dialogs,
        Action<AppSettingsViewModel> save, Action<AppSettingsViewModel> close)
    {
        Entry = entry;
        IsNew = isNew;
        Icon = icon;
        _settings = settings;
        _dialogs = dialogs;
        _save = save;
        _close = close;
        AddConnection = addConnection;
        AddConnection.Added += OnConnectionAdded;

        var network = entry.Network ?? new AppNetwork();
        _name = entry.Name;
        _mode = network.Mode;
        _ruleMode = network.RuleMode;
        _connectionId = network.ConnectionId;
        _requireVpn = network.RequireVpn;
        _closeOnDrop = network.CloseOnDrop;
        _domainsText = string.Join(Environment.NewLine, network.DirectDomains ?? new List<string>());
        _trackDns = entry.TrackDns;
        _arguments = entry.Arguments ?? string.Empty;

        var selected = new HashSet<string>(network.SelectedAdapters ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var a in adapters)
            Adapters.Add(new AdapterChoiceViewModel(a.Name, a.Product, a.Details, a.IsUp, selected.Contains(a.Name), OnChanged));
        foreach (var name in selected.Where(n => Adapters.All(a => !string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))))
            Adapters.Add(new AdapterChoiceViewModel(name, name, "нет в системе", false, true, OnChanged));

        foreach (var c in connections)
            Connections.Add(c);
        _isAddTab = Connections.Count == 0;

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => _close(this));
        ChooseTabCommand = new RelayCommand(() => IsAddTab = false);
        AddTabCommand = new RelayCommand(() => IsAddTab = true);
        _initial = Signature();
    }

    public AppEntry Entry { get; }

    /// <summary>Приложение ещё не добавлено: страница открылась после выбора файла.</summary>
    public bool IsNew { get; }

    public ImageSource? Icon { get; }

    public string Title => IsNew ? "Новое приложение" : "Настройки приложения";

    public string SaveText => IsNew ? "Добавить" : "Сохранить";

    public string PathText => Entry.IsPackaged ? $"Microsoft Store · {Entry.AppUserModelId}" : Entry.SourcePath ?? Entry.ExePath;

    public ObservableCollection<AdapterChoiceViewModel> Adapters { get; } = new();

    public ObservableCollection<ConnectionItemViewModel> Connections { get; } = new();

    public AddConnectionViewModel AddConnection { get; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ChooseTabCommand { get; }
    public ICommand AddTabCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value ?? string.Empty))
                OnChanged();
        }
    }

    // ---------------------------------------------------------------- условие запуска

    public AppNetworkMode Mode
    {
        get => _mode;
        set
        {
            if (!SetField(ref _mode, value)) return;
            if (value == AppNetworkMode.OwnConnection && _connectionId is null && Connections.Count == 1)
                ConnectionId = Connections[0].Id;
            OnPropertyChanged(nameof(IsInherit));
            OnPropertyChanged(nameof(IsOwnAdapters));
            OnPropertyChanged(nameof(IsOwnConnection));
            OnPropertyChanged(nameof(UsesLauncherCore));
            OnPropertyChanged(nameof(CloseOnDropHint));
            OnPropertyChanged(nameof(RequireVpnHint));
            OnPropertyChanged(nameof(DomainsHint));
            OnChanged();
        }
    }

    public bool IsInherit => Mode == AppNetworkMode.Inherit;
    public bool IsOwnAdapters => Mode == AppNetworkMode.OwnAdapters;
    public bool IsOwnConnection => Mode == AppNetworkMode.OwnConnection;

    /// <summary>Что сейчас выбрано в общих настройках: подпись под «Как в общих».</summary>
    public string InheritText => _settings.NetworkMode == GlobalNetworkMode.LauncherConnection
        ? _settings.Connections.FirstOrDefault(c => c.Id == _settings.ConnectionId) is { } connection
            ? $"Сейчас в общих настройках: подключение лаунчера «{connection.Name}». Через него пойдёт весь трафик приложения."
            : "В общих настройках выбрано подключение лаунчера, но само подключение не выбрано. Пока его не выберут, приложение не запустится."
        : $"Сейчас в общих настройках: системный VPN, {DescribeRule(_settings.RuleMode, _settings.SelectedAdapters.Count)}. " +
          "Маршрутом управляет ваша VPN-программа, лаунчер следит, что она включена.";

    /// <summary>Трафик приложения поведёт ядро лаунчера (а не другая VPN-программа).</summary>
    public bool UsesLauncherCore =>
        Mode == AppNetworkMode.OwnConnection ||
        Mode == AppNetworkMode.Inherit && _settings.NetworkMode == GlobalNetworkMode.LauncherConnection;

    public VpnRuleMode RuleMode
    {
        get => _ruleMode;
        set
        {
            if (!SetField(ref _ruleMode, value)) return;
            OnPropertyChanged(nameof(IsAdapterSelectionEnabled));
            OnPropertyChanged(nameof(RuleHint));
            OnChanged();
        }
    }

    public bool IsAdapterSelectionEnabled => RuleMode != VpnRuleMode.AnyVpn;

    public string RuleHint => RuleMode switch
    {
        VpnRuleMode.AnyOfSelected => "Достаточно, чтобы был активен хотя бы один из включённых адаптеров.",
        VpnRuleMode.AllOfSelected => "Должны быть активны все включённые адаптеры одновременно.",
        _ => "Подойдёт любой активный VPN. Переключатели адаптеров не используются."
    };

    public bool NeedsAdapterSelection => IsOwnAdapters && RuleMode != VpnRuleMode.AnyVpn && !Adapters.Any(a => a.IsSelected);

    /// <summary>Выбранное подключение (вкладка «Выбрать»).</summary>
    public Guid? ConnectionId
    {
        get => _connectionId;
        set
        {
            if (SetField(ref _connectionId, value))
                OnChanged();
        }
    }

    /// <summary>Открыта вкладка «Добавить подключение».</summary>
    public bool IsAddTab
    {
        get => _isAddTab;
        set
        {
            if (!SetField(ref _isAddTab, value)) return;
            OnPropertyChanged(nameof(IsChooseTab));
            if (value)
                AddConnection.OfferClipboard();
        }
    }

    public bool IsChooseTab => !IsAddTab;

    public bool HasConnections => Connections.Count > 0;

    // ---------------------------------------------------------------- защита

    public ProtectionOverride RequireVpn
    {
        get => _requireVpn;
        set
        {
            if (SetField(ref _requireVpn, value))
                OnChanged();
        }
    }

    public ProtectionOverride CloseOnDrop
    {
        get => _closeOnDrop;
        set
        {
            if (_closeOnDrop == value) return;

            // Под системным VPN выключенный Kill-Switch оставляет приложение в сети без VPN: только с подтверждением.
            if (value == ProtectionOverride.Off && !UsesLauncherCore && !_dialogs.Confirm(
                    $"Выключить Kill-Switch для «{Name}»?",
                    "При обрыве VPN приложение продолжит работать без защиты. Это может привести к утечке данных.",
                    "Выключить", destructive: true))
            {
                // Вернуть переключатель (асинхронно, чтобы привязка успела завершить запись).
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => OnPropertyChanged(nameof(CloseOnDrop)));
                return;
            }

            _closeOnDrop = value;
            OnPropertyChanged();
            OnChanged();
        }
    }

    public string RequireVpnInheritText => $"Как в общих ({(_settings.RequireVpn ? "вкл" : "выкл")})";

    public string CloseOnDropInheritText => $"Как в общих ({(_settings.KillSwitchEnabled ? "вкл" : "выкл")})";

    public string RequireVpnHint => UsesLauncherCore
        ? "Перед запуском лаунчер проверит, что сервер подключения отвечает. Трафик приложения в любом случае идёт только через подключение."
        : "Приложение не запустится, пока VPN не подключён.";

    public string CloseOnDropHint => UsesLauncherCore
        ? "Если сервер перестал отвечать: закрыть приложение или оставить его без сети до возвращения связи. Мимо подключения трафик не уйдёт в обоих случаях."
        : "Если VPN отключился, приложение закроется. Без этого оно продолжит работать без VPN.";

    // ---------------------------------------------------------------- исключения, DNS, запуск

    /// <summary>Домены, которые открываются напрямую, по одному в строке.</summary>
    public string DomainsText
    {
        get => _domainsText;
        set
        {
            if (SetField(ref _domainsText, value ?? string.Empty))
                OnChanged();
        }
    }

    public string DomainsHint => UsesLauncherCore
        ? "Эти сайты приложение откроет напрямую, мимо подключения. По одному в строке, поддомены входят: kaspi.kz откроет и online.kaspi.kz."
        : "Работают только с подключением лаунчера: под системным VPN маршрутом управляет ваша VPN-программа.";

    public bool TrackDns
    {
        get => _trackDns;
        set
        {
            if (SetField(ref _trackDns, value))
                OnChanged();
        }
    }

    public string Arguments
    {
        get => _arguments;
        set
        {
            if (SetField(ref _arguments, value ?? string.Empty))
                OnChanged();
        }
    }

    /// <summary>Добавлено ярлыком: параметры берутся из ярлыка при каждом запуске.</summary>
    public bool IsFromShortcut => Entry.ShortcutPath is not null;

    public bool CanEditArguments => !IsFromShortcut && !Entry.IsPackaged;

    public string ArgumentsHint => IsFromShortcut
        ? "Берутся из ярлыка при каждом запуске. Чтобы поменять, измените сам ярлык."
        : Entry.IsPackaged ? "Приложения Microsoft Store запускает Windows." : "Например, --profile-directory=\"VPN\".";

    // ---------------------------------------------------------------- сохранение

    public string? Error
    {
        get => _error;
        private set => SetField(ref _error, value);
    }

    public bool HasChanges => IsNew || Signature() != _initial;

    /// <summary>Записать черновик в приложение. Проверку делает <see cref="Validate"/>.</summary>
    public void ApplyTo(AppEntry entry)
    {
        entry.Name = Name.Trim();
        entry.TrackDns = TrackDns;
        if (CanEditArguments)
            entry.Arguments = string.IsNullOrWhiteSpace(Arguments) ? null : Arguments.Trim();

        entry.Network ??= new AppNetwork();
        var network = entry.Network;
        network.Mode = Mode;
        network.RuleMode = RuleMode;
        network.SelectedAdapters = Adapters.Where(a => a.IsSelected).Select(a => a.Name).ToList();
        network.ConnectionId = ConnectionId;
        network.RequireVpn = RequireVpn;
        network.CloseOnDrop = CloseOnDrop;
        network.DirectDomains = DomainList.Normalize(SplitLines(DomainsText));
    }

    /// <summary>null — можно сохранять, иначе что исправить.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Укажите название приложения.";
        if (IsOwnConnection && (ConnectionId is not { } id || Connections.All(c => c.Id != id)))
            return Connections.Count == 0
                ? "Добавьте подключение на вкладке «Добавить подключение» или выберите другое условие запуска."
                : "Выберите подключение для приложения.";
        if (NeedsAdapterSelection)
            return "Включите хотя бы один адаптер или выберите правило «Любой VPN».";

        var wrong = SplitLines(DomainsText).Where(d => !DomainList.TryNormalize(d, out _)).ToList();
        if (wrong.Count > 0)
            return $"Это не похоже на домен: {string.Join(", ", wrong.Take(3))}. Пишите так: kaspi.kz.";
        return null;
    }

    /// <summary>Подключения изменились (добавили, переименовали, удалили): обновить список выбора.</summary>
    public void SyncConnections(IEnumerable<ConnectionItemViewModel> connections)
    {
        var list = connections.ToList();
        for (var i = Connections.Count - 1; i >= 0; i--)
        {
            if (!list.Contains(Connections[i]))
                Connections.RemoveAt(i);
        }

        foreach (var c in list.Where(c => !Connections.Contains(c)))
            Connections.Add(c);

        if (_connectionId is { } id && Connections.All(c => c.Id != id))
            ConnectionId = null;
        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(InheritText));
    }

    public void Detach() => AddConnection.Added -= OnConnectionAdded;

    private void Save()
    {
        Error = Validate();
        if (Error is null)
            _save(this);
    }

    private void OnConnectionAdded(VpnConnection connection)
    {
        // Главное окно уже добавило подключение в списки: выбираем его и возвращаемся на «Выбрать».
        ConnectionId = connection.Id;
        IsAddTab = false;
    }

    private void OnChanged()
    {
        Error = null;
        OnPropertyChanged(nameof(NeedsAdapterSelection));
        OnPropertyChanged(nameof(HasChanges));
    }

    private string Signature() => string.Join("|",
        Name.Trim(), Mode, RuleMode, string.Join(",", Adapters.Where(a => a.IsSelected).Select(a => a.Name)),
        ConnectionId, RequireVpn, CloseOnDrop, string.Join(",", SplitLines(DomainsText)), TrackDns, Arguments.Trim());

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(new[] { '\n', '\r', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static string DescribeRule(VpnRuleMode mode, int selected) => mode switch
    {
        VpnRuleMode.AnyOfSelected => $"один из выбранных адаптеров ({selected})",
        VpnRuleMode.AllOfSelected => $"все выбранные адаптеры ({selected})",
        _ => "любой активный VPN"
    };
}
