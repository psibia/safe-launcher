using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.ViewModels;

/// <summary>
/// Страница приложения: название, условие запуска («Основное подключение», «Своё подключение» с вкладками «Выбрать»
/// и «Добавить подключение», «Без сети»), своя защита и исключения. Правит черновик: в приложение изменения попадают
/// по «Сохранить» (для нового приложения — «Добавить»). Журнал DNS включается из меню плитки, параметры запуска
/// берутся из ярлыка или задаются при добавлении, на странице их нет.
/// </summary>
public sealed class AppSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettingsViewModel> _save;
    private readonly Action<AppSettingsViewModel> _close;
    private readonly string _initial;

    private string _name;
    private AppNetworkMode _mode;
    private Guid? _connectionId;
    private bool _isAddTab;
    private ProtectionOverride _closeOnDrop;
    private OutsideCopiesOverride _outsideCopies;
    private string _domainsText;
    private DomainsMode _domainsMode;
    private string? _error;

    public AppSettingsViewModel(AppEntry entry, bool isNew, AppSettings settings, ImageSource? icon,
        IEnumerable<ConnectionItemViewModel> connections, AddConnectionViewModel addConnection,
        Action<AppSettingsViewModel> save, Action<AppSettingsViewModel> close)
    {
        Entry = entry;
        IsNew = isNew;
        Icon = icon;
        _settings = settings;
        _save = save;
        _close = close;
        AddConnection = addConnection;
        AddConnection.Added += OnConnectionAdded;

        var network = entry.Network ?? new AppNetwork();
        _name = entry.Name;
        _mode = network.Mode;
        _connectionId = network.ConnectionId;
        _closeOnDrop = network.CloseOnDrop;
        _outsideCopies = network.OutsideCopies;
        _domainsText = string.Join(Environment.NewLine, network.DirectDomains ?? new List<string>());
        _domainsMode = network.DomainsMode;

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
            OnPropertyChanged(nameof(IsOwnConnection));
            OnPropertyChanged(nameof(IsOffline));
            OnPropertyChanged(nameof(UsesLauncherCore));
            OnChanged();
        }
    }

    public bool IsInherit => Mode == AppNetworkMode.Inherit;
    public bool IsOwnConnection => Mode == AppNetworkMode.OwnConnection;

    /// <summary>«Без сети»: защита и исключения не нужны, на странице скрыты.</summary>
    public bool IsOffline => Mode == AppNetworkMode.Offline;

    /// <summary>Основное подключение из раздела «Подключения»: подпись под «Основное подключение».</summary>
    public string InheritText => _settings.Connections.FirstOrDefault(c => c.Id == _settings.ConnectionId) is { } connection
        ? $"Сейчас это «{connection.Name}». Сделать основным другое можно в настройках, раздел «Подключения»."
        : "Основное подключение не выбрано: пока его не выберут в настройках, раздел «Подключения», приложение не запустится.";

    /// <summary>Трафик приложения ведёт ядро лаунчера: всё, кроме «Без сети».</summary>
    public bool UsesLauncherCore => Mode is AppNetworkMode.Inherit or AppNetworkMode.OwnConnection;

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

    public ProtectionOverride CloseOnDrop
    {
        get => _closeOnDrop;
        set
        {
            if (SetField(ref _closeOnDrop, value))
                OnChanged();
        }
    }

    public string CloseOnDropInheritText => $"Как в общих ({(_settings.KillSwitchEnabled ? "вкл" : "выкл")})";

    public string CloseOnDropHint =>
        "Закрыть приложение, если сервер подключения перестал отвечать. Иначе оно останется без интернета, пока связь не вернётся.";

    /// <summary>
    /// «Если открыто не из лаунчера»: с ярлыка, из автозагрузки, из другой программы. «Через лаунчер» — для всех приложений,
    /// остальное только для подключения лаунчера.
    /// </summary>
    public OutsideCopiesOverride OutsideCopies
    {
        get => _outsideCopies;
        set
        {
            if (!SetField(ref _outsideCopies, value)) return;
            OnPropertyChanged(nameof(OutsideCopiesHint));
            OnChanged();
        }
    }

    public string OutsideCopiesInheritText => _settings.OutsideCopies switch
    {
        Models.OutsideCopies.Block => "Как в общих (без интернета)",
        Models.OutsideCopies.Allow => "Как в общих (напрямую)",
        Models.OutsideCopies.Launcher => "Как в общих (через лаунчер)",
        _ => "Как в общих (через VPN)"
    };

    public string OutsideCopiesHint => NetworkPlan.Resolve(OutsideCopies, _settings.OutsideCopies) switch
    {
        Models.OutsideCopies.Launcher => "Лаунчер берёт его под контроль так же, как при запуске с плитки: с теми же условиями и защитой. " +
                                         "Если запускать приложение сейчас нельзя, например без VPN, оно сразу закроется. Действует, пока открыт лаунчер.",
        Models.OutsideCopies.Block => "У него нет интернета, пока открыт лаунчер.",
        Models.OutsideCopies.Allow => "Его трафик идёт мимо VPN. Изредка так же может уйти и соединение приложения, открытого из лаунчера.",
        _ => "Его трафик идёт через подключение приложения, пока открыт лаунчер."
    };

    // ---------------------------------------------------------------- исключения

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

    public string DomainsHint =>
        "Эти сайты приложение откроет напрямую, мимо подключения. По одному в строке, поддомены входят: kaspi.kz откроет и online.kaspi.kz. " +
        "Какие сайты открывает приложение, видно в журнале DNS: он включается в меню плитки.";

    /// <summary>Свои исключения дополняют общие из настроек или заменяют их.</summary>
    public DomainsMode DomainsMode
    {
        get => _domainsMode;
        set
        {
            if (!SetField(ref _domainsMode, value)) return;
            OnPropertyChanged(nameof(CommonDomainsHint));
            OnChanged();
        }
    }

    /// <summary>Что приходит из общих исключений (раздел настроек «Исключения») при выбранном варианте.</summary>
    public string CommonDomainsHint
    {
        get
        {
            var common = _settings.DirectDomains ?? new List<string>();
            var list = common.Count == 0 ? "" : ": " + string.Join(", ", common.Take(3)) + (common.Count > 3 ? $" и ещё {common.Count - 3}" : "");
            if (DomainsMode == DomainsMode.OwnOnly)
                return common.Count == 0
                    ? "Напрямую идут только сайты из списка ниже. Если он пуст, всё идёт через подключение."
                    : $"Общие исключения ({common.Count}) не действуют, напрямую идут только сайты из списка ниже. Если он пуст, всё идёт через подключение.";
            return common.Count == 0
                ? "Общих исключений нет. Их можно задать в настройках, раздел «Исключения»."
                : $"Напрямую идут и общие исключения ({common.Count}){list}.";
        }
    }

    // ---------------------------------------------------------------- сохранение

    public string? Error
    {
        get => _error;
        private set => SetField(ref _error, value);
    }

    public bool HasChanges => IsNew || Signature() != _initial;

    /// <summary>
    /// Записать черновик в приложение. Проверку делает <see cref="Validate"/>. Журнал DNS и параметры запуска не трогает:
    /// журнал включается из меню плитки, параметры приходят из ярлыка или задаются при добавлении.
    /// </summary>
    public void ApplyTo(AppEntry entry)
    {
        entry.Name = Name.Trim();

        entry.Network ??= new AppNetwork();
        var network = entry.Network;
        network.Mode = Mode;
        network.ConnectionId = ConnectionId;
        network.CloseOnDrop = CloseOnDrop;
        network.OutsideCopies = OutsideCopies;
        network.DirectDomains = DomainList.Normalize(DomainList.Split(DomainsText));
        network.DomainsMode = DomainsMode;
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

        if (DomainList.Problem(DomainsText) is { } problem)
            return problem;
        var own = DomainList.Normalize(DomainList.Split(DomainsText));
        var total = DomainsMode == DomainsMode.OwnOnly ? own.Count : DomainList.Normalize((_settings.DirectDomains ?? new List<string>()).Concat(own)).Count;
        if (total > DomainList.MaxPerApp)
            return DomainsMode == DomainsMode.OwnOnly
                ? $"Исключений больше {DomainList.MaxPerApp}: столько ядро не примет. Уберите лишние."
                : $"Исключений вместе с общими больше {DomainList.MaxPerApp}: столько ядро не примет. Уберите лишние.";
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
        OnPropertyChanged(nameof(HasChanges));
    }

    private string Signature() => string.Join("|",
        Name.Trim(), Mode, ConnectionId, CloseOnDrop, OutsideCopies, string.Join(",", DomainList.Split(DomainsText)), DomainsMode);
}
