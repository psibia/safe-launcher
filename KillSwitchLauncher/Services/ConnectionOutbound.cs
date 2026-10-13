using System.Text.Json.Nodes;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>Подключение из настроек → сервер, через который пойдёт трафик, в формате ядра.</summary>
public static class ConnectionOutbound
{
    /// <summary>Какой сервер стоит за подключением. Если подключение не настроено — <see cref="ConnectionParseException"/>.</summary>
    public static ParsedServer Resolve(VpnConnection connection)
    {
        switch (connection.Kind)
        {
            case ConnectionKind.Link:
                if (string.IsNullOrWhiteSpace(connection.Link))
                    throw new ConnectionParseException($"У подключения «{connection.Name}» нет ссылки.");
                return ConnectionLinks.Parse(connection.Link);

            case ConnectionKind.Subscription:
                var server = Subscriptions.Pick(connection.SubscriptionServers, connection.SelectedServer)
                             ?? throw new ConnectionParseException($"Подписка «{connection.Name}» ещё не загружена или в ней нет серверов.");
                return ConnectionLinks.Parse(server);

            case ConnectionKind.WireGuard:
                if (string.IsNullOrWhiteSpace(connection.WireGuardConfig))
                    throw new ConnectionParseException($"У подключения «{connection.Name}» нет файла WireGuard.");
                return WireGuardConf.Parse(connection.WireGuardConfig, connection.Name);

            case ConnectionKind.LocalProxy:
                return LocalProxy(connection);

            default:
                throw new ConnectionParseException($"Неизвестный вид подключения у «{connection.Name}».");
        }
    }

    /// <summary>Сервер подключения с меткой для ядра.</summary>
    public static JsonObject Build(VpnConnection connection, string tag) => BuildWithDns(connection, tag).Outbound;

    /// <summary>Сервер подключения с меткой для ядра и DNS внутри подключения (из файла WireGuard), если он есть.</summary>
    public static (JsonObject Outbound, string? Dns) BuildWithDns(VpnConnection connection, string tag)
    {
        var server = Resolve(connection);
        var outbound = (JsonObject)server.Outbound.DeepClone();
        outbound["tag"] = tag;
        return (outbound, server.Dns);
    }

    /// <summary>Прокси другой VPN-программы на этом компьютере (v2rayN, Clash Verge, Hiddify).</summary>
    private static ParsedServer LocalProxy(VpnConnection connection)
    {
        var host = connection.ProxyHost?.Trim();
        if (string.IsNullOrEmpty(host))
            throw new ConnectionParseException($"У подключения «{connection.Name}» не указан адрес прокси.");
        if (connection.ProxyPort is < 1 or > 65535)
            throw new ConnectionParseException($"У подключения «{connection.Name}» не указан порт прокси.");

        var socks = connection.ProxyProtocol == ProxyProtocol.Socks5;
        var outbound = new JsonObject
        {
            ["type"] = socks ? "socks" : "http",
            ["server"] = host,
            ["server_port"] = connection.ProxyPort
        };
        if (socks)
            outbound["version"] = "5";
        return new ParsedServer(socks ? "SOCKS5" : "HTTP", connection.Name, host, connection.ProxyPort, "локальный прокси", outbound);
    }
}
