using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SafeLauncher.Service;

/// <summary>Лаунчер прислал недопустимые настройки ядра. Сообщение — для пользователя.</summary>
public sealed class CoreConfigException : Exception
{
    public CoreConfigException(string message) : base(message)
    {
    }
}

/// <summary>Управление ядром на 127.0.0.1: через него лаунчер проверяет серверы и видит соединения. Без ключа не пускает.</summary>
/// <param name="CheckPort">
/// Прокси на 127.0.0.1 для проверки подключений через работающее ядро (имя пользователя — tag подключения, пароль — ключ
/// управления) или null — без него.
/// </param>
public sealed record CoreApi(int Port, string Secret, int? CheckPort = null);

/// <summary>
/// Собирает настройки ядра из запроса лаунчера. Служба работает от имени системы, поэтому всё, что приходит
/// от лаунчера, проверяется: подключения только известных типов и без путей к файлам, адаптер, маршруты
/// и журнал служба задаёт сама. Всё, что не помечено лаунчером, идёт напрямую.
/// </summary>
public static class CoreConfigBuilder
{
    public const string InterfaceName = "SafeLauncher";
    public const string DirectTag = "direct";
    public const string TagPrefix = "launcher:";
    public const string LocalDnsTag = "local";
    public const string CheckInboundTag = "check-in";

    /// <summary>DNS-сервер, к которому приложения обращаются через своё подключение (по TCP, внутри туннеля).</summary>
    public const string RemoteDnsServer = "1.1.1.1";

    public const string FakeIpDnsTag = "fakeip";

    /// <summary>Подменные адреса (IPv4) — диапазон для тестов сетей, в интернете не встречается.</summary>
    public const string FakeIpRange4 = "198.18.0.0/15";

    /// <summary>Подменные адреса IPv6 — из частного диапазона.</summary>
    public const string FakeIpRange6 = "fc00::/18";

    /// <summary>
    /// Что подменными адресами не отвечаем: проверка интернета Windows (иначе значок сети покажет «нет интернета»),
    /// имена локальной сети (.local, однословные) и служебные зоны.
    /// </summary>
    public static readonly IReadOnlyList<string> FakeIpExcluded = new[]
    {
        "msftncsi.com", "msftconnecttest.com", "local", "localhost", "arpa"
    };

    private const int MaxOutbounds = 64;
    private const int MaxApps = 256;
    private const int MaxDomainsPerApp = 1000;
    private const int MaxProgramsPerApp = 32;
    private const int MaxPathLength = 1024;

    /// <summary>Типы подключений, которые служба согласна запустить. WireGuard в ядре — endpoint, а не outbound.</summary>
    private static readonly HashSet<string> OutboundTypes = new(StringComparer.Ordinal)
    {
        "vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic", "socks", "http"
    };

    private static readonly HashSet<string> EndpointTypes = new(StringComparer.Ordinal) { "wireguard" };

    private static readonly HashSet<string> ReservedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        DirectTag, "tun-in", CheckInboundTag, "dns", "block"
    };

    private static readonly Regex TagPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    private static readonly Regex DomainPattern = new(
        @"^(?=.{1,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?\.)+[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?$",
        RegexOptions.CultureInvariant);

    /// <param name="api">Адрес и ключ управления ядром для лаунчера (проверка серверов, список соединений); null — без него.</param>
    /// <param name="servicePrograms">
    /// Программы служб Windows (пути): их процессы не трогаем, даже если они подходят под программы приложения
    /// (<see cref="CoreAppRoute.Programs"/>). null — не проверять.
    /// </param>
    public static JsonObject Build(CoreRequest request, CoreApi? api = null, IReadOnlyCollection<string>? servicePrograms = null)
    {
        var outbounds = request.Outbounds ?? new List<JsonObject>();
        var apps = request.Apps ?? new List<CoreAppRoute>();
        if (outbounds.Count > MaxOutbounds)
            throw new CoreConfigException($"Слишком много подключений: {outbounds.Count}.");
        if (apps.Count > MaxApps)
            throw new CoreConfigException($"Слишком много приложений: {apps.Count}.");

        var tags = new HashSet<string>(StringComparer.Ordinal);
        var outboundList = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = DirectTag } };
        var endpointList = new JsonArray();
        foreach (var source in outbounds)
        {
            if (source is null)
                throw new CoreConfigException("Пустое подключение.");
            var outbound = (JsonObject)source.DeepClone();
            var tag = outbound["tag"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string)outbound["tag"]! : null;
            var type = outbound["type"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string)outbound["type"]! : null;
            if (tag is null || !TagPattern.IsMatch(tag) || ReservedTags.Contains(tag))
                throw new CoreConfigException($"Недопустимое имя подключения: «{tag}».");
            if (!tags.Add(tag))
                throw new CoreConfigException($"Подключение «{tag}» указано дважды.");
            if (type is null || !OutboundTypes.Contains(type) && !EndpointTypes.Contains(type))
                throw new CoreConfigException($"Тип подключения «{type}» не поддерживается.");
            RejectFilePaths(outbound, tag);
            (EndpointTypes.Contains(type) ? endpointList : outboundList).Add(outbound);
        }

        // Цепочки (detour) — только на подключения из этого же запроса.
        foreach (var node in outboundList.Concat(endpointList).Skip(1))
        {
            if (node!["detour"] is { } detour && (detour.GetValueKind() != System.Text.Json.JsonValueKind.String || !tags.Contains((string)detour!)))
                throw new CoreConfigException($"Подключение «{node["tag"]}» ссылается на неизвестное подключение.");
        }

        if (request.DnsOutbound is not null && !tags.Contains(request.DnsOutbound))
            throw new CoreConfigException($"Не найдено подключение «{request.DnsOutbound}» для DNS-запросов.");

        var rules = new JsonArray();
        JsonObject? checkInbound = null;
        if (api?.CheckPort is { } checkPort && tags.Count > 0)
        {
            // Проверка подключения через работающее ядро: вторая копия ядра с тем же ключом WireGuard отбила бы у этой
            // связь с сервером. Прокси пускает только с ключом управления, имя пользователя выбирает подключение.
            var checkTags = outboundList.Skip(1).Concat(endpointList).Select(o => (string)o!["tag"]!).ToList();
            var secret = api.Secret;
            checkInbound = new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = CheckInboundTag,
                ["listen"] = "127.0.0.1",
                ["listen_port"] = checkPort,
                ["users"] = new JsonArray(checkTags.Select(t => (JsonNode?)new JsonObject { ["username"] = t, ["password"] = secret }).ToArray())
            };
            foreach (var tag in checkTags)
            {
                rules.Add(new JsonObject
                {
                    ["inbound"] = new JsonArray(CheckInboundTag),
                    ["auth_user"] = new JsonArray(tag),
                    ["action"] = "route",
                    ["outbound"] = tag
                });
            }

            rules.Add(new JsonObject { ["inbound"] = new JsonArray(CheckInboundTag), ["action"] = "reject" });
        }

        var dnsServers = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = LocalDnsTag });
        var dnsRules = new JsonArray();
        var dnsByOutbound = new Dictionary<string, string>(StringComparer.Ordinal);
        // Правило распознавания имён сайтов встаёт сюда, когда известны программы приложений (ниже).
        var sniffAt = rules.Count;
        var sniffed = new List<string> { "^" + TagPrefix };

        if (request.HideDns)
        {
            // Запросы любых программ к любому DNS-серверу (порт 53): распознать, чтобы на них ответило ядро. Иначе запрос
            // службы DNS Windows к серверу провайдера, через адаптер ядра, ушёл бы ему как есть.
            rules.Add(new JsonObject
            {
                ["port"] = new JsonArray(53),
                ["action"] = "sniff",
                ["sniffer"] = new JsonArray("dns")
            });
        }

        // DNS-запросы, которые ядро распознало (у приложений лаунчера, а со скрытием DNS — у всех) или которые пришли
        // на DNS-адрес адаптера ядра, отвечает само ядро по правилам DNS ниже, а не отправляет как обычный трафик.
        rules.Add(new JsonObject
        {
            ["protocol"] = new JsonArray("dns"),
            ["action"] = "hijack-dns"
        });

        var appTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            if (app is null || !TagPattern.IsMatch(app.Tag ?? string.Empty))
                throw new CoreConfigException($"Недопустимая метка приложения: «{app?.Tag}».");
            if (!appTags.Add(app.Tag!))
                throw new CoreConfigException($"Метка приложения «{app.Tag}» указана дважды.");
            if (!tags.Contains(app.Outbound ?? string.Empty))
                throw new CoreConfigException($"У приложения «{app.Tag}» не найдено подключение «{app.Outbound}».");

            var domains = app.DirectDomains ?? new List<string>();
            if (domains.Count > MaxDomainsPerApp)
                throw new CoreConfigException($"Слишком много исключений у приложения «{app.Tag}».");
            foreach (var domain in domains)
            {
                if (domain is null || !DomainPattern.IsMatch(domain))
                    throw new CoreConfigException($"Недопустимый домен в исключениях: «{domain}».");
            }

            var match = "^" + Regex.Escape(TagPrefix + app.Tag) + @"\|";

            // Процессы программ приложения без метки (копия, запущенная мимо лаунчера, или процесс, о котором лаунчер
            // не успел ответить ядру) идут так же, как приложение, или блокируются; без программ — напрямую.
            var outside = OutsidePatterns(app, servicePrograms);
            var same = app.BlockOutside ? new List<string>() : outside;
            sniffed.AddRange(same.Where(p => !sniffed.Contains(p)));
            JsonArray Matches() => new(new[] { match }.Concat(same).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());

            // Свои DNS-запросы приложения идут через его подключение: к DNS из файла подключения (корпоративный
            // WireGuard знает внутренние имена) или к 1.1.1.1. Провайдер их не видит.
            var dnsTag = DnsServer(app.Outbound!);

            if (domains.Count > 0)
            {
                dnsRules.Add(new JsonObject
                {
                    ["process_path_regex"] = Matches(),
                    ["domain_suffix"] = new JsonArray(domains.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
                    ["action"] = "route",
                    ["server"] = LocalDnsTag
                });
            }

            dnsRules.Add(new JsonObject
            {
                ["process_path_regex"] = Matches(),
                ["action"] = "route",
                ["server"] = dnsTag
            });

            if (domains.Count > 0)
            {
                rules.Add(new JsonObject
                {
                    ["process_path_regex"] = Matches(),
                    ["domain_suffix"] = new JsonArray(domains.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
                    ["action"] = "route",
                    ["outbound"] = DirectTag
                });
            }

            rules.Add(new JsonObject
            {
                ["process_path_regex"] = Matches(),
                ["action"] = "route",
                ["outbound"] = app.Outbound
            });

            if (app.BlockOutside && outside.Count > 0)
            {
                rules.Add(new JsonObject
                {
                    ["process_path_regex"] = new JsonArray(outside.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                    ["action"] = "reject"
                });
            }
        }

        if (apps.Count > 0)
        {
            // Имя сайта из TLS, HTTP и QUIC нужно для исключений по доменам. И соединение идёт к сайту по имени,
            // а не по адресу, который вернул системный DNS: адрес ищет сервер подключения, подменённый ответ
            // провайдера ничего не испортит. Чужой трафик не трогаем.
            rules.Insert(sniffAt, new JsonObject
            {
                ["process_path_regex"] = new JsonArray(sniffed.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["action"] = "sniff",
                ["override_destination"] = true
            });
        }

        if (request.HideDns)
        {
            // Запросы службы DNS Windows (она ищет имена за все программы, и приложение по ней не узнать) получают
            // подменный адрес. Настоящий адрес ищется, только когда к нему подключаются: для приложений лаунчера —
            // сервером подключения, для остальных программ — как обычно. Так провайдер не видит имён сайтов приложений.
            // Только для запросов, пришедших как DNS (protocol dns): поиск адресов самим ядром идёт по-настоящему.
            var fakeIp = new JsonObject
            {
                ["type"] = "fakeip",
                ["tag"] = FakeIpDnsTag,
                ["inet4_range"] = FakeIpRange4
            };
            if (request.Ipv6)
                fakeIp["inet6_range"] = FakeIpRange6;
            dnsServers.Add(fakeIp);
            dnsRules.Add(new JsonObject
            {
                ["domain_suffix"] = new JsonArray(FakeIpExcluded.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
                ["domain_regex"] = new JsonArray(@"^[^.]+$"),
                ["action"] = "route",
                ["server"] = LocalDnsTag
            });
            // Записи HTTPS/SVCB (браузеры спрашивают их вместе с адресом) подменить нельзя, а настоящий ответ выдал бы имя
            // провайдеру: отвечаем «записей нет», браузер обойдётся адресом.
            dnsRules.Add(new JsonObject
            {
                ["protocol"] = new JsonArray("dns"),
                ["query_type"] = new JsonArray("HTTPS", "SVCB"),
                ["action"] = "predefined",
                ["rcode"] = "NOERROR"
            });
            dnsRules.Add(new JsonObject
            {
                ["protocol"] = new JsonArray("dns"),
                ["query_type"] = new JsonArray("A", "AAAA"),
                ["action"] = "route",
                ["server"] = FakeIpDnsTag,
                // Короткая жизнь ответа: после выключения ядра программы быстро забудут подменные адреса.
                ["rewrite_ttl"] = 1
            });
            if (request.DnsOutbound is { } dnsOutbound)
            {
                // Остальные запросы (редкие типы записей: TXT, SRV, MX…) — к DNS-серверу подключения, которое выбрал
                // лаунчер, через него, а не к DNS компьютера: так провайдер не видит и их.
                dnsRules.Add(new JsonObject
                {
                    ["protocol"] = new JsonArray("dns"),
                    ["action"] = "route",
                    ["server"] = DnsServer(dnsOutbound)
                });
            }
        }

        var addresses = new JsonArray("172.31.250.1/30");
        if (request.Ipv6)
            addresses.Add("fdfe:dcba:9877::1/126");

        var config = new JsonObject
        {
            // Время строк ставит служба при приеме (UTC, мс, общее с журналами лаунчера и службы), см. ServiceLog.
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = false },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = InterfaceName,
                ["address"] = addresses,
                ["auto_route"] = true,
                ["strict_route"] = true,
                ["stack"] = "mixed"
            }),
            ["dns"] = new JsonObject
            {
                ["servers"] = dnsServers,
                ["rules"] = dnsRules,
                ["final"] = LocalDnsTag
            },
            ["outbounds"] = outboundList,
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                // Адреса серверов подключений и сайтов-исключений ищет системный DNS, как и без лаунчера.
                ["default_domain_resolver"] = LocalDnsTag,
                ["find_process"] = true,
                ["rules"] = rules,
                ["final"] = DirectTag
            }
        };
        if (endpointList.Count > 0)
            config["endpoints"] = endpointList;
        if (checkInbound is not null)
            config["inbounds"]!.AsArray().Add(checkInbound);
        if (api is not null)
        {
            config["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{api.Port}",
                    ["secret"] = api.Secret
                }
            };
        }

        return config;

        // DNS-сервер внутри подключения, один на подключение: из файла подключения (по UDP) или 1.1.1.1 по TCP.
        string DnsServer(string outbound)
        {
            if (dnsByOutbound.TryGetValue(outbound, out var tag))
                return tag;
            tag = "dns-" + outbound;
            dnsByOutbound[outbound] = tag;
            var own = OwnDnsServer(request, outbound);
            dnsServers.Add(new JsonObject
            {
                ["type"] = own is null ? "tcp" : "udp",
                ["tag"] = tag,
                ["server"] = own ?? RemoteDnsServer,
                ["detour"] = outbound
            });
            return tag;
        }
    }

    /// <summary>
    /// Шаблоны ядра (регулярные выражения Go, без учёта регистра) для путей процессов программ приложения без метки.
    /// Программы Windows, её защитника и служб пропускаются: ядро ведёт трафик всего компьютера, а такие процессы —
    /// не копии приложения, и закрывать или уводить в VPN их нельзя.
    /// </summary>
    private static List<string> OutsidePatterns(CoreAppRoute app, IReadOnlyCollection<string>? servicePrograms)
    {
        var result = new List<string>();
        var programs = app.Programs ?? new List<CoreProgram>();
        if (programs.Count > MaxProgramsPerApp)
            throw new CoreConfigException($"Слишком много программ у приложения «{app.Tag}»: {programs.Count}.");
        foreach (var program in programs)
        {
            var folder = program?.Folder?.TrimEnd('\\');
            if (program is null || !IsFolder(folder) || !IsExeName(program.Name))
                throw new CoreConfigException($"Недопустимая программа приложения «{app.Tag}»: «{program?.Folder}», «{program?.Name}».");
            // Весь диск — слишком широко: так оказалась бы «копией» любая программа с тем же именем.
            if (program.Subfolders && folder!.Length == 2 || IsSystemProgram(folder!, program.Name))
                continue;

            var pattern = "(?i)^" + GoQuote(folder + "\\") + (program.Subfolders ? @"(?:[^\\]+\\)*" : string.Empty) + GoQuote(program.Name) + "$";
            if (result.Contains(pattern) ||
                servicePrograms is not null && servicePrograms.Any(s => Regex.IsMatch(s, pattern, RegexOptions.CultureInvariant)))
                continue;
            result.Add(pattern);
        }

        return result;
    }

    /// <summary>Полный путь к папке на диске: «C:» или «C:\Папка\…», без «.», «..» и недопустимых знаков.</summary>
    private static bool IsFolder(string? folder)
    {
        if (folder is null || folder.Length < 2 || folder.Length > MaxPathLength || !char.IsAsciiLetter(folder[0]) || folder[1] != ':')
            return false;
        if (folder.Length == 2)
            return true;
        if (folder[2] != '\\')
            return false;
        return folder[3..].Split('\\').All(IsFileName);
    }

    private static bool IsExeName(string? name) =>
        IsFileName(name) && name!.Length > 4 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileName(string? name) =>
        name is { Length: > 0 and <= 255 } && name != "." && name != ".." &&
        name.All(c => c >= ' ' && c is not ('<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'));

    /// <summary>Программа Windows или её защитника: в папке Windows, в общих папках (как у фильтров) или системная по имени.</summary>
    private static bool IsSystemProgram(string folder, string name)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } w ? w : @"C:\Windows";
        return NetworkGuard.ProtectedNames.Contains(name) || Under(windows) || NetworkGuard.SharedFolders.Any(Under);

        bool Under(string parent) =>
            (folder + "\\").StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Как regexp.QuoteMeta в Go: знаки синтаксиса выражений — с «\».</summary>
    private static string GoQuote(string text)
    {
        var result = new System.Text.StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (@"\.+*?()|[]{}^$".Contains(c))
                result.Append('\\');
            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>DNS-сервер внутри подключения, если лаунчер его передал (только IP-адрес, без порта и имён), иначе null.</summary>
    private static string? OwnDnsServer(CoreRequest request, string outbound)
    {
        if (request.OutboundDns is null || !request.OutboundDns.TryGetValue(outbound, out var value) || value is null)
            return null;
        if (!System.Net.IPAddress.TryParse(value, out var address) || address.ToString() != value.Trim())
            throw new CoreConfigException($"Недопустимый DNS-сервер подключения «{outbound}»: «{value}».");
        return address.ToString();
    }

    /// <summary>Пути к файлам в подключении: ядро работает от имени системы и не должно читать файлы по указке лаунчера.</summary>
    private static void RejectFilePaths(JsonNode? node, string tag)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key.EndsWith("_path", StringComparison.OrdinalIgnoreCase))
                        throw new CoreConfigException($"В подключении «{tag}» указан файл ({key}): так нельзя.");
                    RejectFilePaths(value, tag);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                    RejectFilePaths(item, tag);
                break;
        }
    }
}
