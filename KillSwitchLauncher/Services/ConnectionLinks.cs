using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KillSwitchLauncher.Services;

/// <summary>Ссылку или файл подключения не удалось разобрать. Сообщение — для пользователя.</summary>
public sealed class ConnectionParseException : Exception
{
    public ConnectionParseException(string message) : base(message)
    {
    }
}

/// <summary>
/// Распознанный сервер: что показать пользователю и подключение в формате ядра (outbound или endpoint sing-box
/// без tag — его задаёт лаунчер).
/// </summary>
/// <param name="Dns">DNS-сервер внутри подключения (из файла WireGuard), если он есть: через него приложение ищет имена.</param>
public sealed record ParsedServer(string Protocol, string Name, string Server, int Port, string Details, JsonObject Outbound, string? Dns = null)
{
    /// <summary>Для списка подключений: «VLESS · Reality · example.com:443».</summary>
    public string Summary => string.Join(" · ", new[] { Protocol, Details, $"{FormatHost(Server)}:{Port}" }.Where(s => s.Length > 0));

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;
}

/// <summary>
/// Ссылки на серверы, какими их раздают провайдеры и программы v2rayN, Hiddify, Nekobox:
/// vless://, vmess://, trojan://, ss://, hysteria2:// (hy2://), tuic://, socks://.
/// Превращает ссылку в подключение для ядра или объясняет, что в ней не так.
/// </summary>
public static class ConnectionLinks
{
    private static readonly string[] Schemes = { "vless", "vmess", "trojan", "ss", "hysteria2", "hy2", "tuic", "socks", "socks5" };

    private static readonly HashSet<string> UtlsFingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized"
    };

    private static readonly HashSet<string> ShadowsocksMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
        "none", "aes-128-gcm", "aes-192-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
        "aes-128-ctr", "aes-192-ctr", "aes-256-ctr", "aes-128-cfb", "aes-192-cfb", "aes-256-cfb",
        "rc4-md5", "chacha20-ietf", "xchacha20"
    };

    /// <summary>Похож ли текст на ссылку на сервер (например, при вставке из буфера обмена).</summary>
    public static bool LooksLikeLink(string? text)
    {
        var scheme = SchemeOf(text?.Trim());
        return scheme is not null && Schemes.Contains(scheme);
    }

    /// <summary>Разбирает ссылку. Если что-то не так — <see cref="ConnectionParseException"/> с понятным объяснением.</summary>
    public static ParsedServer Parse(string link)
    {
        link = link.Trim();
        var scheme = SchemeOf(link)
                     ?? throw new ConnectionParseException("Это не ссылка на сервер: она должна начинаться с vless://, vmess://, trojan://, ss://, hysteria2://, tuic:// или socks://.");
        return scheme switch
        {
            "vless" => Vless(Split(link)),
            "vmess" => Vmess(link),
            "trojan" => Trojan(Split(link)),
            "ss" => Shadowsocks(link),
            "hysteria2" or "hy2" => Hysteria2(Split(link)),
            "tuic" => Tuic(Split(link)),
            "socks" or "socks5" => Socks(Split(link)),
            "hysteria" => throw new ConnectionParseException("Hysteria первой версии не поддерживается, только Hysteria2."),
            "wireguard" or "wg" => throw new ConnectionParseException("Для WireGuard добавьте файл .conf, а не ссылку."),
            "http" or "https" => throw new ConnectionParseException("Это адрес сайта. Если это подписка, добавьте её как подписку."),
            _ => throw new ConnectionParseException($"Ссылки «{scheme}://» не поддерживаются. Поддерживаются vless, vmess, trojan, ss, hysteria2, tuic и socks.")
        };
    }

    // ---- протоколы

    private static ParsedServer Vless(LinkParts p)
    {
        var uuid = RequireUuid(p.UserInfo, "VLESS");
        var q = p.Query;
        var encryption = Get(q, "encryption");
        if (encryption is not null && encryption != "none")
            throw new ConnectionParseException($"Шифрование VLESS «{encryption}» ядро не поддерживает.");

        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["server"] = p.Host,
            ["server_port"] = p.Port,
            ["uuid"] = uuid
        };
        var flow = Get(q, "flow");
        if (!string.IsNullOrEmpty(flow))
        {
            if (!flow.StartsWith("xtls-rprx-vision", StringComparison.Ordinal))
                throw new ConnectionParseException($"Режим VLESS «{flow}» ядро не поддерживает.");
            outbound["flow"] = "xtls-rprx-vision";
        }

        outbound["packet_encoding"] = "xudp";
        var (tls, security) = Tls(q, defaultOn: false);
        var (transport, transportName) = Transport(q);
        if (tls is not null)
            outbound["tls"] = tls;
        if (transport is not null)
            outbound["transport"] = transport;
        return Result("VLESS", p, Details(transportName, security), outbound);
    }

    private static ParsedServer Vmess(string link)
    {
        var body = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var hash = body.IndexOf('#');
        if (hash >= 0)
            body = body[..hash];

        // Формат v2rayN: base64 от JSON. Встречается и обычный вид vmess://uuid@сервер:порт?…
        var json = TryBase64(body);
        if (json is null || !json.TrimStart().StartsWith('{'))
        {
            if (body.Contains('@'))
                return VmessUri(Split(link));
            throw new ConnectionParseException("Ссылка VMess повреждена: внутри должен быть JSON в base64.");
        }

        JsonObject v;
        try
        {
            v = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new ConnectionParseException("Ссылка VMess повреждена: не удалось прочитать её содержимое.");
        }

        string? S(string key) => v[key] is { } node ? node.GetValueKind() == JsonValueKind.String ? (string?)node : node.ToJsonString() : null;

        var server = S("add");
        if (string.IsNullOrWhiteSpace(server))
            throw new ConnectionParseException("В ссылке VMess нет адреса сервера.");
        var port = ParsePort(S("port"), null);
        var uuid = RequireUuid(S("id"), "VMess");

        // Те же поля, что в ссылках vless://, чтобы разобрать транспорт и TLS одинаково.
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                q[key] = value;
        }

        Put("type", S("net"));
        Put("headerType", S("type"));
        Put("host", S("host"));
        Put("path", S("path"));
        Put("security", S("tls") is "tls" or "reality" ? S("tls") : "none");
        Put("sni", S("sni"));
        Put("alpn", S("alpn"));
        Put("fp", S("fp"));
        Put("allowInsecure", S("allowInsecure") ?? S("skip-cert-verify"));
        if (S("net") == "grpc")
            Put("serviceName", S("path"));

        var outbound = new JsonObject
        {
            ["type"] = "vmess",
            ["server"] = server.Trim(),
            ["server_port"] = port,
            ["uuid"] = uuid,
            ["security"] = VmessSecurity(S("scy")),
            ["alter_id"] = int.TryParse(S("aid"), out var aid) ? aid : 0
        };
        var (tls, security) = Tls(q, defaultOn: false);
        var (transport, transportName) = Transport(q);
        if (tls is not null)
            outbound["tls"] = tls;
        if (transport is not null)
            outbound["transport"] = transport;

        var name = S("ps");
        return new ParsedServer("VMess", string.IsNullOrWhiteSpace(name) ? $"{server}:{port}" : name.Trim(), server.Trim(), port,
            Details(transportName, security), outbound);
    }

    private static ParsedServer VmessUri(LinkParts p)
    {
        var q = p.Query;
        var outbound = new JsonObject
        {
            ["type"] = "vmess",
            ["server"] = p.Host,
            ["server_port"] = p.Port,
            ["uuid"] = RequireUuid(p.UserInfo, "VMess"),
            ["security"] = VmessSecurity(Get(q, "encryption") ?? Get(q, "scy")),
            ["alter_id"] = int.TryParse(Get(q, "aid"), out var aid) ? aid : 0
        };
        var (tls, security) = Tls(q, defaultOn: false);
        var (transport, transportName) = Transport(q);
        if (tls is not null)
            outbound["tls"] = tls;
        if (transport is not null)
            outbound["transport"] = transport;
        return Result("VMess", p, Details(transportName, security), outbound);
    }

    private static string VmessSecurity(string? value) => value switch
    {
        null or "" => "auto",
        "auto" or "none" or "zero" or "aes-128-gcm" or "chacha20-poly1305" or "aes-128-ctr" => value,
        _ => throw new ConnectionParseException($"Шифрование VMess «{value}» ядро не поддерживает.")
    };

    private static ParsedServer Trojan(LinkParts p)
    {
        if (string.IsNullOrEmpty(p.UserInfo))
            throw new ConnectionParseException("В ссылке Trojan нет пароля.");
        var outbound = new JsonObject
        {
            ["type"] = "trojan",
            ["server"] = p.Host,
            ["server_port"] = p.Port,
            ["password"] = p.UserInfo
        };
        var (tls, security) = Tls(p.Query, defaultOn: true);
        var (transport, transportName) = Transport(p.Query);
        if (tls is not null)
            outbound["tls"] = tls;
        if (transport is not null)
            outbound["transport"] = transport;
        return Result("Trojan", p, Details(transportName, security == "TLS" ? "" : security), outbound);
    }

    /// <summary>
    /// ss://base64(метод:пароль)@сервер:порт (SIP002), ss://метод:пароль@сервер:порт (так пишут ключи 2022)
    /// и старый вид ss://base64(метод:пароль@сервер:порт).
    /// </summary>
    private static ParsedServer Shadowsocks(string link)
    {
        var body = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var hash = body.IndexOf('#');
        var name = hash >= 0 ? Unescape(body[(hash + 1)..]) : null;
        if (hash >= 0)
            body = body[..hash];
        var queryStart = body.IndexOf('?');
        var query = queryStart >= 0 ? ParseQuery(body[(queryStart + 1)..]) : new Dictionary<string, string>();
        if (queryStart >= 0)
            body = body[..queryStart];
        body = body.TrimEnd('/');

        if (!body.Contains('@'))
        {
            body = TryBase64(body) ?? throw new ConnectionParseException("Ссылка Shadowsocks повреждена.");
            if (!body.Contains('@'))
                throw new ConnectionParseException("В ссылке Shadowsocks нет адреса сервера.");
        }

        var at = body.LastIndexOf('@');
        var userInfo = body[..at];
        var (host, port) = HostPort(body[(at + 1)..], null);

        string credentials;
        var decoded = TryBase64(Unescape(userInfo));
        if (decoded is not null && decoded.Contains(':') && ShadowsocksMethods.Contains(decoded[..decoded.IndexOf(':')]))
            credentials = decoded;
        else
            credentials = Unescape(userInfo);
        var colon = credentials.IndexOf(':');
        if (colon <= 0)
            throw new ConnectionParseException("В ссылке Shadowsocks нет метода шифрования и пароля.");
        var method = credentials[..colon].ToLowerInvariant();
        var password = credentials[(colon + 1)..];
        if (!ShadowsocksMethods.Contains(method))
            throw new ConnectionParseException($"Шифрование Shadowsocks «{method}» ядро не поддерживает.");

        var outbound = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["server"] = host,
            ["server_port"] = port,
            ["method"] = method,
            ["password"] = password
        };

        var details = "";
        if (Get(query, "plugin") is { Length: > 0 } plugin)
        {
            var semicolon = plugin.IndexOf(';');
            var pluginName = semicolon >= 0 ? plugin[..semicolon] : plugin;
            var options = semicolon >= 0 ? plugin[(semicolon + 1)..] : "";
            pluginName = pluginName switch
            {
                "obfs-local" or "simple-obfs" => "obfs-local",
                "v2ray-plugin" => "v2ray-plugin",
                _ => throw new ConnectionParseException($"Плагин Shadowsocks «{pluginName}» ядро не поддерживает.")
            };
            outbound["plugin"] = pluginName;
            if (options.Length > 0)
                outbound["plugin_opts"] = options;
            details = pluginName;
        }

        return new ParsedServer("Shadowsocks", string.IsNullOrWhiteSpace(name) ? $"{host}:{port}" : name.Trim(), host, port, details, outbound);
    }

    private static ParsedServer Hysteria2(LinkParts p)
    {
        var q = p.Query;
        var outbound = new JsonObject
        {
            ["type"] = "hysteria2",
            ["server"] = p.Host,
            ["server_port"] = p.Port
        };
        if (!string.IsNullOrEmpty(p.UserInfo))
            outbound["password"] = p.UserInfo;

        // Перескок портов: mport=20000-30000 или 443,20000-30000.
        if ((Get(q, "mport") ?? Get(q, "ports")) is { Length: > 0 } ports)
        {
            var list = new JsonArray();
            foreach (var part in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var range = part.Split('-', ':');
                if (range.Length is < 1 or > 2 || range.Any(r => !int.TryParse(r, out var n) || n is < 1 or > 65535))
                    throw new ConnectionParseException($"Непонятный диапазон портов Hysteria2: «{ports}».");
                list.Add(range.Length == 1 ? $"{range[0]}:{range[0]}" : $"{range[0]}:{range[1]}");
            }

            outbound["server_ports"] = list;
        }

        var obfs = Get(q, "obfs");
        if (!string.IsNullOrEmpty(obfs) && obfs != "none")
        {
            if (obfs != "salamander")
                throw new ConnectionParseException($"Маскировка Hysteria2 «{obfs}» ядро не поддерживает.");
            outbound["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = Get(q, "obfs-password") ?? "" };
        }

        var tls = new JsonObject { ["enabled"] = true };
        if (Get(q, "sni") is { Length: > 0 } sni)
            tls["server_name"] = sni;
        if (IsTrue(Get(q, "insecure")) || IsTrue(Get(q, "allowInsecure")))
            tls["insecure"] = true;
        tls["alpn"] = new JsonArray("h3");
        outbound["tls"] = tls;
        return Result("Hysteria2", p, obfs == "salamander" ? "Salamander" : "", outbound);
    }

    private static ParsedServer Tuic(LinkParts p)
    {
        var q = p.Query;
        var colon = p.UserInfo.IndexOf(':');
        if (colon <= 0)
            throw new ConnectionParseException("В ссылке TUIC должны быть UUID и пароль: tuic://uuid:пароль@сервер:порт.");
        var outbound = new JsonObject
        {
            ["type"] = "tuic",
            ["server"] = p.Host,
            ["server_port"] = p.Port,
            ["uuid"] = RequireUuid(p.UserInfo[..colon], "TUIC"),
            ["password"] = p.UserInfo[(colon + 1)..]
        };
        if ((Get(q, "congestion_control") ?? Get(q, "congestion")) is { Length: > 0 } congestion)
        {
            if (congestion is not ("cubic" or "new_reno" or "bbr"))
                throw new ConnectionParseException($"Алгоритм TUIC «{congestion}» ядро не поддерживает.");
            outbound["congestion_control"] = congestion;
        }

        if (Get(q, "udp_relay_mode") is { Length: > 0 } relay)
        {
            if (relay is not ("native" or "quic"))
                throw new ConnectionParseException($"Режим UDP TUIC «{relay}» ядро не поддерживает.");
            outbound["udp_relay_mode"] = relay;
        }

        var tls = new JsonObject { ["enabled"] = true };
        if (Get(q, "sni") is { Length: > 0 } sni)
            tls["server_name"] = sni;
        if (IsTrue(Get(q, "allow_insecure")) || IsTrue(Get(q, "insecure")) || IsTrue(Get(q, "allowInsecure")))
            tls["insecure"] = true;
        if (IsTrue(Get(q, "disable_sni")))
            tls["disable_sni"] = true;
        tls["alpn"] = Alpn(Get(q, "alpn")) ?? new JsonArray("h3");
        outbound["tls"] = tls;
        return Result("TUIC", p, "", outbound);
    }

    /// <summary>socks://base64(логин:пароль)@сервер:порт (v2rayN) или socks5://логин:пароль@сервер:порт.</summary>
    private static ParsedServer Socks(LinkParts p)
    {
        var outbound = new JsonObject
        {
            ["type"] = "socks",
            ["server"] = p.Host,
            ["server_port"] = p.Port,
            ["version"] = "5"
        };
        if (!string.IsNullOrEmpty(p.UserInfo))
        {
            var credentials = p.UserInfo.Contains(':') ? p.UserInfo : TryBase64(p.UserInfo) ?? p.UserInfo;
            var colon = credentials.IndexOf(':');
            outbound["username"] = colon >= 0 ? credentials[..colon] : credentials;
            if (colon >= 0)
                outbound["password"] = credentials[(colon + 1)..];
        }

        return Result("SOCKS5", p, "", outbound);
    }

    // ---- TLS и транспорт (общие для vless, vmess и trojan)

    private static (JsonObject? Tls, string Name) Tls(Dictionary<string, string> q, bool defaultOn)
    {
        var security = Get(q, "security") ?? (defaultOn ? "tls" : "none");
        switch (security)
        {
            case "" or "none":
                return (null, "");
            case "tls" or "xtls" or "reality":
                break;
            default:
                throw new ConnectionParseException($"Защита соединения «{security}» ядро не поддерживает.");
        }

        var tls = new JsonObject { ["enabled"] = true };
        if ((Get(q, "sni") ?? Get(q, "peer")) is { Length: > 0 } sni)
            tls["server_name"] = sni;
        if (IsTrue(Get(q, "allowInsecure")) || IsTrue(Get(q, "insecure")))
            tls["insecure"] = true;
        if (Alpn(Get(q, "alpn")) is { } alpn)
            tls["alpn"] = alpn;

        var fingerprint = Get(q, "fp");
        if (security == "reality")
        {
            var publicKey = Get(q, "pbk");
            if (string.IsNullOrEmpty(publicKey))
                throw new ConnectionParseException("В ссылке Reality нет открытого ключа сервера (pbk).");
            // Неверный ключ или идентификатор не даёт запуститься всему ядру, а с ним и остальным приложениям.
            var reality = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = RealityPublicKey(publicKey)
                                 ?? throw new ConnectionParseException("Открытый ключ сервера Reality (pbk) в ссылке повреждён: ключ должен быть длиной 43 символа.")
            };
            if (Get(q, "sid") is { Length: > 0 } shortId)
            {
                if (shortId.Length > 16 || shortId.Length % 2 != 0 || !shortId.All(Uri.IsHexDigit))
                    throw new ConnectionParseException("Короткий идентификатор Reality (sid) в ссылке повреждён: нужно чётное число шестнадцатеричных цифр, не больше 16.");
                reality["short_id"] = shortId.ToLowerInvariant();
            }
            tls["reality"] = reality;
            fingerprint ??= "chrome"; // Reality в ядре работает только с отпечатком браузера
        }

        if (!string.IsNullOrEmpty(fingerprint))
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = UtlsFingerprints.Contains(fingerprint) ? fingerprint.ToLowerInvariant() : "chrome" };
        return (tls, security == "reality" ? "Reality" : "TLS");
    }

    /// <summary>Открытый ключ Reality так, как его ждёт ядро: base64url без «=», 32 байта. null — ключ повреждён.</summary>
    internal static string? RealityPublicKey(string value)
    {
        var key = value.Trim().TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (key.Length != 43)
            return null;
        try
        {
            return Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/') + "=").Length == 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static (JsonObject? Transport, string Name) Transport(Dictionary<string, string> q)
    {
        var type = Get(q, "type") ?? "tcp";
        var host = Get(q, "host");
        var path = Get(q, "path");
        switch (type)
        {
            case "" or "tcp" or "raw":
                var header = Get(q, "headerType");
                if (!string.IsNullOrEmpty(header) && header != "none")
                    throw new ConnectionParseException($"Маскировка TCP «{header}» ядро не поддерживает.");
                return (null, "");

            case "ws":
            {
                var ws = new JsonObject { ["type"] = "ws" };
                path = string.IsNullOrEmpty(path) ? "/" : path;
                // «?ed=2048» в пути — ранние данные: ядро отправляет их в заголовке Sec-WebSocket-Protocol.
                var question = path.IndexOf('?');
                if (question >= 0)
                {
                    var pathQuery = ParseQuery(path[(question + 1)..]);
                    if (Get(pathQuery, "ed") is { } ed && int.TryParse(ed, out var earlyData) && earlyData > 0)
                    {
                        pathQuery.Remove("ed");
                        path = path[..question] + (pathQuery.Count > 0 ? "?" + string.Join("&", pathQuery.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")) : "");
                        ws["max_early_data"] = earlyData;
                        ws["early_data_header_name"] = "Sec-WebSocket-Protocol";
                    }
                }

                ws["path"] = path;
                if (!string.IsNullOrEmpty(host))
                    ws["headers"] = new JsonObject { ["Host"] = host };
                return (ws, "WebSocket");
            }

            case "grpc":
            {
                var grpc = new JsonObject { ["type"] = "grpc" };
                if ((Get(q, "serviceName") ?? Get(q, "service_name")) is { Length: > 0 } service)
                    grpc["service_name"] = service;
                return (grpc, "gRPC");
            }

            case "http" or "h2":
            {
                var http = new JsonObject { ["type"] = "http" };
                if (!string.IsNullOrEmpty(host))
                    http["host"] = new JsonArray(host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(h => (JsonNode?)h).ToArray());
                if (!string.IsNullOrEmpty(path))
                    http["path"] = path;
                return (http, "HTTP/2");
            }

            case "httpupgrade":
            {
                var upgrade = new JsonObject { ["type"] = "httpupgrade" };
                if (!string.IsNullOrEmpty(host))
                    upgrade["host"] = host;
                if (!string.IsNullOrEmpty(path))
                    upgrade["path"] = path;
                return (upgrade, "HTTPUpgrade");
            }

            case "xhttp" or "splithttp":
                throw new ConnectionParseException("Транспорт XHTTP ядро пока не поддерживает. Попросите у провайдера ссылку с другим транспортом (Reality, WebSocket, gRPC).");
            default:
                throw new ConnectionParseException($"Транспорт «{type}» ядро не поддерживает.");
        }
    }

    // ---- разбор ссылки

    private sealed record LinkParts(string Scheme, string UserInfo, string Host, int Port, Dictionary<string, string> Query, string? Name);

    private static string? SchemeOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var end = text.IndexOf("://", StringComparison.Ordinal);
        return end > 0 && text[..end].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.') ? text[..end].ToLowerInvariant() : null;
    }

    /// <summary>схема://пользователь@сервер:порт/путь?параметры#название — вручную: в паролях бывают символы, которые ломают Uri.</summary>
    private static LinkParts Split(string link)
    {
        var scheme = SchemeOf(link)!;
        var rest = link[(scheme.Length + 3)..];
        string? name = null;
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            name = Unescape(rest[(hash + 1)..]).Trim();
            rest = rest[..hash];
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var question = rest.IndexOf('?');
        if (question >= 0)
        {
            query = ParseQuery(rest[(question + 1)..]);
            rest = rest[..question];
        }

        var at = rest.LastIndexOf('@');
        var userInfo = at >= 0 ? Unescape(rest[..at]) : "";
        var hostPort = at >= 0 ? rest[(at + 1)..] : rest;
        var slash = hostPort.IndexOf('/');
        if (slash >= 0)
            hostPort = hostPort[..slash];

        var defaultPort = scheme is "hysteria2" or "hy2" or "tuic" or "trojan" ? 443 : (int?)null;
        var (host, port) = HostPort(hostPort, defaultPort);
        return new LinkParts(scheme, userInfo, host, port, query, string.IsNullOrEmpty(name) ? null : name);
    }

    private static (string Host, int Port) HostPort(string text, int? defaultPort)
    {
        text = text.Trim();
        string host;
        string? port = null;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0)
                throw new ConnectionParseException("Адрес сервера в ссылке повреждён.");
            host = text[1..close];
            if (close + 1 < text.Length && text[close + 1] == ':')
                port = text[(close + 2)..];
        }
        else
        {
            var colon = text.LastIndexOf(':');
            host = colon >= 0 ? text[..colon] : text;
            port = colon >= 0 ? text[(colon + 1)..] : null;
        }

        host = Unescape(host);
        if (host.Length == 0 || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@'))
            throw new ConnectionParseException("В ссылке нет адреса сервера.");
        return (host, ParsePort(port, defaultPort));
    }

    private static int ParsePort(string? text, int? defaultPort)
    {
        if (string.IsNullOrEmpty(text))
            return defaultPort ?? throw new ConnectionParseException("В ссылке нет порта сервера.");
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new ConnectionParseException($"Неверный порт сервера: «{text}».");
        return port;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Unescape(eq >= 0 ? pair[..eq] : pair);
            if (key.Length > 0)
                result[key] = eq >= 0 ? Unescape(pair[(eq + 1)..]) : "";
        }

        return result;
    }

    private static string? Get(Dictionary<string, string> q, string key) => q.TryGetValue(key, out var value) ? value : null;

    private static bool IsTrue(string? value) => value is "1" or "true" or "True" or "TRUE";

    private static JsonArray? Alpn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var items = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.Length == 0 ? null : new JsonArray(items.Select(a => (JsonNode?)a).ToArray());
    }

    private static string RequireUuid(string? value, string protocol)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
            throw new ConnectionParseException($"В ссылке {protocol} нет идентификатора пользователя (UUID).");
        // Ядро принимает и UUID, и произвольную строку (из неё оно делает UUID так же, как Xray).
        if (value.Length > 64 || value.Any(char.IsWhiteSpace))
            throw new ConnectionParseException($"Идентификатор пользователя в ссылке {protocol} повреждён.");
        return value;
    }

    private static string Details(string transport, string security) =>
        string.Join(" · ", new[] { security, transport }.Where(s => s.Length > 0));

    private static ParsedServer Result(string protocol, LinkParts p, string details, JsonObject outbound) =>
        new(protocol, p.Name ?? $"{p.Host}:{p.Port}", p.Host, p.Port, details, outbound);

    private static string Unescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    /// <summary>base64 обычный и для адресов, с отступами и без. null — не base64 или не UTF-8 текст.</summary>
    internal static string? TryBase64(string text)
    {
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/').TrimEnd('=');
        if (clean.Length == 0 || clean.Length % 4 == 1)
            return null;
        clean = clean.PadRight(clean.Length + (4 - clean.Length % 4) % 4, '=');
        try
        {
            var bytes = Convert.FromBase64String(clean);
            var decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return decoded.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) ? null : decoded;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }
}
