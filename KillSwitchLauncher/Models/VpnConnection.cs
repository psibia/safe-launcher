using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.Models;

/// <summary>Откуда берётся подключение лаунчера.</summary>
public enum ConnectionKind
{
    /// <summary>Ссылка на сервер: vless://, vmess://, trojan://, ss://, hysteria2://, tuic://.</summary>
    Link,

    /// <summary>Подписка: адрес, по которому провайдер отдаёт список серверов; список обновляется сам.</summary>
    Subscription,

    /// <summary>Файл .conf WireGuard.</summary>
    WireGuard,

    /// <summary>Локальный прокси другой программы (v2rayN, Clash Verge, Hiddify): лаунчер направляет трафик в него.</summary>
    LocalProxy
}

/// <summary>Протокол локального прокси.</summary>
public enum ProxyProtocol
{
    Socks5,
    Http
}

/// <summary>
/// Подключение, через которое ядро лаунчера ведёт трафик приложений (раздел «Подключения» в настройках).
/// Ключи доступа (ссылки, подписка, конфигурация WireGuard) хранятся зашифрованными средствами Windows,
/// см. <see cref="ProtectedStringConverter"/>.
/// </summary>
public sealed class VpnConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Название в списке подключений.</summary>
    public string Name { get; set; } = string.Empty;

    public ConnectionKind Kind { get; set; }

    /// <summary>Ссылка на сервер (<see cref="ConnectionKind.Link"/>).</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string? Link { get; set; }

    /// <summary>Адрес подписки (<see cref="ConnectionKind.Subscription"/>); в нём обычно есть личный ключ.</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string? SubscriptionUrl { get; set; }

    /// <summary>Серверы из последнего обновления подписки: по ссылке на каждый.</summary>
    [JsonConverter(typeof(ProtectedStringListConverter))]
    public List<string> SubscriptionServers { get; set; } = new();

    /// <summary>Выбранный сервер подписки: его ссылка без названия после «#». Null — первый в списке.</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string? SelectedServer { get; set; }

    /// <summary>Когда подписка последний раз успешно обновилась.</summary>
    public DateTimeOffset? SubscriptionUpdatedAt { get; set; }

    /// <summary>Как часто обновлять подписку, часов.</summary>
    public int SubscriptionUpdateHours { get; set; } = 24;

    /// <summary>Текст файла .conf (<see cref="ConnectionKind.WireGuard"/>): в нём закрытый ключ.</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string? WireGuardConfig { get; set; }

    /// <summary>Адрес локального прокси (<see cref="ConnectionKind.LocalProxy"/>).</summary>
    public string ProxyHost { get; set; } = "127.0.0.1";

    public int ProxyPort { get; set; }

    public ProxyProtocol ProxyProtocol { get; set; } = ProxyProtocol.Socks5;
}
