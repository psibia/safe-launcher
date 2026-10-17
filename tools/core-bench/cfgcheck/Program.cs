// Настройки ядра от CoreConfigBuilder службы: все типы подключений, приложения с программами (через подключение и
// с блокировкой), скрытие DNS, редкие DNS-запросы через основное, прокси проверки. Шаг service-config стенда проверяет их
// командой «sing-box check»: так видно, принимает ли новая версия sing-box то, что строит служба.
// Запуск: CfgCheck <папка>  — пишет туда *.json. Ключи и адреса ненастоящие (адреса из диапазонов для документации).
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using SafeLauncher.Service;

const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
const string Uuid = "bf000d23-0752-40b4-affe-68f7707a9661";

JsonObject Tls(string server) => new() { ["enabled"] = true, ["server_name"] = server };

var outbounds = new List<JsonObject>
{
    new()
    {
        ["type"] = "vless", ["tag"] = "cVless", ["server"] = "203.0.113.1", ["server_port"] = 443, ["uuid"] = Uuid, ["flow"] = "xtls-rprx-vision",
        ["tls"] = new JsonObject
        {
            ["enabled"] = true, ["server_name"] = "example.com",
            ["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = "chrome" },
            ["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", ["short_id"] = "0123abcd" }
        }
    },
    new() { ["type"] = "vmess", ["tag"] = "cVmess", ["server"] = "203.0.113.2", ["server_port"] = 443, ["uuid"] = Uuid, ["security"] = "auto", ["tls"] = Tls("example.com") },
    new() { ["type"] = "trojan", ["tag"] = "cTrojan", ["server"] = "203.0.113.3", ["server_port"] = 443, ["password"] = "test", ["tls"] = Tls("example.com") },
    new() { ["type"] = "shadowsocks", ["tag"] = "cSs", ["server"] = "203.0.113.4", ["server_port"] = 8388, ["method"] = "chacha20-ietf-poly1305", ["password"] = "test" },
    new() { ["type"] = "hysteria2", ["tag"] = "cHy2", ["server"] = "203.0.113.5", ["server_port"] = 443, ["password"] = "test", ["tls"] = Tls("example.com") },
    new()
    {
        ["type"] = "tuic", ["tag"] = "cTuic", ["server"] = "203.0.113.6", ["server_port"] = 443, ["uuid"] = Uuid, ["password"] = "test",
        ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "example.com", ["alpn"] = new JsonArray("h3") }
    },
    new() { ["type"] = "socks", ["tag"] = "cSocks", ["server"] = "127.0.0.1", ["server_port"] = 10808 },
    new() { ["type"] = "http", ["tag"] = "cHttp", ["server"] = "127.0.0.1", ["server_port"] = 10809 },
    WireGuard("cWg", null),
    WireGuard("cAwg", new JsonObject
    {
        ["jc"] = 6, ["jmin"] = 10, ["jmax"] = 1000, ["s1"] = 24, ["s2"] = 12, ["s3"] = 16, ["s4"] = 13,
        ["h1"] = "100000-200000", ["h2"] = "300000-400000", ["h3"] = "500000-600000", ["h4"] = "700000-800000",
        ["i1"] = "<b 0xc70000000108><r 30><t>"
    })
};

JsonObject WireGuard(string tag, JsonObject? amnezia)
{
    var endpoint = new JsonObject
    {
        ["type"] = "wireguard", ["tag"] = tag, ["address"] = new JsonArray("10.66.0.2/32"), ["private_key"] = Key, ["mtu"] = 1420,
        ["peers"] = new JsonArray(new JsonObject
        {
            ["address"] = "203.0.113.7", ["port"] = 51820, ["public_key"] = Key, ["allowed_ips"] = new JsonArray("0.0.0.0/0", "::/0"),
            ["persistent_keepalive_interval"] = 25
        })
    };
    if (amnezia is not null)
        endpoint["amnezia"] = amnezia;
    return endpoint;
}

CoreRequest Request(bool hideDns, bool programs, bool block) => new()
{
    Outbounds = outbounds.Select(o => (JsonObject)o.DeepClone()).ToList(),
    Apps = outbounds.Select((o, i) => new CoreAppRoute
    {
        Tag = "a" + i,
        Outbound = (string)o["tag"]!,
        DirectDomains = i == 0 ? new List<string> { "example.com", "example.org" } : new List<string>(),
        Programs = programs
            ? new List<CoreProgram>
            {
                new() { Folder = $@"C:\Users\Пользователь\AppData\Local\App{i}", Name = $"app{i}.exe", Subfolders = true },
                new() { Folder = @"D:\Games (x86)\Game+", Name = $"game{i}.exe" }
            }
            : null,
        BlockOutside = block
    }).ToList(),
    Ipv6 = true,
    HideDns = hideDns,
    OutboundDns = new Dictionary<string, string> { ["cWg"] = "10.66.0.1", ["cAwg"] = "10.66.0.1" },
    DnsOutbound = hideDns ? "cVless" : null
};

var outDir = args[0];
Directory.CreateDirectory(outDir);
var api = new CoreApi(9090, "bench", 9091);
var cases = new Dictionary<string, CoreRequest>
{
    ["plain"] = Request(hideDns: false, programs: false, block: false),
    ["hide-dns"] = Request(hideDns: true, programs: false, block: false),
    ["outside-vpn"] = Request(hideDns: true, programs: true, block: false),
    ["outside-block"] = Request(hideDns: false, programs: true, block: true)
};
foreach (var (name, request) in cases)
{
    var config = CoreConfigBuilder.Build(request, api);
    File.WriteAllText(Path.Combine(outDir, name + ".json"), config.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() }));
    Console.WriteLine(name + ".json");
}
