using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Ключ vpn:// программы AmneziaVPN → текст файла AmneziaWG или WireGuard, дальше он работает как файл .conf.
/// Ключ — это сжатые настройки сервера (JSON). В ключе от своего сервера («Поделиться» → формат «Для приложения AmneziaVPN»)
/// есть тот же текст файла, что AmneziaVPN сохраняет в «Оригинальном формате AmneziaWG», только адреса DNS она подставляет
/// при подключении. Ключи сервиса Amnezia (Premium, Free) настроек не содержат: AmneziaVPN получает их с серверов Amnezia.
/// </summary>
public static class AmneziaVpnKey
{
    private const string Prefix = "vpn://";

    /// <summary>Больше этого ключ не распаковывается: настоящий занимает несколько килобайт.</summary>
    private const int MaxJson = 1 << 20;

    /// <summary>MTU, с которым по ключу подключается AmneziaVPN для Windows: в тексте файла его нет, она ставит его сама.</summary>
    private const int DefaultMtu = 1376;

    private const string HowToShare = "В AmneziaVPN откройте «Поделиться», выберите протокол AmneziaWG и формат «Для приложения AmneziaVPN».";

    /// <summary>Подключения, которые лаунчер берёт из ключа: контейнер AmneziaVPN и раздел с его настройками.</summary>
    private static readonly (string Container, string Section)[] Supported =
    {
        ("amnezia-awg2", "awg"), ("amnezia-awg", "awg"), ("amnezia-wireguard", "wireguard")
    };

    /// <summary>Как AmneziaVPN называет остальные свои протоколы.</summary>
    private static readonly Dictionary<string, string> OtherProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amnezia-openvpn"] = "OpenVPN",
        ["amnezia-openvpn-cloak"] = "OpenVPN over Cloak",
        ["amnezia-shadowsocks"] = "OpenVPN over Shadowsocks",
        ["amnezia-xray"] = "XRay",
        ["amnezia-ssxray"] = "Shadowsocks",
        ["amnezia-ipsec"] = "IKEv2"
    };

    private static readonly Regex Placeholder = new(@"\$[A-Z][A-Z0-9_]*", RegexOptions.CultureInvariant);
    private static readonly Regex MtuLine = new(@"^[ \t]*MTU[ \t]*=", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InterfaceLine = new(@"^[ \t]*\[Interface\][ \t]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Похож ли текст на ключ AmneziaVPN.</summary>
    public static bool LooksLikeKey(string? text) =>
        text is not null && text.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Текст файла .conf из ключа и название сервера, если оно в ключе есть. Ключ не подходит —
    /// <see cref="ConnectionParseException"/> с объяснением.
    /// </summary>
    public static (string Conf, string? Name) ToConf(string text)
    {
        using var document = Open(text);
        var root = document.RootElement;

        // Ключ сервиса Amnezia: вместо настроек в нём ключ доступа к серверам Amnezia.
        if (Has(root, "api_config") || Has(root, "auth_data") || Has(root, "api_key") || Has(root, "api_endpoint"))
            throw new ConnectionParseException(
                "Это ключ сервиса Amnezia (Premium или Free). Настроек подключения в нём нет: AmneziaVPN получает их с серверов Amnezia. " +
                "Подойдёт ключ от своего сервера или файл AmneziaWG (.conf).");

        var newer = root.TryGetProperty("format_version", out var version) && version.ValueKind == JsonValueKind.Number &&
                    version.TryGetInt32(out var number) && number > 1;
        var containers = root.TryGetProperty("containers", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).ToList()
            : new List<JsonElement>();
        var defaultContainer = Text(root, "defaultContainer");

        // Подключение по умолчанию первым, затем AmneziaWG, затем WireGuard.
        var candidates = containers
            .Select(c => (Element: c, Name: Text(c, "container"), Index: Array.FindIndex(Supported, s => s.Container.Equals(Text(c, "container"), StringComparison.OrdinalIgnoreCase))))
            .Where(c => c.Index >= 0)
            .OrderBy(c => string.Equals(c.Name, defaultContainer, StringComparison.OrdinalIgnoreCase) ? -1 : c.Index)
            .ToList();
        if (candidates.Count == 0)
        {
            if (containers.Count == 0)
                throw Fail("В ключе vpn:// нет настроек подключения. " + HowToShare, newer);
            var names = containers.Select(c => Text(c, "container") ?? "?").Select(n => OtherProtocols.TryGetValue(n, out var human) ? human : n).Distinct();
            throw Fail($"В ключе vpn:// подключение {string.Join(", ", names)}, а лаунчер берёт из таких ключей только AmneziaWG и WireGuard. {HowToShare}", newer);
        }

        foreach (var (element, _, index) in candidates)
        {
            if (element.TryGetProperty(Supported[index].Section, out var protocol) && LastConfig(protocol) is { } last &&
                Text(last, "config") is { } conf && WireGuardConf.LooksLikeConf(conf))
                return (Fill(conf, root), Text(root, "description"));
        }

        // В ключе полного доступа данные для входа на сервер, а настроек подключения нет: AmneziaVPN создаёт их сама.
        if (Has(root, "userName") || Has(root, "password"))
            throw new ConnectionParseException("Это ключ полного доступа к серверу: в нём пароль от сервера, а не подключение. " + HowToShare);
        throw Fail("В ключе vpn:// нет настроек подключения AmneziaWG. " + HowToShare, newer);
    }

    /// <summary>JSON из ключа: base64url, а в нём qCompress из Qt (длина и поток zlib) или несжатый текст.</summary>
    private static JsonDocument Open(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[Prefix.Length..];
        // Мессенджеры и почта переносят длинный ключ на несколько строк.
        var body = string.Concat(trimmed.Where(c => !char.IsWhiteSpace(c)));
        if (body.Length > MaxJson)
            throw TooBig();

        var base64 = body.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4));
        }
        catch (FormatException)
        {
            throw Broken();
        }

        // У ключей сервиса Amnezia вместо длины метка 000000ff, поэтому длина не проверяется.
        var json = (IsZlib(data, 4) ? Inflate(data, 4) : null) ?? (IsZlib(data, 0) ? Inflate(data, 0) : null) ?? data;
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;
            document.Dispose();
        }
        catch (JsonException)
        {
        }

        throw Broken();
    }

    private static bool IsZlib(byte[] data, int offset) =>
        data.Length > offset + 2 && (data[offset] & 0x0F) == 8 && data[offset] >> 4 <= 7 && ((data[offset] << 8) | data[offset + 1]) % 31 == 0;

    /// <summary>Распакованный поток zlib; null — это не он.</summary>
    private static byte[]? Inflate(byte[] data, int offset)
    {
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(data, offset, data.Length - offset), CompressionMode.Decompress);
            using var result = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (result.Length + read > MaxJson)
                    throw TooBig();
                result.Write(buffer, 0, read);
            }

            return result.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Настройки подключения из раздела протокола: в ключе это JSON, записанный строкой.</summary>
    private static JsonElement? LastConfig(JsonElement protocol)
    {
        if (protocol.ValueKind != JsonValueKind.Object || !protocol.TryGetProperty("last_config", out var last))
            return null;
        if (last.ValueKind == JsonValueKind.Object)
            return last;
        if (last.ValueKind != JsonValueKind.String)
            return null;
        try
        {
            using var inner = JsonDocument.Parse(last.GetString()!);
            return inner.RootElement.ValueKind == JsonValueKind.Object ? inner.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Подставляет в текст файла то, что AmneziaVPN подставляет при подключении: DNS и MTU.</summary>
    private static string Fill(string conf, JsonElement root)
    {
        // DNS из ключа, если там адреса IPv4, иначе те, что AmneziaVPN берёт по умолчанию.
        var text = conf.Replace("\r\n", "\n").Trim()
            .Replace("$PRIMARY_DNS", Dns(root, "dns1", "1.1.1.1"))
            .Replace("$SECONDARY_DNS", Dns(root, "dns2", "1.0.0.1"));
        if (Placeholder.Match(text) is { Success: true } left)
            throw new ConnectionParseException($"В ключе vpn:// не заполнены настройки ({left.Value}). Создайте ключ в AmneziaVPN заново.");
        if (!MtuLine.IsMatch(text))
            text = InterfaceLine.Replace(text, m => m.Value + "\nMTU = " + DefaultMtu.ToString(CultureInfo.InvariantCulture), 1);
        return text;
    }

    private static string Dns(JsonElement root, string key, string fallback) =>
        Text(root, key) is { } value && IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() == value
            ? value
            : fallback;

    private static string? Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString()!.Trim() is { Length: > 0 } text
            ? text
            : null;

    private static bool Has(JsonElement element, string key) => element.TryGetProperty(key, out _);

    private static ConnectionParseException Fail(string message, bool newer) =>
        new(newer ? message + " Ключ создан более новой версией AmneziaVPN, чем знает лаунчер." : message);

    private static ConnectionParseException Broken() =>
        new("Ключ vpn:// не читается: похоже, он скопирован не целиком. Скопируйте его из AmneziaVPN ещё раз.");

    private static ConnectionParseException TooBig() =>
        new("Ключ vpn:// слишком большой: ключ подключения AmneziaVPN занимает несколько килобайт.");
}
