using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.ViewModels;

/// <summary>
/// Форма «Добавить подключение»: одно поле «Вставьте ссылку» (лаунчер сам поймёт, ссылка это, подписка или файл
/// WireGuard) и отдельно прокси другой VPN-программы. Одна и та же форма в настройках, на странице приложения и в мастере.
/// </summary>
public sealed class AddConnectionViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<VpnConnection>> _existing;
    private readonly Func<VpnConnection, Task> _add;
    private readonly IDialogService _dialogs;

    private string _text = string.Empty;
    private string? _fileName;
    private bool _isProxy;
    private string _proxyHost = "127.0.0.1";
    private string _proxyPort = string.Empty;
    private ProxyProtocol _proxyProtocol = ProxyProtocol.Socks5;
    private string? _error;
    private string? _recognized;
    private bool _isBusy;
    private LocalProxyHint? _proxyHint;

    /// <param name="existing">Уже добавленные подключения: новое получит название, которого среди них нет, а такое же не добавится.</param>
    /// <param name="add">Сохранить новое подключение (подписка к этому моменту уже загружена).</param>
    public AddConnectionViewModel(Func<IReadOnlyList<VpnConnection>> existing, Func<VpnConnection, Task> add, IDialogService dialogs)
    {
        _existing = existing;
        _add = add;
        _dialogs = dialogs;
        AddCommand = new RelayCommand(() => _ = AddAsync());
        PickFileCommand = new RelayCommand(PickFile);
        UseHintCommand = new RelayCommand(UseHint);
    }

    /// <summary>Подключение добавлено (уже сохранено в настройках).</summary>
    public event Action<VpnConnection>? Added;

    public ICommand AddCommand { get; }

    /// <summary>«Файл WireGuard или AmneziaWG»: прочитать .conf и подставить его текст.</summary>
    public ICommand PickFileCommand { get; }

    /// <summary>Подставить прокси запущенной VPN-программы.</summary>
    public ICommand UseHintCommand { get; }

    /// <summary>Ссылка, адрес подписки или текст файла WireGuard.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (!SetField(ref _text, value ?? string.Empty)) return;
            _fileName = null;
            Error = null;
            Recognize();
        }
    }

    /// <summary>Вкладка «Прокси другой программы» вместо поля для ссылки.</summary>
    public bool IsProxy
    {
        get => _isProxy;
        set
        {
            if (!SetField(ref _isProxy, value)) return;
            OnPropertyChanged(nameof(IsLink));
            Error = null;
            if (value && ProxyHint is null)
                ProxyHint = LocalProxies.Detect();
        }
    }

    /// <summary>Вкладка «Ссылка или файл» (обратная к <see cref="IsProxy"/>, для переключателя).</summary>
    public bool IsLink
    {
        get => !IsProxy;
        set => IsProxy = !value;
    }

    public string ProxyHost
    {
        get => _proxyHost;
        set => SetField(ref _proxyHost, value ?? string.Empty);
    }

    public string ProxyPort
    {
        get => _proxyPort;
        set => SetField(ref _proxyPort, value ?? string.Empty);
    }

    public ProxyProtocol ProxyProtocol
    {
        get => _proxyProtocol;
        set => SetField(ref _proxyProtocol, value);
    }

    /// <summary>Запущенная VPN-программа, чей прокси можно взять.</summary>
    public LocalProxyHint? ProxyHint
    {
        get => _proxyHint;
        private set
        {
            if (SetField(ref _proxyHint, value))
                OnPropertyChanged(nameof(ProxyHintText));
        }
    }

    public string? ProxyHintText => ProxyHint is { } hint
        ? $"Запущен {hint.Program}. Обычно его прокси — 127.0.0.1:{hint.Port}."
        : null;

    /// <summary>Что распознано во вставленном тексте, например «VLESS · Reality · example.com:443».</summary>
    public string? Recognized
    {
        get => _recognized;
        private set => SetField(ref _recognized, value);
    }

    /// <summary>Почему не добавилось: по-русски, для пользователя.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => Error is not null;

    /// <summary>Идёт загрузка подписки.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsBusy;

    /// <summary>Форма показана: если в буфере обмена подключение, подставляем его сами.</summary>
    public void OfferClipboard()
    {
        if (Text.Length > 0 || IsProxy)
            return;
        var clipboard = _dialogs.ReadClipboardText();
        if (ConnectionFactory.LooksLikeConnection(clipboard))
            Text = clipboard!.Trim();
    }

    public void Reset()
    {
        _text = string.Empty;
        _fileName = null;
        OnPropertyChanged(nameof(Text));
        Recognized = null;
        Error = null;
        ProxyPort = string.Empty;
    }

    /// <summary>Распознать и сохранить. Подписку сначала загружаем: без списка серверов подключаться не к чему.</summary>
    public async Task<VpnConnection?> AddAsync()
    {
        if (IsBusy)
            return null;

        Error = null;
        VpnConnection connection;
        try
        {
            connection = IsProxy
                ? ConnectionFactory.LocalProxy(ProxyHost, ProxyPort, ProxyProtocol, _existing())
                : ConnectionFactory.FromText(Text, _existing(), _fileName);
        }
        catch (ConnectionParseException ex)
        {
            Error = ex.Message;
            return null;
        }

        if (ConnectionFactory.FindSame(connection, _existing()) is { } same)
        {
            Error = $"Такое подключение уже есть: «{same.Name}».";
            return null;
        }

        IsBusy = true;
        try
        {
            if (connection.Kind == ConnectionKind.Subscription)
            {
                Recognized = "Загружаю подписку…";
                var content = await Subscriptions.FetchAsync(connection.SubscriptionUrl!);
                SubscriptionUpdater.Apply(connection, content, DateTimeOffset.Now);
            }

            await _add(connection);
        }
        catch (ConnectionParseException ex)
        {
            Recognize();
            Error = ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }

        Reset();
        Added?.Invoke(connection);
        return connection;
    }

    private void Recognize()
    {
        var text = Text.Trim();
        if (text.Length == 0)
        {
            Recognized = null;
            return;
        }

        try
        {
            var connection = ConnectionFactory.FromText(text, Array.Empty<VpnConnection>(), _fileName);
            Recognized = connection.Kind switch
            {
                ConnectionKind.Subscription => $"Подписка: {new Uri(connection.SubscriptionUrl!).Host}. Список серверов загрузится при добавлении.",
                _ => $"{connection.Name} · {ConnectionOutbound.Resolve(connection).Summary}"
            };
        }
        catch (ConnectionParseException)
        {
            Recognized = null; // объясним при нажатии «Добавить», чтобы не ругаться на каждую букву
        }
    }

    private void PickFile()
    {
        var path = _dialogs.PickWireGuardFile();
        if (path is null)
            return;

        try
        {
            var text = File.ReadAllText(path);
            IsProxy = false;
            Text = text.Trim();
            _fileName = Path.GetFileNameWithoutExtension(path);
            Recognize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "Не удалось прочитать файл: " + ex.Message;
        }
    }

    private void UseHint()
    {
        if (ProxyHint is not { } hint)
            return;
        ProxyHost = "127.0.0.1";
        ProxyPort = hint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ProxyProtocol = hint.Protocol;
    }
}
