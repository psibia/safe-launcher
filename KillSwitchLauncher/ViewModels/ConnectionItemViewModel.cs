using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.ViewModels;

/// <summary>Сервер подписки в списке выбора.</summary>
public sealed record SubscriptionServerOption(string Key, string Name);

/// <summary>Что лаунчер умеет делать с подключением из списка (реализует главное окно).</summary>
public interface IConnectionActions
{
    void Check(ConnectionItemViewModel item);
    void Rename(ConnectionItemViewModel item, string name);
    void Delete(ConnectionItemViewModel item);
    void UpdateSubscription(ConnectionItemViewModel item);
    void SelectServer(ConnectionItemViewModel item, string serverKey);
}

/// <summary>Строка подключения: в разделе «Подключения лаунчера» и в списках выбора подключения.</summary>
public sealed class ConnectionItemViewModel : ObservableObject
{
    private readonly IConnectionActions _actions;
    private string _usedBy = string.Empty;
    private bool _isDefault;
    private bool _isChecking;
    private bool _isUpdating;
    private string? _checkText;
    private bool? _checkOk;
    private bool _isRenaming;
    private string _editName = string.Empty;
    private bool _isServersExpanded;
    private string? _selectedServerKey;

    public ConnectionItemViewModel(VpnConnection connection, IConnectionActions actions)
    {
        Connection = connection;
        _actions = actions;
        CheckCommand = new RelayCommand(() => _actions.Check(this), () => !IsChecking);
        StartRenameCommand = new RelayCommand(() =>
        {
            EditName = Name;
            IsRenaming = true;
        });
        SaveNameCommand = new RelayCommand(() =>
        {
            IsRenaming = false;
            if (!string.IsNullOrWhiteSpace(EditName) && EditName.Trim() != Name)
                _actions.Rename(this, EditName.Trim());
        });
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        DeleteCommand = new RelayCommand(() => _actions.Delete(this));
        UpdateCommand = new RelayCommand(() => _actions.UpdateSubscription(this), () => !IsUpdating);
        Refresh();
    }

    public VpnConnection Connection { get; }

    public Guid Id => Connection.Id;

    public string Name => Connection.Name;

    /// <summary>«VLESS · Reality», «Подписка · 12 серверов», «WireGuard».</summary>
    public string KindText { get; private set; } = string.Empty;

    /// <summary>Вторая строка: сервер и, у подписки, когда она обновлялась.</summary>
    public string Details { get; private set; } = string.Empty;

    public bool IsSubscription => Connection.Kind == ConnectionKind.Subscription;

    /// <summary>Глиф вида подключения (Segoe Fluent Icons).</summary>
    public string Glyph => Connection.Kind switch
    {
        ConnectionKind.Subscription => "",
        ConnectionKind.WireGuard => "",
        ConnectionKind.LocalProxy => "",
        _ => ""
    };

    public ICommand CheckCommand { get; }
    public ICommand StartRenameCommand { get; }
    public ICommand SaveNameCommand { get; }
    public ICommand CancelRenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand UpdateCommand { get; }

    /// <summary>Кто им пользуется: «Основное · Discord, Telegram» или «Не используется».</summary>
    public string UsedBy
    {
        get => _usedBy;
        set => SetField(ref _usedBy, value);
    }

    /// <summary>Выбрано в общих настройках.</summary>
    public bool IsDefault
    {
        get => _isDefault;
        set => SetField(ref _isDefault, value);
    }

    public bool IsChecking
    {
        get => _isChecking;
        set
        {
            if (!SetField(ref _isChecking, value)) return;
            if (value)
            {
                CheckText = "Проверяю…";
                CheckOk = null;
            }

            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsUpdating
    {
        get => _isUpdating;
        set
        {
            if (SetField(ref _isUpdating, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Итог проверки: «42 мс · Германия · 1.2.3.4» или причина, почему не работает.</summary>
    public string? CheckText
    {
        get => _checkText;
        private set => SetField(ref _checkText, value);
    }

    /// <summary>true — работает, false — нет, null — не проверялось или идёт проверка.</summary>
    public bool? CheckOk
    {
        get => _checkOk;
        private set => SetField(ref _checkOk, value);
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set => SetField(ref _isRenaming, value);
    }

    public string EditName
    {
        get => _editName;
        set => SetField(ref _editName, value ?? string.Empty);
    }

    // ---------------------------------------------------------------- серверы подписки

    public IReadOnlyList<SubscriptionServerOption> Servers { get; private set; } = Array.Empty<SubscriptionServerOption>();

    public bool HasServers => Servers.Count > 1;

    public bool IsServersExpanded
    {
        get => _isServersExpanded;
        set => SetField(ref _isServersExpanded, value);
    }

    public string ServersToggleText => $"Сервер: {CurrentServerName}";

    private string CurrentServerName =>
        Servers.FirstOrDefault(s => s.Key == SelectedServerKey)?.Name ?? Servers.FirstOrDefault()?.Name ?? "—";

    /// <summary>Выбранный сервер подписки (ссылка без названия после «#»).</summary>
    public string? SelectedServerKey
    {
        get => _selectedServerKey;
        set
        {
            if (value is null || !SetField(ref _selectedServerKey, value)) return;
            OnPropertyChanged(nameof(ServersToggleText));
            _actions.SelectServer(this, value);
        }
    }

    public void SetResult(ConnectionCheck check)
    {
        IsChecking = false;
        CheckOk = check.Ok;
        CheckText = check.Ok
            ? string.Join(" · ", new[]
            {
                check.LatencyMs is { } ms ? $"{ms} мс" : null,
                check.Country ?? check.CountryCode,
                check.Ip
            }.Where(p => !string.IsNullOrEmpty(p)))
            : check.Error ?? "Не работает";
    }

    public void SetError(string message)
    {
        IsChecking = false;
        CheckOk = false;
        CheckText = message;
    }

    /// <summary>Перечитать подключение после переименования или обновления подписки.</summary>
    public void Refresh()
    {
        KindText = ConnectionFactory.Describe(Connection);
        Details = BuildDetails();

        if (IsSubscription)
        {
            Servers = Connection.SubscriptionServers
                .Select(s => new SubscriptionServerOption(Subscriptions.ServerKey(s), ConnectionFactory.ServerName(s)))
                .GroupBy(s => s.Key)
                .Select(g => g.First())
                .ToList();
            _selectedServerKey = Subscriptions.Pick(Connection.SubscriptionServers, Connection.SelectedServer) is { } picked
                ? Subscriptions.ServerKey(picked)
                : null;
        }

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(Servers));
        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(SelectedServerKey));
        OnPropertyChanged(nameof(ServersToggleText));
    }

    private string BuildDetails()
    {
        switch (Connection.Kind)
        {
            case ConnectionKind.Subscription:
                var updated = Connection.SubscriptionUpdatedAt is { } at
                    ? "обновлена " + at.ToLocalTime().ToString("d MMMM, HH:mm", new CultureInfo("ru-RU"))
                    : "ещё не загружалась";
                return $"{Host(Connection.SubscriptionUrl)} · {updated}";

            case ConnectionKind.LocalProxy:
                return "Трафик приложений пойдёт через эту программу";

            default:
                try
                {
                    var server = ConnectionOutbound.Resolve(Connection);
                    return server.Port > 0 ? $"{server.Server}:{server.Port}" : server.Server;
                }
                catch (ConnectionParseException ex)
                {
                    return ex.Message;
                }
        }
    }

    private static string Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "адрес не указан";
}
