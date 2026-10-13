using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.ViewModels;

/// <summary>Программа из меню «Пуск» с галочкой в мастере.</summary>
public sealed class StartMenuChoiceViewModel : ObservableObject
{
    private bool _isSelected;
    private ImageSource? _icon;

    public StartMenuChoiceViewModel(StartMenuApp app) => App = app;

    public StartMenuApp App { get; }
    public string Name => App.Name;
    public string Path => App.ExePath;

    public ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

/// <summary>
/// Мастер первого запуска из трёх шагов: подключение (или «У меня уже есть VPN-программа»), проверка подключения
/// и программы из меню «Пуск» с галочками.
/// </summary>
public sealed class WelcomeViewModel : ObservableObject
{
    private readonly Func<VpnConnection, Task<ConnectionCheck>> _check;
    private readonly Func<Task<IReadOnlyList<StartMenuApp>>> _findApps;
    private readonly Func<string, ImageSource?> _icon;
    private readonly Action _useSystemVpn;
    private readonly Action<IReadOnlyList<StartMenuApp>> _finish;

    private int _step = 1;
    private VpnConnection? _connection;
    private bool _isChecking;
    private bool? _checkOk;
    private string _checkTitle = string.Empty;
    private string _checkDetails = string.Empty;
    private bool _isLoadingApps;
    private bool _appsLoaded;

    /// <param name="check">Проверить подключение (задержка, IP, страна).</param>
    /// <param name="findApps">Программы из меню «Пуск» (долго: в фоне).</param>
    /// <param name="useSystemVpn">«У меня уже есть VPN-программа»: оставить системный VPN.</param>
    /// <param name="finish">Добавить отмеченные программы и закрыть мастер.</param>
    /// <param name="skip">Закрыть мастер, ничего не добавляя.</param>
    public WelcomeViewModel(AddConnectionViewModel addConnection, Func<VpnConnection, Task<ConnectionCheck>> check,
        Func<Task<IReadOnlyList<StartMenuApp>>> findApps, Func<string, ImageSource?> icon,
        Action useSystemVpn, Action<IReadOnlyList<StartMenuApp>> finish, Action skip)
    {
        AddConnection = addConnection;
        _check = check;
        _findApps = findApps;
        _icon = icon;
        _useSystemVpn = useSystemVpn;
        _finish = finish;
        AddConnection.Added += OnConnectionAdded;

        UseSystemVpnCommand = new RelayCommand(() =>
        {
            _useSystemVpn();
            Step = 3;
        });
        BackCommand = new RelayCommand(() => Step = Math.Max(1, Step == 3 && _connection is null ? 1 : Step - 1));
        NextCommand = new RelayCommand(() => Step = Math.Min(3, Step + 1));
        RecheckCommand = new RelayCommand(() => _ = CheckAsync(), () => !IsChecking);
        FinishCommand = new RelayCommand(() => _finish(Apps.Where(a => a.IsSelected).Select(a => a.App).ToList()));
        SkipCommand = new RelayCommand(skip);
    }

    public AddConnectionViewModel AddConnection { get; }

    public ObservableCollection<StartMenuChoiceViewModel> Apps { get; } = new();

    public ICommand UseSystemVpnCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand RecheckCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand SkipCommand { get; }

    /// <summary>1 — подключение, 2 — проверка, 3 — приложения.</summary>
    public int Step
    {
        get => _step;
        private set
        {
            if (!SetField(ref _step, value)) return;
            OnPropertyChanged(nameof(IsStep1));
            OnPropertyChanged(nameof(IsStep2));
            OnPropertyChanged(nameof(IsStep3));
            OnPropertyChanged(nameof(StepText));
            if (value == 1)
                AddConnection.OfferClipboard();
            if (value == 3)
                _ = LoadAppsAsync();
        }
    }

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;

    public string StepText => $"Шаг {Step} из 3";

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetField(ref _isChecking, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool? CheckOk
    {
        get => _checkOk;
        private set => SetField(ref _checkOk, value);
    }

    public string CheckTitle
    {
        get => _checkTitle;
        private set => SetField(ref _checkTitle, value);
    }

    public string CheckDetails
    {
        get => _checkDetails;
        private set => SetField(ref _checkDetails, value);
    }

    public bool IsLoadingApps
    {
        get => _isLoadingApps;
        private set => SetField(ref _isLoadingApps, value);
    }

    /// <summary>Мастер показан: подставить подключение из буфера обмена.</summary>
    public void Start() => AddConnection.OfferClipboard();

    public void Detach() => AddConnection.Added -= OnConnectionAdded;

    private void OnConnectionAdded(VpnConnection connection)
    {
        _connection = connection;
        Step = 2;
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (_connection is null || IsChecking)
            return;

        IsChecking = true;
        CheckOk = null;
        CheckTitle = $"Проверяю «{_connection.Name}»…";
        CheckDetails = "Лаунчер открывает маленькую страницу через сервер и узнаёт, каким адресом вас видят сайты.";
        try
        {
            var result = await _check(_connection);
            CheckOk = result.Ok;
            if (result.Ok)
            {
                CheckTitle = "Подключение работает";
                CheckDetails = string.Join(" · ", new[]
                {
                    result.LatencyMs is { } ms ? $"Задержка {ms} мс" : null,
                    result.Country ?? result.CountryCode,
                    result.Ip is { } ip ? $"IP {ip}" : null
                }.Where(p => !string.IsNullOrEmpty(p)));
            }
            else
            {
                CheckTitle = "Не удалось подключиться";
                CheckDetails = (result.Error ?? "Сервер не ответил.") + " Можно вернуться и вставить другую ссылку или продолжить: подключение можно поправить в настройках.";
            }
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task LoadAppsAsync()
    {
        if (_appsLoaded || IsLoadingApps)
            return;

        IsLoadingApps = true;
        try
        {
            var apps = await _findApps();
            foreach (var app in apps)
                Apps.Add(new StartMenuChoiceViewModel(app));
            _appsLoaded = true;

            // Иконки — после списка, чтобы он появился сразу.
            foreach (var choice in Apps.ToList())
            {
                choice.Icon = _icon(choice.Path);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        finally
        {
            IsLoadingApps = false;
        }
    }
}
