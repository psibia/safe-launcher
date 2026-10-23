using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.ViewModels;

/// <summary>
/// Мастер первого запуска из трёх шагов: подключение, проверка подключения и программы из меню «Пуск» с галочками.
/// </summary>
public sealed class WelcomeViewModel : ObservableObject
{
    private readonly Func<VpnConnection, Task<ConnectionCheck>> _check;
    private readonly Action<IReadOnlyList<StartMenuApp>> _finish;

    private int _step = 1;
    private VpnConnection? _connection;
    private bool _isChecking;
    private bool? _checkOk;
    private string _checkTitle = string.Empty;
    private string _checkDetails = string.Empty;

    /// <param name="check">Проверить подключение (задержка, IP, страна).</param>
    /// <param name="appList">Программы из меню «Пуск» с галочками.</param>
    /// <param name="finish">Добавить отмеченные программы и закрыть мастер.</param>
    /// <param name="skip">Закрыть мастер, ничего не добавляя.</param>
    public WelcomeViewModel(AddConnectionViewModel addConnection, Func<VpnConnection, Task<ConnectionCheck>> check,
        StartMenuListViewModel appList, Action<IReadOnlyList<StartMenuApp>> finish, Action skip)
    {
        AddConnection = addConnection;
        _check = check;
        AppList = appList;
        _finish = finish;
        AddConnection.Added += OnConnectionAdded;

        BackCommand = new RelayCommand(() => Step = Math.Max(1, Step - 1));
        NextCommand = new RelayCommand(() => Step = Math.Min(3, Step + 1));
        RecheckCommand = new RelayCommand(() => _ = CheckAsync(), () => !IsChecking);
        FinishCommand = new RelayCommand(() => _finish(AppList.Selected));
        SkipCommand = new RelayCommand(skip);
    }

    public AddConnectionViewModel AddConnection { get; }

    /// <summary>Шаг 3: программы из меню «Пуск».</summary>
    public StartMenuListViewModel AppList { get; }

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
                _ = AppList.LoadAsync();
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
}
