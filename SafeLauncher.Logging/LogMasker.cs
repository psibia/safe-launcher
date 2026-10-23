using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SafeLauncher.Logging;

/// <summary>
/// Чистит текст перед записью в журнал.
/// Всегда: секреты (ключи, пароли, UUID, ссылки подписок и подключений, query-строки адресов), путь профиля пользователя.
/// Если включено <see cref="HideAddresses"/>: серверы подключений → <c>{srv-тег}</c>, остальные IP → <c>{ip4-pub-xxxxxx}</c>
/// (одинаковый адрес дает одинаковую метку: HMAC с ключом, который не попадает в журналы и выгрузку).
/// Потокобезопасен: настройки заменяются целиком.
/// </summary>
public sealed class LogMasker
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // ключ=значение, "ключ": "значение", ключ: значение
    private static readonly Regex SecretPair = new(
        @"(?i)(?<![A-Za-z0-9_])(""?)(private_key|privatekey|pre_shared_key|presharedkey|preshared_key|password|passwd|uuid|secret|token|psk|auth|auth_str|obfs_password|short_id|public_key|publickey)\1(\s*[:=]\s*)(""?)([^\s"",;&}\]]+)",
        Opts);

    private static readonly Regex Bearer = new(@"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}", Opts);

    // ключи WireGuard и прочий base64 на 32 байта
    private static readonly Regex Base64Key = new(@"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{42,43}={1,2}(?![A-Za-z0-9+/=])", Opts);

    private static readonly Regex Uuid = new(@"(?i)(?<![0-9a-f])[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![0-9a-f])", Opts);

    // ссылки подключений целиком: в них и адрес, и ключи
    private static readonly Regex ShareLink = new(
        @"(?i)\b(vless|vmess|trojan|ss|ssr|hy2|hysteria2?|tuic|wireguard|wg|socks5?|anytls|naive\+https?)://[^\s""'<>]+", Opts);

    private static readonly Regex QueryString = new(@"(?i)\b(https?://[^\s?""'<>#]+)\?[^\s""'<>]+", Opts);

    private static readonly Regex UserProfile = new(
        @"(?i)\b[a-z]:(\\{1,2}|/)users(\\{1,2}|/)(?!public(?:\\|/)|default(?:\\|/)|all users(?:\\|/))[^\\/:*?""<>|\r\n\t]+?(?=\\|/)", Opts);

    private static readonly Regex IpCandidate = new(
        @"(?<![0-9A-Za-z:.\-])(?:(?<v6>(?:[0-9A-Fa-f]{0,4}:){2,7}(?:[0-9A-Fa-f]{1,4}|(?:\d{1,3}\.){3}\d{1,3})?)|(?<v4>(?:\d{1,3}\.){3}\d{1,3}))(?![0-9A-Za-z\-]|\.\d)",
        Opts);

    private static readonly Regex Domain = new(
        @"(?<![A-Za-z0-9\-.{])((?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?\.)+)([a-z]{2,24}|xn--[a-z0-9\-]{2,59})(?![A-Za-z0-9\-(])",
        Opts);

    /// <summary>Расширения файлов и части имен .NET, которые похожи на домены, но ими не являются.</summary>
    private static readonly HashSet<string> NotTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "log", "json", "txt", "cs", "sys", "ps1", "cmd", "bat", "zip", "lnk", "ini", "xml", "msi", "png", "ico",
        "tmp", "dat", "db", "md", "iss", "pdb", "config", "conf", "yaml", "yml", "csv", "jpg", "jpeg", "gif", "svg", "html", "htm",
        "js", "css", "url", "key", "lock", "bak", "old", "new", "etl", "evtx", "msix", "appx", "xaml", "nupkg", "sh", "py", "go"
    };

    /// <summary>Публичные DNS и служебные имена, которые не скрываются в выгрузке (не секрет, важны для разбора).</summary>
    private static readonly HashSet<string> KeepDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "dns.google", "one.one.one.one", "cloudflare-dns.com", "dns.quad9.net", "localhost"
    };

    private static readonly (IPAddress Net, int Bits)[] KeepNets =
    {
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("0.0.0.0"), 32),
        (IPAddress.Parse("172.31.250.0"), 30),   // адаптер ядра (CoreConfigBuilder)
        (IPAddress.Parse("198.18.0.0"), 15),     // подменные адреса (fakeip)
        (IPAddress.Parse("224.0.0.0"), 4),       // multicast
        (IPAddress.Parse("255.255.255.255"), 32),
        (IPAddress.Parse("1.1.1.1"), 32),
        (IPAddress.Parse("1.0.0.1"), 32),
        (IPAddress.Parse("8.8.8.8"), 32),
        (IPAddress.Parse("8.8.4.4"), 32),
        (IPAddress.Parse("9.9.9.9"), 32),
        (IPAddress.Parse("::1"), 128),
        (IPAddress.Parse("::"), 128),
        (IPAddress.Parse("fdfe:dcba:9877::"), 126), // адаптер ядра, IPv6
        (IPAddress.Parse("fc00::"), 18),            // fakeip IPv6
        (IPAddress.Parse("ff00::"), 8),             // multicast
        (IPAddress.Parse("2606:4700:4700::1111"), 128),
        (IPAddress.Parse("2606:4700:4700::1001"), 128),
        (IPAddress.Parse("2001:4860:4860::8888"), 128),
        (IPAddress.Parse("2001:4860:4860::8844"), 128)
    };

    private static readonly (IPAddress Net, int Bits)[] LanNets =
    {
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("100.64.0.0"), 10),
        (IPAddress.Parse("169.254.0.0"), 16),
        (IPAddress.Parse("fc00::"), 7),
        (IPAddress.Parse("fe80::"), 10)
    };

    private sealed class State
    {
        public byte[] Key = Array.Empty<byte>();
        public bool Hide;
        public Dictionary<string, string> ServerIps = new(StringComparer.Ordinal);
        public (string Host, string Label)[] ServerHosts = Array.Empty<(string, string)>();
        public Regex? HostRegex;
        public string[] Secrets = Array.Empty<string>();
    }

    private volatile State _state = new();

    public LogMasker()
    {
        SetKey(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>Скрывать IP-адреса и серверы подключений (настройка «Скрывать IP-адреса», по умолчанию выключена).</summary>
    public bool HideAddresses
    {
        get => _state.Hide;
        set => Update(s => s.Hide = value);
    }

    /// <summary>Ключ меток адресов. Общий у лаунчера и службы, чтобы метки совпадали.</summary>
    public void SetKey(byte[] key)
    {
        if (key is not { Length: > 0 })
            return;
        var copy = (byte[])key.Clone();
        Update(s => s.Key = copy);
    }

    /// <summary>
    /// Серверы подключений: адрес или имя → тег подключения (c + 32 hex). Заменяются на <c>{srv-8 hex тега}</c>.
    /// </summary>
    public void SetServers(IEnumerable<KeyValuePair<string, string>> servers)
    {
        var ips = new Dictionary<string, string>(StringComparer.Ordinal);
        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawHost, tag) in servers)
        {
            var host = (rawHost ?? string.Empty).Trim().Trim('[', ']');
            if (host.Length == 0 || string.IsNullOrEmpty(tag))
                continue;
            var label = ServerLabel(tag);
            if (IPAddress.TryParse(host, out var ip))
                ips[Canonical(ip)] = label;
            else if (host.Contains('.'))
            {
                hosts[host.TrimEnd('.')] = label;
                try
                {
                    var ascii = new System.Globalization.IdnMapping().GetAscii(host.TrimEnd('.'));
                    hosts[ascii] = label;
                }
                catch (ArgumentException)
                {
                    // не IDN
                }
            }
        }

        var ordered = hosts.OrderByDescending(h => h.Key.Length).Select(h => (h.Key, h.Value)).ToArray();
        Regex? regex = null;
        if (ordered.Length > 0)
        {
            var alternatives = string.Join("|", ordered.Select(h => Regex.Escape(h.Key)));
            regex = new Regex(@"(?i)(?<![A-Za-z0-9\-.])(" + alternatives + @")(?![A-Za-z0-9\-]|\.[A-Za-z0-9])", RegexOptions.CultureInvariant);
        }

        Update(s =>
        {
            s.ServerIps = ips;
            s.ServerHosts = ordered;
            s.HostRegex = regex;
        });
    }

    /// <summary>Строки, которые нельзя писать никогда (ссылки подписок, секрет управления ядром).</summary>
    public void SetSecrets(IEnumerable<string> secrets)
    {
        var list = secrets.Where(s => !string.IsNullOrWhiteSpace(s) && s.Length >= 6).Distinct().OrderByDescending(s => s.Length).ToArray();
        Update(s => s.Secrets = list);
    }

    /// <summary>Чистка по текущим настройкам: перед записью в журнал.</summary>
    public string Mask(string? text)
    {
        var state = _state;
        var result = MaskSecrets(text, state);
        return state.Hide ? MaskAddresses(result, state) : result;
    }

    /// <summary>Скрыть адреса независимо от настройки (галочка «Скрыть IP-адреса» в выгрузке).</summary>
    public string MaskAddressesNow(string? text)
    {
        var state = _state;
        return MaskAddresses(MaskSecrets(text, state), state);
    }

    /// <summary>Только секреты и профиль пользователя (без адресов).</summary>
    public string MaskSecretsOnly(string? text) => MaskSecrets(text, _state);

    /// <summary>
    /// Домены → <c>{site-xxxxxx}.зона</c> (галочка «Скрыть имена сайтов» в выгрузке). Зона первого уровня остается.
    /// Метки <c>{…}</c>, имена файлов и .NET-типы не трогаются.
    /// </summary>
    public string MaskDomains(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var state = _state;
        return Domain.Replace(text, m =>
        {
            var tld = m.Groups[2].Value;
            if (NotTlds.Contains(tld))
                return m.Value;
            var full = m.Value.TrimEnd('.');
            if (KeepDomains.Contains(full))
                return m.Value;
            var body = m.Groups[1].Value.TrimEnd('.');
            // имена .NET и методов (System.Net.Http) обычно с большой буквы; домены в журналах в нижнем регистре
            if (body.Any(char.IsUpper))
                return m.Value;
            return "{site-" + Hash(state.Key, "site:" + full.ToLowerInvariant(), 3) + "}." + tld;
        });
    }

    /// <summary>
    /// Метка названия для выгрузки (<see cref="NameMasker"/>): <c>{вид-xxxxxx}</c>, одинаковая для одного названия без учета регистра.
    /// </summary>
    public string NameLabel(string kind, string value) =>
        "{" + kind + "-" + Hash(_state.Key, kind + ":" + value.Trim().ToLowerInvariant(), 3) + "}";

    /// <summary>Метка приложения или подключения по его тегу (a… или c… + 32 hex): <c>{app-8 hex}</c>, как у серверов.</summary>
    public static string TagLabel(string kind, string tag) =>
        "{" + kind + "-" + (tag.Length > 8 ? tag.Substring(1, 8) : tag) + "}";

    public static string ServerLabel(string tag)
    {
        var hex = tag.StartsWith('c') && tag.Length > 8 ? tag.Substring(1, 8) : tag.Length > 8 ? tag[..8] : tag;
        return "{srv-" + hex + "}";
    }

    /// <summary>Метка адреса или сам адрес, если он не скрывается (локальный, служебный, публичный DNS).</summary>
    public string LabelFor(IPAddress ip) => Label(ip, _state);

    public static bool IsKept(IPAddress ip) => InAny(Normalize(ip), KeepNets);

    public static bool IsLan(IPAddress ip) => InAny(Normalize(ip), LanNets);

    private void Update(Action<State> change)
    {
        lock (this)
        {
            var old = _state;
            var next = new State
            {
                Key = old.Key,
                Hide = old.Hide,
                ServerIps = old.ServerIps,
                ServerHosts = old.ServerHosts,
                HostRegex = old.HostRegex,
                Secrets = old.Secrets
            };
            change(next);
            _state = next;
        }
    }

    private static string MaskSecrets(string? text, State state)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var result = text;
        foreach (var secret in state.Secrets)
        {
            if (result.Contains(secret, StringComparison.Ordinal))
                result = result.Replace(secret, "[скрыто]", StringComparison.Ordinal);
        }

        result = ShareLink.Replace(result, m => "[ссылка " + m.Groups[1].Value.ToLowerInvariant() + "]");
        result = QueryString.Replace(result, "$1?[…]");
        result = SecretPair.Replace(result, "$1$2$1$3$4***");
        result = Bearer.Replace(result, "$1 ***");
        result = Base64Key.Replace(result, "[ключ]");
        result = Uuid.Replace(result, "[uuid]");
        result = UserProfile.Replace(result, "%USERPROFILE%");
        return result;
    }

    private static string MaskAddresses(string text, State state)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var result = text;
        if (state.HostRegex is not null)
            result = state.HostRegex.Replace(result, m => FindHostLabel(state, m.Value) ?? m.Value);

        return IpCandidate.Replace(result, m => ReplaceIp(m, state));
    }

    private static string? FindHostLabel(State state, string host)
    {
        foreach (var (h, label) in state.ServerHosts)
        {
            if (string.Equals(h, host, StringComparison.OrdinalIgnoreCase))
                return label;
        }
        return null;
    }

    private static string ReplaceIp(Match m, State state)
    {
        var text = m.Value;
        if (m.Groups["v6"].Success)
        {
            if (text.Count(c => c == ':') >= 2 && IPAddress.TryParse(text, out var ip6) && ip6.AddressFamily == AddressFamily.InterNetworkV6)
                return Label(ip6, state);

            // не IPv6 (например, «время:минуты:») — но внутри может быть IPv4
            return IpCandidate.Replace(text, inner => inner.Groups["v4"].Success ? ReplaceIp(inner, state) : inner.Value);
        }

        if (IsIpv4(text, out var ip4))
            return Label(ip4, state);
        return text;
    }

    private static bool IsIpv4(string text, out IPAddress ip)
    {
        ip = IPAddress.None;
        var parts = text.Split('.');
        if (parts.Length != 4)
            return false;
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if (parts[i].Length is 0 or > 3 || !int.TryParse(parts[i], out var v) || v > 255)
                return false;
            // 01.02.03.04 — скорее номер версии, чем адрес
            if (parts[i].Length > 1 && parts[i][0] == '0')
                return false;
            bytes[i] = (byte)v;
        }
        ip = new IPAddress(bytes);
        return true;
    }

    private static string Label(IPAddress ip, State state)
    {
        var normal = Normalize(ip);
        var canonical = Canonical(normal);
        if (state.ServerIps.TryGetValue(canonical, out var server))
            return server;
        if (InAny(normal, KeepNets))
            return canonical;
        var v = normal.AddressFamily == AddressFamily.InterNetwork ? "ip4" : "ip6";
        var kind = InAny(normal, LanNets) ? "lan" : "pub";
        return "{" + v + "-" + kind + "-" + Hash(state.Key, canonical, 3) + "}";
    }

    private static IPAddress Normalize(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            return ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0)
            return new IPAddress(ip.GetAddressBytes());
        return ip;
    }

    private static string Canonical(IPAddress ip) => Normalize(ip).ToString().ToLowerInvariant();

    private static bool InAny(IPAddress ip, (IPAddress Net, int Bits)[] nets)
    {
        var bytes = ip.GetAddressBytes();
        foreach (var (net, bits) in nets)
        {
            var nb = net.GetAddressBytes();
            if (nb.Length != bytes.Length)
                continue;
            if (PrefixMatch(bytes, nb, bits))
                return true;
        }
        return false;
    }

    private static bool PrefixMatch(byte[] a, byte[] b, int bits)
    {
        var full = bits / 8;
        for (var i = 0; i < full; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        var rest = bits % 8;
        if (rest == 0)
            return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[full] & mask) == (b[full] & mask);
    }

    private static string Hash(byte[] key, string value, int bytes)
    {
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(mac, 0, bytes).ToLowerInvariant();
    }
}
