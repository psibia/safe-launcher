using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Файл .conf WireGuard или AmneziaWG (как его выдают провайдеры и программы WireGuard, AmneziaWG и AmneziaVPN) → подключение
/// ядра. Берётся один сервер ([Peer]). DNS из файла (первый адрес, который доступен через туннель) ядро использует для имён
/// приложения — через туннель: корпоративный сервер знает внутренние имена, а провайдер запросов не видит.
/// </summary>
public static class WireGuardConf
{
    /// <summary>Похож ли текст на файл WireGuard.</summary>
    public static bool LooksLikeConf(string? text) =>
        text is not null && text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) && text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);

    /// <param name="fallbackName">Название, если в файле его нет (обычно имя файла).</param>
    public static ParsedServer Parse(string text, string? fallbackName = null)
    {
        var sections = new List<(string Name, Dictionary<string, string> Values)>();
        string? name = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#'))
            {
                // «# Name = …» — так WireGuard и некоторые провайдеры подписывают туннель.
                var comment = line.TrimStart('#').Trim();
                if (comment.StartsWith("Name", StringComparison.OrdinalIgnoreCase) && comment.Contains('='))
                    name ??= comment[(comment.IndexOf('=') + 1)..].Trim();
                continue;
            }

            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash].Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                sections.Add((line[1..^1].Trim(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || sections.Count == 0)
                throw new ConnectionParseException($"Файл WireGuard повреждён: непонятная строка «{line}».");
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            var values = sections[^1].Values;
            // Повторяющиеся Address и AllowedIPs складываются.
            values[key] = values.TryGetValue(key, out var previous) && key is "Address" or "AllowedIPs" ? previous + "," + value : value;
        }

        var iface = sections.FirstOrDefault(s => s.Name.Equals("Interface", StringComparison.OrdinalIgnoreCase)).Values
                    ?? throw new ConnectionParseException("В файле WireGuard нет раздела [Interface].");
        var peers = sections.Where(s => s.Name.Equals("Peer", StringComparison.OrdinalIgnoreCase)).Select(s => s.Values).ToList();
        if (peers.Count == 0)
            throw new ConnectionParseException("В файле WireGuard нет раздела [Peer] с сервером.");
        if (peers.Count > 1)
            throw new ConnectionParseException("В файле WireGuard несколько серверов ([Peer]). Лаунчер поддерживает файл с одним сервером.");
        var peer = peers[0];

        if (peer.Keys.FirstOrDefault(AmneziaConf.IsKey) is { } misplaced)
            throw new ConnectionParseException($"В файле AmneziaWG параметр {misplaced} стоит в разделе [Peer], а должен быть в [Interface].");
        var amnezia = AmneziaConf.Parse(iface);

        var privateKey = Key(iface, "PrivateKey", "закрытого ключа (PrivateKey)");
        var publicKey = Key(peer, "PublicKey", "открытого ключа сервера (PublicKey)");
        var addresses = Prefixes(iface, "Address");
        if (addresses.Count == 0)
            throw new ConnectionParseException("В файле WireGuard нет адреса туннеля (Address).");

        if (!peer.TryGetValue("Endpoint", out var endpoint) || string.IsNullOrWhiteSpace(endpoint))
            throw new ConnectionParseException("В файле WireGuard нет адреса сервера (Endpoint).");
        var (host, port) = Endpoint(endpoint);

        var allowed = Prefixes(peer, "AllowedIPs");
        if (allowed.Count == 0)
            allowed = new List<string> { "0.0.0.0/0", "::/0" };

        var peerJson = new JsonObject
        {
            ["address"] = host,
            ["port"] = port,
            ["public_key"] = publicKey,
            ["allowed_ips"] = new JsonArray(allowed.Select(a => (JsonNode?)a).ToArray())
        };
        if (peer.TryGetValue("PresharedKey", out var psk) && psk.Length > 0)
            peerJson["pre_shared_key"] = Key(peer, "PresharedKey", "общего ключа (PresharedKey)");
        if (peer.TryGetValue("PersistentKeepalive", out var keepalive))
        {
            if (int.TryParse(keepalive, out var seconds) && seconds is > 0 and <= 65535)
                peerJson["persistent_keepalive_interval"] = seconds;
            // AmneziaWG: «20-30» — каждый раз случайный интервал из диапазона.
            else if (AmneziaConf.Range(keepalive) is { Hi: > 0 and <= 65535 } range)
            {
                if (range.Lo == range.Hi)
                    peerJson["persistent_keepalive_interval"] = (int)range.Lo;
                else
                    peerJson["persistent_keepalive_range"] = AmneziaConf.RangeText(range);
            }
        }

        var outbound = new JsonObject
        {
            ["type"] = "wireguard",
            ["address"] = new JsonArray(addresses.Select(a => (JsonNode?)a).ToArray()),
            ["private_key"] = privateKey,
            ["peers"] = new JsonArray(peerJson)
        };
        if (iface.TryGetValue("MTU", out var mtuText) && int.TryParse(mtuText, NumberStyles.None, CultureInfo.InvariantCulture, out var mtu) && mtu is >= 576 and <= 9000)
            outbound["mtu"] = mtu;
        if (amnezia is not null)
            outbound["amnezia"] = amnezia;

        var title = !string.IsNullOrWhiteSpace(name) ? name : !string.IsNullOrWhiteSpace(fallbackName) ? fallbackName.Trim() : $"{host}:{port}";
        return new ParsedServer(amnezia is null ? "WireGuard" : "AmneziaWG", title, host, port, "", outbound, TunnelDns(iface, allowed));
    }

    /// <summary>«AmneziaWG», если в тексте файла есть параметры AmneziaWG, иначе «WireGuard»: так подключение называется, даже если файл не читается.</summary>
    public static string ProtocolName(string? text)
    {
        foreach (var rawLine in (text ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash];
            var eq = line.IndexOf('=');
            if (eq > 0 && line[(eq + 1)..].Trim().Length > 0 && AmneziaConf.IsKey(line[..eq].Trim()))
                return "AmneziaWG";
        }

        return "WireGuard";
    }

    /// <summary>
    /// DNS из строки «DNS = 10.0.0.1, 1.1.1.1, corp.local» (имена — это домены поиска, они пропускаются): первый адрес,
    /// который попадает в AllowedIPs, то есть доступен через туннель. Нет такого — null (ядро возьмёт 1.1.1.1).
    /// </summary>
    internal static string? TunnelDns(Dictionary<string, string> iface, IReadOnlyList<string> allowed)
    {
        if (!iface.TryGetValue("DNS", out var text))
            return null;
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(item, out var ip) && !item.Contains('%') && allowed.Any(prefix => Covers(prefix, ip)))
                return ip.ToString();
        }

        return null;
    }

    private static bool Covers(string prefix, IPAddress ip)
    {
        var slash = prefix.IndexOf('/');
        if (slash < 0 || !IPAddress.TryParse(prefix[..slash], out var network) || !int.TryParse(prefix[(slash + 1)..], out var bits) ||
            network.AddressFamily != ip.AddressFamily)
            return false;
        var a = network.GetAddressBytes();
        var b = ip.GetAddressBytes();
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (b[i] & mask))
                return false;
        }

        return true;
    }

    private static string Key(Dictionary<string, string> values, string key, string what)
    {
        if (!values.TryGetValue(key, out var value) || value.Length == 0)
            throw new ConnectionParseException($"В файле WireGuard нет {what}.");
        try
        {
            if (Convert.FromBase64String(value).Length == 32)
                return value;
        }
        catch (FormatException)
        {
        }

        throw new ConnectionParseException($"В файле WireGuard повреждён ключ {key}.");
    }

    private static List<string> Prefixes(Dictionary<string, string> values, string key)
    {
        var result = new List<string>();
        if (!values.TryGetValue(key, out var text))
            return result;
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = item.IndexOf('/');
            var ipText = slash >= 0 ? item[..slash] : item;
            if (!IPAddress.TryParse(ipText, out var ip))
                throw new ConnectionParseException($"В файле WireGuard неверный адрес в {key}: «{item}».");
            var bits = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            if (slash >= 0 && (!int.TryParse(item[(slash + 1)..], out bits) || bits < 0 || bits > (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32)))
                throw new ConnectionParseException($"В файле WireGuard неверная маска в {key}: «{item}».");
            result.Add($"{ip}/{bits}");
        }

        return result;
    }

    private static (string Host, int Port) Endpoint(string text)
    {
        text = text.Trim();
        string host, port;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0 || close + 2 > text.Length || text[close + 1] != ':')
                throw new ConnectionParseException($"Неверный адрес сервера WireGuard: «{text}».");
            host = text[1..close];
            port = text[(close + 2)..];
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon <= 0)
                throw new ConnectionParseException($"В адресе сервера WireGuard нет порта: «{text}».");
            host = text[..colon];
            port = text[(colon + 1)..];
        }

        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
            throw new ConnectionParseException($"Неверный порт сервера WireGuard: «{port}».");
        return (host, number);
    }
}
