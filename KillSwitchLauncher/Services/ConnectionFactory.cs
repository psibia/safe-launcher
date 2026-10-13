using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Новое подключение из того, что вставил пользователь: ссылка на сервер, адрес подписки или текст файла WireGuard
/// (лаунчер сам понимает, что это), либо прокси другой VPN-программы. И короткие описания подключений для списков.
/// </summary>
public static class ConnectionFactory
{
    /// <summary>Похоже ли на подключение: так лаунчер решает, подставлять ли текст из буфера обмена.</summary>
    public static bool LooksLikeConnection(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (WireGuardConf.LooksLikeConf(text) || ConnectionLinks.LooksLikeLink(text) || Subscriptions.LooksLikeUrl(text) && !text.Trim().Contains('\n'));

    /// <summary>
    /// Распознаёт вставленный текст. Подписка возвращается ещё не загруженной: её список серверов скачивает вызывающий.
    /// Если распознать нельзя — <see cref="ConnectionParseException"/> с понятным объяснением.
    /// </summary>
    /// <param name="fileName">Имя файла WireGuard без расширения: название подключения, если в файле его нет.</param>
    public static VpnConnection FromText(string? text, IEnumerable<VpnConnection> existing, string? fileName = null)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0)
            throw new ConnectionParseException("Вставьте ссылку на сервер, адрес подписки или содержимое файла WireGuard.");

        if (WireGuardConf.LooksLikeConf(value))
        {
            var server = WireGuardConf.Parse(value, fileName);
            return New(ConnectionKind.WireGuard, server.Name, existing, c => c.WireGuardConfig = value);
        }

        var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1)
            throw new ConnectionParseException(lines.All(ConnectionLinks.LooksLikeLink)
                ? "Вставьте одну ссылку. Если серверов несколько, добавьте адрес подписки: лаунчер сам загрузит их список."
                : "Вставьте одну ссылку на сервер или один адрес подписки.");

        if (ConnectionLinks.LooksLikeLink(value))
        {
            var server = ConnectionLinks.Parse(value);
            return New(ConnectionKind.Link, server.Name, existing, c => c.Link = value);
        }

        if (Subscriptions.LooksLikeUrl(value))
        {
            var host = new Uri(value).Host;
            return New(ConnectionKind.Subscription, "Подписка " + host, existing, c => c.SubscriptionUrl = value);
        }

        throw new ConnectionParseException(
            "Не получилось распознать. Подойдёт ссылка vless://, vmess://, trojan://, ss://, hysteria2://, tuic:// или socks://, " +
            "адрес подписки https://… или содержимое файла WireGuard (.conf).");
    }

    /// <summary>Прокси другой VPN-программы на этом компьютере (v2rayN, Clash Verge, Hiddify).</summary>
    public static VpnConnection LocalProxy(string? host, string? portText, ProxyProtocol protocol, IEnumerable<VpnConnection> existing)
    {
        var address = host?.Trim();
        if (string.IsNullOrEmpty(address))
            throw new ConnectionParseException("Укажите адрес прокси, обычно 127.0.0.1.");
        if (!int.TryParse(portText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new ConnectionParseException("Укажите порт прокси: число от 1 до 65535. Его можно посмотреть в настройках VPN-программы.");

        return New(ConnectionKind.LocalProxy, $"Прокси {address}:{port}", existing, c =>
        {
            c.ProxyHost = address;
            c.ProxyPort = port;
            c.ProxyProtocol = protocol;
        });
    }

    /// <summary>Название, которого ещё нет в списке: «Германия», «Германия 2», «Германия 3»…</summary>
    public static string UniqueName(string name, IEnumerable<VpnConnection> existing, Guid? except = null)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Подключение" : name.Trim();
        if (baseName.Length > 60)
            baseName = baseName[..60].TrimEnd();
        var taken = new HashSet<string>(existing.Where(c => c.Id != except).Select(c => c.Name), StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(baseName))
            return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>Вид подключения для списка: «VLESS · Reality», «Подписка · 12 серверов», «WireGuard», «SOCKS5 127.0.0.1:10808».</summary>
    public static string Describe(VpnConnection connection)
    {
        switch (connection.Kind)
        {
            case ConnectionKind.Subscription:
                var count = connection.SubscriptionServers.Count;
                return count == 0 ? "Подписка · ещё не загружена" : $"Подписка · {ServersText(count)}";
            case ConnectionKind.LocalProxy:
                return $"{(connection.ProxyProtocol == ProxyProtocol.Socks5 ? "SOCKS5" : "HTTP")} {connection.ProxyHost}:{connection.ProxyPort} · прокси другой программы";
        }

        try
        {
            var server = ConnectionOutbound.Resolve(connection);
            return string.IsNullOrEmpty(server.Details) ? server.Protocol : $"{server.Protocol} · {server.Details}";
        }
        catch (ConnectionParseException)
        {
            return connection.Kind == ConnectionKind.WireGuard ? "WireGuard · файл повреждён" : "Ссылка не распознаётся";
        }
    }

    /// <summary>Название сервера подписки (то, что после «#» в ссылке), иначе адрес сервера.</summary>
    public static string ServerName(string link)
    {
        try
        {
            return ConnectionLinks.Parse(link).Name;
        }
        catch (ConnectionParseException)
        {
            return Subscriptions.ServerKey(link);
        }
    }

    public static string ServersText(int count)
    {
        var word = (count % 100) is >= 11 and <= 14 ? "серверов"
            : (count % 10) switch { 1 => "сервер", 2 or 3 or 4 => "сервера", _ => "серверов" };
        return $"{count} {word}";
    }

    private static VpnConnection New(ConnectionKind kind, string name, IEnumerable<VpnConnection> existing, Action<VpnConnection> fill)
    {
        var connection = new VpnConnection { Kind = kind, Name = UniqueName(name, existing) };
        fill(connection);
        return connection;
    }
}
