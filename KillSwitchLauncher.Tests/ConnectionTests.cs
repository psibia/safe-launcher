using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using Xunit;
using Svc = SafeLauncher.Service;

namespace KillSwitchLauncher.Tests;

/// <summary>Ссылки и файлы, какими их раздают провайдеры (ключи ненастоящие).</summary>
public static class SampleConnections
{
    public const string VlessReality =
        "vless://b831381d-6324-4d53-ad4f-8cda48b30811@de.example.com:443?type=tcp&security=reality&pbk=A7zccni_SyWduZFcIk3dpRLbcvJ-w6pRkQzKD5OohDU" +
        "&sid=6ba85179e30d4fc2&sni=www.microsoft.com&fp=chrome&flow=xtls-rprx-vision&encryption=none#%F0%9F%87%A9%F0%9F%87%AA%20Германия";

    public const string VlessWs =
        "vless://b831381d-6324-4d53-ad4f-8cda48b30811@cdn.example.com:443?encryption=none&security=tls&sni=cdn.example.com&type=ws" +
        "&host=cdn.example.com&path=%2Fws%3Fed%3D2048#WS";

    public const string VlessGrpc =
        "vless://b831381d-6324-4d53-ad4f-8cda48b30811@1.2.3.4:8443?security=tls&type=grpc&serviceName=gun&alpn=h2&allowInsecure=1#gRPC";

    public const string VlessHttpUpgrade =
        "vless://b831381d-6324-4d53-ad4f-8cda48b30811@[2001:db8::1]:80?type=httpupgrade&host=a.example.com&path=%2Fup";

    public const string Vmess =
        "vmess://eyJ2IjogIjIiLCAicHMiOiAi0J3QuNC00LXRgNC70LDQvdC00YsiLCAiYWRkIjogIm5sLmV4YW1wbGUuY29tIiwgInBvcnQiOiAiNDQzIiwgImlkIjogIjFjMmIzYTRkLTVlNmYtNGExYi05YzJkLTNlNGY1YTZiN2M4ZCIsICJhaWQiOiAiMCIsICJzY3kiOiAiYXV0byIsICJuZXQiOiAid3MiLCAidHlwZSI6ICJub25lIiwgImhvc3QiOiAibmwuZXhhbXBsZS5jb20iLCAicGF0aCI6ICIvdm0iLCAidGxzIjogInRscyIsICJzbmkiOiAibmwuZXhhbXBsZS5jb20iLCAiYWxwbiI6ICJoMixodHRwLzEuMSIsICJmcCI6ICJmaXJlZm94In0=";

    public const string Trojan = "trojan://p%40ss%3Aword@tr.example.com:443?sni=tr.example.com&type=tcp#Trojan";

    public const string ShadowsocksSip002 = "ss://YWVzLTI1Ni1nY206c2VjcmV0@5.6.7.8:8388#SS";

    public const string Shadowsocks2022 = "ss://2022-blake3-aes-128-gcm:mPQnFCv3GqlJDP%2B0TsLiyA%3D%3D@5.6.7.8:443#SS-2022";

    public const string ShadowsocksLegacy = "ss://Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpwd0A1LjYuNy44OjgwMDA=#Old";

    public const string ShadowsocksObfs = "ss://YWVzLTI1Ni1nY206c2VjcmV0@5.6.7.8:8388/?plugin=obfs-local%3Bobfs%3Dhttp%3Bobfs-host%3Dwww.bing.com#Obfs";

    public const string Hysteria2 =
        "hysteria2://secret@hy.example.com:443/?sni=hy.example.com&obfs=salamander&obfs-password=cry&insecure=1&mport=20000-30000#HY2";

    public const string Hy2Short = "hy2://secret@hy.example.com?sni=hy.example.com";

    public const string Tuic =
        "tuic://1c2b3a4d-5e6f-4a1b-9c2d-3e4f5a6b7c8d:pw@tuic.example.com:443?congestion_control=bbr&udp_relay_mode=native&alpn=h3&sni=tuic.example.com#TUIC";

    public const string Socks = "socks://dXNlcjpwYXNz@9.9.9.9:1080#Proxy";

    public static readonly string[] Valid =
    {
        VlessReality, VlessWs, VlessGrpc, VlessHttpUpgrade, Vmess, Trojan, ShadowsocksSip002, Shadowsocks2022, ShadowsocksLegacy,
        ShadowsocksObfs, Hysteria2, Hy2Short, Tuic, Socks
    };

    public const string WireGuard = """
        [Interface]
        # Name = Финляндия
        PrivateKey = WAuhwkg2VQ2Li1vUHAo9cZDPkdDDFvg82c/oQfgrgRA=
        Address = 10.66.66.2/32, fd42:42:42::2/128
        DNS = 1.1.1.1
        MTU = 1420

        [Peer]
        PublicKey = x/b20vrPkzLMPl/3mw3gvCgbcQQsZdx1dHVYVCGU0a4=
        PresharedKey = aL9uNb6n9mAKo+QAkldvCYQBH0QDOf7olJeL+0y2q3c=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = fi.example.com:51820
        PersistentKeepalive = 25
        """;

    /// <summary>AmneziaWG 3.1: все виды параметров, неиспользуемые I2–I5 пустые (так пишет AmneziaVPN).</summary>
    public const string AmneziaWg = """
        [Interface]
        PrivateKey = WAuhwkg2VQ2Li1vUHAo9cZDPkdDDFvg82c/oQfgrgRA=
        Address = 10.8.1.2/32
        DNS = 1.1.1.1, 1.0.0.1
        MTU = 1376
        Jc = 4
        Jmin = 10
        Jmax = 50
        S1 = 24
        S2 = 31
        S3 = 17
        S4 = 14
        H1 = 1015624281-1015624290
        H2 = 2104758963 - 2104758970
        H3 = 523480127-523480135
        H4 = 1828407562-1828407570
        I1 = <r 2><b 0x858000010001000000000669636c6f756403636f6d0000010001c00c000100010000105a00044d583737>
        I2 =
        I3 =
        I4 =
        I5 =
        HeaderProtectionKey = 2tsfOTQeuz7v+9PoVEsrF4aryRUjCFCe6ZKKTstbYZE=
        ContentPaddingAddition = 0-32
        RekeyAfterTime = 110-130
        RandomTrailers = on
        DisableCookies = off

        [Peer]
        PublicKey = x/b20vrPkzLMPl/3mw3gvCgbcQQsZdx1dHVYVCGU0a4=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = awg.example.com:51820
        PersistentKeepalive = 20-30
        """;
}

public class ConnectionLinksTests
{
    [Fact]
    public void AllSamples_Parse()
    {
        foreach (var link in SampleConnections.Valid)
        {
            Assert.True(ConnectionLinks.LooksLikeLink(link), link);
            var server = ConnectionLinks.Parse(link);
            Assert.False(string.IsNullOrEmpty(server.Name));
            Assert.InRange(server.Port, 1, 65535);
            Assert.Null(server.Outbound["tag"]); // метку задаёт лаунчер
        }
    }

    [Fact]
    public void VlessReality()
    {
        var server = ConnectionLinks.Parse(SampleConnections.VlessReality);
        var o = server.Outbound;

        Assert.Equal(("VLESS", "🇩🇪 Германия", "de.example.com", 443), (server.Protocol, server.Name, server.Server, server.Port));
        Assert.Equal("VLESS · Reality · de.example.com:443", server.Summary);
        Assert.Equal("vless", (string)o["type"]!);
        Assert.Equal("xtls-rprx-vision", (string)o["flow"]!);
        Assert.Equal("www.microsoft.com", (string)o["tls"]!["server_name"]!);
        Assert.Equal("A7zccni_SyWduZFcIk3dpRLbcvJ-w6pRkQzKD5OohDU", (string)o["tls"]!["reality"]!["public_key"]!);
        Assert.Equal("6ba85179e30d4fc2", (string)o["tls"]!["reality"]!["short_id"]!);
        Assert.Equal("chrome", (string)o["tls"]!["utls"]!["fingerprint"]!);
        Assert.Null(o["transport"]);
    }

    [Fact]
    public void Websocket_EarlyDataMovesToHeader()
    {
        var o = ConnectionLinks.Parse(SampleConnections.VlessWs).Outbound;

        var ws = o["transport"]!;
        Assert.Equal("ws", (string)ws["type"]!);
        Assert.Equal("/ws", (string)ws["path"]!);
        Assert.Equal(2048, (int)ws["max_early_data"]!);
        Assert.Equal("Sec-WebSocket-Protocol", (string)ws["early_data_header_name"]!);
        Assert.Equal("cdn.example.com", (string)ws["headers"]!["Host"]!);
    }

    [Fact]
    public void Grpc_AndIpv6Server()
    {
        var grpc = ConnectionLinks.Parse(SampleConnections.VlessGrpc).Outbound;
        Assert.Equal("gun", (string)grpc["transport"]!["service_name"]!);
        Assert.True((bool)grpc["tls"]!["insecure"]!);
        Assert.Equal("h2", (string)grpc["tls"]!["alpn"]![0]!);

        var upgrade = ConnectionLinks.Parse(SampleConnections.VlessHttpUpgrade);
        Assert.Equal("2001:db8::1", upgrade.Server);
        Assert.Equal("[2001:db8::1]:80", upgrade.Summary.Split(" · ").Last());
        Assert.Equal("httpupgrade", (string)upgrade.Outbound["transport"]!["type"]!);
        Assert.Null(upgrade.Outbound["tls"]);
    }

    [Fact]
    public void Vmess_FromBase64Json()
    {
        var server = ConnectionLinks.Parse(SampleConnections.Vmess);

        Assert.Equal(("VMess", "Нидерланды", "nl.example.com", 443), (server.Protocol, server.Name, server.Server, server.Port));
        Assert.Equal("1c2b3a4d-5e6f-4a1b-9c2d-3e4f5a6b7c8d", (string)server.Outbound["uuid"]!);
        Assert.Equal("/vm", (string)server.Outbound["transport"]!["path"]!);
        Assert.Equal("firefox", (string)server.Outbound["tls"]!["utls"]!["fingerprint"]!);
        Assert.Equal(new[] { "h2", "http/1.1" }, server.Outbound["tls"]!["alpn"]!.AsArray().Select(a => (string)a!));
    }

    [Fact]
    public void Trojan_PasswordIsUnescaped_TlsByDefault()
    {
        var o = ConnectionLinks.Parse(SampleConnections.Trojan).Outbound;

        Assert.Equal("p@ss:word", (string)o["password"]!);
        Assert.True((bool)o["tls"]!["enabled"]!);
    }

    [Theory]
    [InlineData(SampleConnections.ShadowsocksSip002, "aes-256-gcm", "secret", 8388)]
    [InlineData(SampleConnections.Shadowsocks2022, "2022-blake3-aes-128-gcm", "mPQnFCv3GqlJDP+0TsLiyA==", 443)]
    [InlineData(SampleConnections.ShadowsocksLegacy, "chacha20-ietf-poly1305", "pw", 8000)]
    public void Shadowsocks_AllForms(string link, string method, string password, int port)
    {
        var server = ConnectionLinks.Parse(link);

        Assert.Equal(method, (string)server.Outbound["method"]!);
        Assert.Equal(password, (string)server.Outbound["password"]!);
        Assert.Equal("5.6.7.8", server.Server);
        Assert.Equal(port, server.Port);
    }

    [Fact]
    public void Shadowsocks_Plugin()
    {
        var o = ConnectionLinks.Parse(SampleConnections.ShadowsocksObfs).Outbound;

        Assert.Equal("obfs-local", (string)o["plugin"]!);
        Assert.Equal("obfs=http;obfs-host=www.bing.com", (string)o["plugin_opts"]!);
    }

    [Fact]
    public void Hysteria2_ObfsAndPortHopping()
    {
        var o = ConnectionLinks.Parse(SampleConnections.Hysteria2).Outbound;

        Assert.Equal("secret", (string)o["password"]!);
        Assert.Equal("salamander", (string)o["obfs"]!["type"]!);
        Assert.Equal("cry", (string)o["obfs"]!["password"]!);
        Assert.Equal("20000:30000", (string)o["server_ports"]![0]!);
        Assert.True((bool)o["tls"]!["insecure"]!);

        Assert.Equal(443, ConnectionLinks.Parse(SampleConnections.Hy2Short).Port); // порт по умолчанию
    }

    [Fact]
    public void Tuic_AndSocks()
    {
        var tuic = ConnectionLinks.Parse(SampleConnections.Tuic).Outbound;
        Assert.Equal("pw", (string)tuic["password"]!);
        Assert.Equal("bbr", (string)tuic["congestion_control"]!);

        var socks = ConnectionLinks.Parse(SampleConnections.Socks).Outbound;
        Assert.Equal(("user", "pass"), ((string)socks["username"]!, (string)socks["password"]!));
    }

    [Theory]
    [InlineData("vless://uuid@host:443?type=xhttp&security=tls", "XHTTP")]
    [InlineData("vless://@host:443", "UUID")]
    [InlineData("vless://b831381d-6324-4d53-ad4f-8cda48b30811@host:443?security=reality", "pbk")]
    [InlineData("vless://b831381d-6324-4d53-ad4f-8cda48b30811@host:443?security=reality&pbk=A7zccni_SyWduZFcIk3dpRLbcvJ", "pbk")]
    [InlineData("vless://b831381d-6324-4d53-ad4f-8cda48b30811@host:443?security=reality&pbk=A7zccni_SyWduZFcIk3dpRLbcvJ-w6pRkQzKD5OohDU&sid=xyz1", "sid")]
    [InlineData("vless://b831381d-6324-4d53-ad4f-8cda48b30811@host:443?security=reality&pbk=A7zccni_SyWduZFcIk3dpRLbcvJ-w6pRkQzKD5OohDU&sid=6ba85179e30d4fc2aa", "sid")]
    [InlineData("vless://b831381d-6324-4d53-ad4f-8cda48b30811@host", "порт")]
    [InlineData("vless://b831381d-6324-4d53-ad4f-8cda48b30811@host:99999", "порт")]
    [InlineData("vmess://not-base64!", "VMess")]
    [InlineData("ss://bad-method:pw@host:1", "Shadowsocks")]
    [InlineData("hysteria://x@host:443", "Hysteria2")]
    [InlineData("wireguard://x@host:1", ".conf")]
    [InlineData("https://example.com/sub", "подписк")]
    [InlineData("ftp://x", "не поддерживаются")]
    [InlineData("просто текст", "vless://")]
    public void BadLinks_Explained(string link, string hint)
    {
        var ex = Assert.Throws<ConnectionParseException>(() => ConnectionLinks.Parse(link));
        Assert.Contains(hint, ex.Message);
    }

    [Fact]
    public void RealityKey_StandardBase64_ConvertedForCore()
    {
        Assert.Equal("A7zccni_SyWduZFcIk3dpRLbcvJ-w6pRkQzKD5OohDU", ConnectionLinks.RealityPublicKey("A7zccni/SyWduZFcIk3dpRLbcvJ+w6pRkQzKD5OohDU="));
        Assert.Null(ConnectionLinks.RealityPublicKey("A7zccni_SyWduZFcIk3dpRLbcvJ-w6pRkQzKD5Ooh!U"));
    }

    [Fact]
    public void ServiceAcceptsEverythingParsed()
    {
        // Всё, что лаунчер получает из ссылок и файлов, служба соглашается запустить.
        var request = new Svc.CoreRequest();
        var i = 0;
        foreach (var link in SampleConnections.Valid)
        {
            var outbound = (JsonObject)ConnectionLinks.Parse(link).Outbound.DeepClone();
            outbound["tag"] = "c" + i++;
            request.Outbounds.Add(outbound);
        }

        var wg = (JsonObject)WireGuardConf.Parse(SampleConnections.WireGuard).Outbound.DeepClone();
        wg["tag"] = "wg";
        request.Outbounds.Add(wg);
        var awg = (JsonObject)WireGuardConf.Parse(SampleConnections.AmneziaWg).Outbound.DeepClone();
        awg["tag"] = "awg";
        request.Outbounds.Add(awg);

        Svc.CoreConfigBuilder.Build(request);
    }
}

public class WireGuardConfTests
{
    [Fact]
    public void Conf_BecomesEndpoint()
    {
        Assert.True(WireGuardConf.LooksLikeConf(SampleConnections.WireGuard));
        var server = WireGuardConf.Parse(SampleConnections.WireGuard, "файл");
        var o = server.Outbound;

        Assert.Equal(("WireGuard", "Финляндия", "fi.example.com", 51820), (server.Protocol, server.Name, server.Server, server.Port));
        Assert.Equal("wireguard", (string)o["type"]!);
        Assert.Equal(new[] { "10.66.66.2/32", "fd42:42:42::2/128" }, o["address"]!.AsArray().Select(a => (string)a!));
        Assert.Equal(1420, (int)o["mtu"]!);
        var peer = o["peers"]![0]!;
        Assert.Equal("x/b20vrPkzLMPl/3mw3gvCgbcQQsZdx1dHVYVCGU0a4=", (string)peer["public_key"]!);
        Assert.Equal(25, (int)peer["persistent_keepalive_interval"]!);
        Assert.Equal(2, peer["allowed_ips"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("DNS = 1.1.1.1", "1.1.1.1")] // весь трафик в туннель: любой адрес доступен
    [InlineData("DNS = corp.local, 10.0.0.53, 8.8.8.8", "10.0.0.53")] // имена — домены поиска
    [InlineData("", null)]
    public void Dns_TakenWhenReachableThroughTunnel(string line, string? expected)
    {
        var conf = SampleConnections.WireGuard.Replace("DNS = 1.1.1.1", line);
        Assert.Equal(expected, WireGuardConf.Parse(conf).Dns);
    }

    [Theory]
    [InlineData("DNS = 192.168.1.1, 10.1.2.3", "10.1.2.3")]
    [InlineData("DNS = 1.1.1.1", null)] // в туннель идёт только корпоративная сеть: 1.1.1.1 через него недоступен
    public void Dns_SplitTunnel_OnlyInsideAllowedIps(string line, string? expected)
    {
        var conf = SampleConnections.WireGuard.Replace("DNS = 1.1.1.1", line).Replace("AllowedIPs = 0.0.0.0/0, ::/0", "AllowedIPs = 10.0.0.0/8");
        Assert.Equal(expected, WireGuardConf.Parse(conf).Dns);
    }

    [Fact]
    public void Dns_ReachesCorePlan()
    {
        var conf = SampleConnections.WireGuard.Replace("DNS = 1.1.1.1", "DNS = 10.66.66.1");
        var connection = new KillSwitchLauncher.Models.VpnConnection { Name = "wg", Kind = KillSwitchLauncher.Models.ConnectionKind.WireGuard, WireGuardConfig = conf };
        var (_, dns) = ConnectionOutbound.BuildWithDns(connection, "c1");
        Assert.Equal("10.66.66.1", dns);
    }

    [Fact]
    public void NoName_UsesFileName()
    {
        var conf = SampleConnections.WireGuard.Replace("# Name = Финляндия", "");
        Assert.Equal("office", WireGuardConf.Parse(conf, "office").Name);
    }

    [Fact]
    public void Plain_HasNoAmnezia()
    {
        var server = WireGuardConf.Parse(SampleConnections.WireGuard);
        Assert.Equal("WireGuard", server.Protocol);
        Assert.Null(server.Outbound["amnezia"]);
        Assert.Equal("WireGuard", WireGuardConf.ProtocolName(SampleConnections.WireGuard));
    }

    [Fact]
    public void Amnezia_BecomesEndpointWithAllParameters()
    {
        var server = WireGuardConf.Parse(SampleConnections.AmneziaWg, "файл");
        var o = server.Outbound;

        Assert.Equal(("AmneziaWG", "файл", "awg.example.com", 51820), (server.Protocol, server.Name, server.Server, server.Port));
        Assert.Equal("wireguard", (string)o["type"]!);
        Assert.Equal(1376, (int)o["mtu"]!);
        var expected = new JsonObject
        {
            ["jc"] = 4, ["jmin"] = 10, ["jmax"] = 50, ["s1"] = 24, ["s2"] = 31, ["s3"] = 17, ["s4"] = 14,
            ["h1"] = "1015624281-1015624290", ["h2"] = "2104758963-2104758970", ["h3"] = "523480127-523480135", ["h4"] = "1828407562-1828407570",
            ["i1"] = "<r 2><b 0x858000010001000000000669636c6f756403636f6d0000010001c00c000100010000105a00044d583737>",
            ["header_protection_key"] = "2tsfOTQeuz7v+9PoVEsrF4aryRUjCFCe6ZKKTstbYZE=",
            ["content_padding_addition"] = "0-32", ["rekey_after_time"] = "110-130", ["random_trailers"] = true
        };
        Assert.Equal(expected.ToJsonString(), o["amnezia"]!.ToJsonString());
        var peer = o["peers"]![0]!;
        Assert.Equal("20-30", (string)peer["persistent_keepalive_range"]!);
        Assert.Null(peer["persistent_keepalive_interval"]);
        Assert.Equal("1.1.1.1", server.Dns);
    }

    [Theory]
    // AmneziaWG 1.0: одно число в H1–H4
    [InlineData("Jc = 4\nJmin = 40\nJmax = 70\nS1 = 15\nS2 = 40\nH1 = 1020325451\nH2 = 3288052141\nH3 = 1766607858\nH4 = 2528465083",
        "{\"jc\":4,\"jmin\":40,\"jmax\":70,\"s1\":15,\"s2\":40,\"h1\":\"1020325451\",\"h2\":\"3288052141\",\"h3\":\"1766607858\",\"h4\":\"2528465083\"}")]
    // AmneziaWG 1.5 из AmneziaVPN: J1–J3 и Itime, пустые значения не заданы
    [InlineData("Jc = 3\nJmin = 10\nJmax = 50\nS1 = 68\nS2 = 149\nH1 = 1206720915\nH2 = 916419016\nH3 = 1477385012\nH4 = 171283462\n" +
                "I1 = <b 0xc70000000108><r 16><c><t>\nI2 = <b 0xc7><wt 10><r 20>\nI3 =\nI4 =\nI5 =\nJ1 = <b 0x1234><r 30>\nJ2 =\nJ3 =\nItime = 120",
        "{\"jc\":3,\"jmin\":10,\"jmax\":50,\"s1\":68,\"s2\":149,\"h1\":\"1206720915\",\"h2\":\"916419016\",\"h3\":\"1477385012\",\"h4\":\"171283462\"," +
        "\"i1\":\"<b 0xc70000000108><r 16><c><t>\",\"i2\":\"<b 0xc7><wt 10><r 20>\",\"j1\":\"<b 0x1234><r 30>\",\"itime\":120}")]
    // AmneziaWG 2.0: S3, S4 и диапазоны H1–H4
    [InlineData("jc = 4\njmin = 40\njmax = 70\ns1 = 15\ns2 = 40\ns3 = 18\ns4 = 9\nh1 = 100000-200000\nh2 = 300000-400000\nh3 = 500000-600000\nh4 = 700000-800000\nI1 = <B 0X0102><R 100><RD 5>",
        "{\"jc\":4,\"jmin\":40,\"jmax\":70,\"s1\":15,\"s2\":40,\"s3\":18,\"s4\":9,\"h1\":\"100000-200000\",\"h2\":\"300000-400000\"," +
        "\"h3\":\"500000-600000\",\"h4\":\"700000-800000\",\"i1\":\"<B 0X0102><R 100><RD 5>\"}")]
    // AmneziaWG 3: сроки и попытки, ключ из нулей — то же, что без ключа
    [InlineData("S1 = 5\nHeaderProtectionKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\nKeepaliveTimeout = 8-12\nRekeyTimeout = 5\nRejectAfterTime = 170-180\nMaxHandshakeAttempts = 10-20\nDisableCookies = 1",
        "{\"s1\":5,\"rekey_timeout\":\"5\",\"reject_after_time\":\"170-180\",\"keepalive_timeout\":\"8-12\",\"max_handshake_attempts\":\"10-20\",\"disable_cookies\":true}")]
    // одни нули и пустые значения: это обычный WireGuard
    [InlineData("Jc = 0\nS1 = 0\nI1 =\nI2 = без тегов\nContentPaddingAddition = 0\nRandomTrailers = off\nItime =", null)]
    // текст вне тегов ядро пропускает
    [InlineData("I1 = пакет: <r 10>, конец", "{\"i1\":\"пакет: <r 10>, конец\"}")]
    public void Amnezia_AllVersions(string lines, string? expected)
    {
        var server = WireGuardConf.Parse(SampleConnections.WireGuard.Replace("MTU = 1420", "MTU = 1420\n" + lines));
        Assert.Equal(expected is null ? null : JsonNode.Parse(expected)!.ToJsonString(), server.Outbound["amnezia"]?.ToJsonString());
        Assert.Equal(expected is null ? "WireGuard" : "AmneziaWG", server.Protocol);
    }

    [Theory]
    [InlineData("20-30", "range", "20-30")]
    [InlineData("20 - 30", "range", "20-30")]
    [InlineData("25-25", "interval", "25")]
    [InlineData("0-15", "range", "0-15")]
    [InlineData("off", null, null)]
    [InlineData("0", null, null)]
    [InlineData("30-20", null, null)]
    public void Keepalive_AmneziaRange(string value, string? kind, string? expected)
    {
        var peer = WireGuardConf.Parse(SampleConnections.WireGuard.Replace("PersistentKeepalive = 25", "PersistentKeepalive = " + value)).Outbound["peers"]![0]!;
        Assert.Equal(kind == "range" ? expected : null, peer["persistent_keepalive_range"]?.GetValue<string>());
        Assert.Equal(kind == "interval" ? expected : null, peer["persistent_keepalive_interval"]?.GetValue<int>().ToString());
    }

    [Theory]
    [InlineData("Jc = 2000", "Jc")]
    [InlineData("Jc = -1", "Jc")]
    [InlineData("Jc = 4\nJmin = 50\nJmax = 10", "Jmax (10) меньше Jmin (50)")]
    [InlineData("Jc = 4\nJmax = 70000", "Jmax")]
    [InlineData("S1 = 65400", "S1")]
    [InlineData("S4 = abc", "S4")]
    [InlineData("H1 = 5-10\nH2 = 8-20", "пересекаются H1 и H2")]
    [InlineData("H1 = 2", "пересекаются H1 и H2")] // H2 не задан, то есть 2
    [InlineData("H3 = 10-5", "H3")]
    [InlineData("H4 = 1-2-3", "H4")]
    [InlineData("HeaderProtectionKey = 2tsfOTQeuz7v+9PoVEsrF4aryRUjCFCe6ZKKTstbYZE=\nS1 = 24\nS2 = 24\nS3 = 24\nS4 = 11", "S4 = 11")]
    [InlineData("HeaderProtectionKey = c2hvcnQ=", "HeaderProtectionKey")]
    [InlineData("I1 = <b 0xZZ>", "<b>")]
    [InlineData("I1 = <b>", "<b>")]
    [InlineData("I1 = <r 10", "«>»")]
    [InlineData("I1 = <>", "пустой тег")]
    [InlineData("I1 = <x 5>", "Неизвестный тег <x>")]
    [InlineData("I1 = <c 5>", "<c>")]
    [InlineData("I1 = <wt 6000>", "<wt>")]
    [InlineData("I1 = <r -1>", "<r>")]
    [InlineData("I1 = <r 65000><r 1000>", "больше 65499")]
    [InlineData("J2 = <rc 5>\u0001<t>", "невидимый символ")]
    [InlineData("RandomTrailers = maybe", "on или off")]
    [InlineData("ContentPaddingAddition = 32-16", "ContentPaddingAddition")]
    public void Amnezia_Broken_Explained(string lines, string hint)
    {
        var conf = SampleConnections.WireGuard.Replace("MTU = 1420", "MTU = 1420\n" + lines);
        var ex = Assert.Throws<ConnectionParseException>(() => WireGuardConf.Parse(conf));
        Assert.Contains(hint, ex.Message);
        Assert.Equal("AmneziaWG", WireGuardConf.ProtocolName(conf));
    }

    [Fact]
    public void Amnezia_InPeerSection_Explained()
    {
        var conf = SampleConnections.WireGuard.Replace("PersistentKeepalive = 25", "PersistentKeepalive = 25\nJc = 4");
        Assert.Contains("[Peer]", Assert.Throws<ConnectionParseException>(() => WireGuardConf.Parse(conf)).Message);
    }

    [Fact]
    public void Amnezia_AddedAndShownInList()
    {
        var connection = ConnectionFactory.FromText(SampleConnections.AmneziaWg, Array.Empty<VpnConnection>());
        Assert.Equal(ConnectionKind.WireGuard, connection.Kind);
        Assert.Equal("awg.example.com:51820", connection.Name);
        Assert.Equal("AmneziaWG", ConnectionFactory.Describe(connection));
        connection.WireGuardConfig = SampleConnections.AmneziaWg.Replace("Jc = 4", "Jc = 5000");
        Assert.Equal("AmneziaWG · файл повреждён", ConnectionFactory.Describe(connection));
    }

    [Theory]
    [InlineData("Endpoint = fi.example.com:51820", "", "Endpoint")]
    [InlineData("PrivateKey = WAuhwkg2VQ2Li1vUHAo9cZDPkdDDFvg82c/oQfgrgRA=", "PrivateKey = broken", "PrivateKey")]
    [InlineData("[Peer]", "[Peer]\nPublicKey = x/b20vrPkzLMPl/3mw3gvCgbcQQsZdx1dHVYVCGU0a4=\nEndpoint = a.b:1\n[Peer]", "несколько")]
    public void Broken_Explained(string from, string to, string hint)
    {
        var ex = Assert.Throws<ConnectionParseException>(() => WireGuardConf.Parse(SampleConnections.WireGuard.Replace(from, to)));
        Assert.Contains(hint, ex.Message);
    }
}

/// <summary>Ключи vpn:// AmneziaVPN, собранные так же, как их собирает AmneziaVPN (ключи ненастоящие).</summary>
public class AmneziaVpnKeyTests
{
    /// <summary>Текст файла в ключе AmneziaWG: шаблон AmneziaVPN, DNS она подставляет при подключении, MTU в нём нет.</summary>
    private const string AwgConfig = """
        [Interface]
        Address = 10.8.1.2/32
        DNS = $PRIMARY_DNS, $SECONDARY_DNS
        PrivateKey = WAuhwkg2VQ2Li1vUHAo9cZDPkdDDFvg82c/oQfgrgRA=
        Jc = 4
        Jmin = 10
        Jmax = 50
        S1 = 24
        S2 = 31
        S3 = 17
        S4 = 14
        H1 = 1015624281-1015624290
        H2 = 2104758963-2104758970
        H3 = 523480127-523480135
        H4 = 1828407562-1828407570
        I1 = <r 2><b 0x858000010001000000000669636c6f756403636f6d0000010001c00c000100010000105a00044d583737>
        HeaderProtectionKey = 2tsfOTQeuz7v+9PoVEsrF4aryRUjCFCe6ZKKTstbYZE=
        ContentPaddingAddition = 10-100
        RekeyAfterTime = 100-120
        RandomTrailers = on
        DisableCookies = on

        [Peer]
        PublicKey = x/b20vrPkzLMPl/3mw3gvCgbcQQsZdx1dHVYVCGU0a4=
        PresharedKey = aL9uNb6n9mAKo+QAkldvCYQBH0QDOf7olJeL+0y2q3c=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = awg.example.com:51820
        PersistentKeepalive = 25-35
        """;

    private const string WgConfig = """
        [Interface]
        Address = 10.8.1.3/32
        DNS = $PRIMARY_DNS, $SECONDARY_DNS
        PrivateKey = WAuhwkg2VQ2Li1vUHAo9cZDPkdDDFvg82c/oQfgrgRA=

        [Peer]
        PublicKey = x/b20vrPkzLMPl/3mw3gvCgbcQQsZdx1dHVYVCGU0a4=
        PresharedKey = aL9uNb6n9mAKo+QAkldvCYQBH0QDOf7olJeL+0y2q3c=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = wg.example.com:51820
        PersistentKeepalive = 25
        """;

    /// <summary>Настройки сервера, как в ключе «Для приложения AmneziaVPN»: один контейнер, настройки подключения строкой JSON.</summary>
    private static JsonObject Server(string container, string section, string config, string? dns1 = "1.1.1.1", string? dns2 = "1.0.0.1")
    {
        var last = new JsonObject
        {
            ["config"] = config, ["hostName"] = "awg.example.com", ["port"] = 51820, ["client_ip"] = "10.8.1.2",
            ["client_priv_key"] = "WAuhwkg2VQ2Li1vUHAo9cZDPkdDDFvg82c/oQfgrgRA=", ["mtu"] = "1376", ["persistent_keep_alive"] = "25-35"
        };
        var root = new JsonObject
        {
            ["format_version"] = 1,
            ["description"] = "Мой сервер",
            ["hostName"] = "awg.example.com",
            ["containers"] = new JsonArray(new JsonObject
            {
                ["container"] = container,
                [section] = new JsonObject { ["port"] = "51820", ["transport_proto"] = "udp", ["Jc"] = "4", ["last_config"] = last.ToJsonString() }
            }),
            ["defaultContainer"] = container
        };
        if (dns1 is not null)
            root["dns1"] = dns1;
        if (dns2 is not null)
            root["dns2"] = dns2;
        return root;
    }

    /// <summary>Как ключ собирает AmneziaVPN: JSON, qCompress (длина big-endian и поток zlib) и base64url без «=».</summary>
    private static string Key(JsonObject json, bool compress = true, byte[]? header = null, bool urlSafe = true)
    {
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        if (compress)
        {
            using var output = new MemoryStream();
            output.Write(header ?? new[] { (byte)(bytes.Length >> 24), (byte)(bytes.Length >> 16), (byte)(bytes.Length >> 8), (byte)bytes.Length });
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(bytes);
            bytes = output.ToArray();
        }

        var base64 = Convert.ToBase64String(bytes);
        return "vpn://" + (urlSafe ? base64.TrimEnd('=').Replace('+', '-').Replace('/', '_') : base64);
    }

    [Fact]
    public void OwnServerKey_BecomesAmneziaWg()
    {
        var key = Key(Server("amnezia-awg2", "awg", AwgConfig));
        Assert.True(ConnectionFactory.LooksLikeConnection(key));

        var connection = ConnectionFactory.FromText(key, Array.Empty<VpnConnection>());

        Assert.Equal(ConnectionKind.WireGuard, connection.Kind);
        Assert.Equal("Мой сервер", connection.Name);
        // тот же текст файла, что сохраняет AmneziaVPN, с DNS и MTU, которые она подставляет при подключении
        var expected = AwgConfig.Replace("\r\n", "\n").Replace("$PRIMARY_DNS, $SECONDARY_DNS", "1.1.1.1, 1.0.0.1").Replace("[Interface]", "[Interface]\nMTU = 1376");
        Assert.Equal(expected, connection.WireGuardConfig);
        Assert.Equal("AmneziaWG", ConnectionFactory.Describe(connection));

        var server = ConnectionOutbound.Resolve(connection);
        Assert.Equal(("AmneziaWG", "awg.example.com", 51820), (server.Protocol, server.Server, server.Port));
        Assert.Equal(1376, (int)server.Outbound["mtu"]!);
        Assert.Equal("10-100", (string)server.Outbound["amnezia"]!["content_padding_addition"]!);
        Assert.Equal("25-35", (string)server.Outbound["peers"]![0]!["persistent_keepalive_range"]!);
        Assert.Equal("1.1.1.1", server.Dns);
    }

    [Theory]
    [InlineData("172.29.172.254", "1.0.0.1", "DNS = 172.29.172.254, 1.0.0.1", "172.29.172.254")] // AmneziaDNS на самом сервере
    [InlineData(null, null, "DNS = 1.1.1.1, 1.0.0.1", "1.1.1.1")] // в ключе DNS нет: как у AmneziaVPN по умолчанию
    [InlineData("dns.example.com", "8.8.4.4", "DNS = 1.1.1.1, 8.8.4.4", "1.1.1.1")] // AmneziaVPN берёт из ключа только адреса IPv4
    public void Dns_FilledAsAmneziaVpnDoes(string? dns1, string? dns2, string line, string tunnelDns)
    {
        var (conf, _) = AmneziaVpnKey.ToConf(Key(Server("amnezia-awg", "awg", AwgConfig, dns1, dns2)));
        Assert.Contains(line, conf);
        Assert.Equal(tunnelDns, WireGuardConf.Parse(conf).Dns);
    }

    [Fact]
    public void AnyEncoding_IsRead()
    {
        var json = Server("amnezia-awg", "awg", AwgConfig);
        var expected = AmneziaVpnKey.ToConf(Key(json)).Conf;

        Assert.Equal(expected, AmneziaVpnKey.ToConf(Key(json, compress: false)).Conf); // несжатый JSON
        Assert.Equal(expected, AmneziaVpnKey.ToConf(Key(json, header: new byte[] { 0, 0, 0, 0xff })).Conf); // метка вместо длины
        Assert.Equal(expected, AmneziaVpnKey.ToConf(Key(json, urlSafe: false)).Conf); // обычный base64 с «=»
        // ключ, перенесённый на несколько строк, как в письме
        var key = Key(json);
        var wrapped = "  VPN://" + string.Join("\r\n", key["vpn://".Length..].Chunk(64).Select(c => new string(c))) + "\n";
        Assert.Equal(expected, ConnectionFactory.FromText(wrapped, Array.Empty<VpnConnection>()).WireGuardConfig);
    }

    [Fact]
    public void DefaultContainerFirst_ThenAmneziaWg()
    {
        var json = Server("amnezia-wireguard", "wireguard", WgConfig);
        json["containers"]!.AsArray().Add(Server("amnezia-awg", "awg", AwgConfig)["containers"]![0]!.DeepClone());
        Assert.Equal("WireGuard", WireGuardConf.Parse(AmneziaVpnKey.ToConf(Key(json)).Conf).Protocol);

        json.Remove("defaultContainer");
        Assert.Equal("AmneziaWG", WireGuardConf.Parse(AmneziaVpnKey.ToConf(Key(json)).Conf).Protocol);
    }

    [Fact]
    public void MtuFromFile_IsKept_NoName_UsesServer()
    {
        var json = Server("amnezia-awg", "awg", AwgConfig.Replace("Address = 10.8.1.2/32", "Address = 10.8.1.2/32\nMTU = 1280"));
        json.Remove("description");

        var connection = ConnectionFactory.FromText(Key(json), Array.Empty<VpnConnection>());

        Assert.Equal(1, connection.WireGuardConfig!.Split("MTU").Length - 1);
        Assert.Equal(1280, (int)ConnectionOutbound.Resolve(connection).Outbound["mtu"]!);
        Assert.Equal("awg.example.com:51820", connection.Name);
    }

    [Theory]
    [InlineData("service", "Premium или Free")]
    [InlineData("full", "полного доступа")]
    [InlineData("xray", "подключение XRay")]
    [InlineData("empty", "нет настроек подключения")]
    [InlineData("placeholder", "$WIREGUARD_CLIENT_PRIVATE_KEY")]
    [InlineData("newer", "более новой версией AmneziaVPN")]
    [InlineData("bad-awg", "Настройки из ключа vpn:// не подходят. В файле AmneziaWG неверный параметр Jc")]
    public void UnsuitableKey_Explained(string kind, string hint)
    {
        var json = kind switch
        {
            "service" => new JsonObject
            {
                ["config_version"] = 2, ["api_config"] = new JsonObject { ["service_type"] = "amnezia-premium" },
                ["auth_data"] = new JsonObject { ["api_key"] = "secret" }
            },
            "full" => new JsonObject
            {
                ["hostName"] = "awg.example.com", ["userName"] = "root", ["password"] = "secret", ["port"] = 22,
                ["containers"] = new JsonArray(new JsonObject { ["container"] = "amnezia-awg", ["awg"] = new JsonObject { ["port"] = "51820" } })
            },
            "xray" => Server("amnezia-xray", "xray", "{}"),
            "empty" => new JsonObject { ["hostName"] = "awg.example.com" },
            "placeholder" => Server("amnezia-awg", "awg", AwgConfig.Replace("WAuhwkg2VQ2Li1vUHAo9cZDPkdDDFvg82c/oQfgrgRA=", "$WIREGUARD_CLIENT_PRIVATE_KEY")),
            "newer" => Server("amnezia-awg4", "awg", AwgConfig),
            _ => Server("amnezia-awg", "awg", AwgConfig.Replace("Jc = 4", "Jc = 5000"))
        };
        if (kind == "newer")
            json["format_version"] = 2;

        var ex = Assert.Throws<ConnectionParseException>(() => ConnectionFactory.FromText(Key(json), Array.Empty<VpnConnection>()));
        Assert.Contains(hint, ex.Message);
    }

    [Theory]
    [InlineData("vpn://")]
    [InlineData("vpn://не-base64!")]
    [InlineData("vpn://AAAA")]
    public void BrokenKey_Explained(string key) =>
        Assert.Contains("не читается", Assert.Throws<ConnectionParseException>(() => ConnectionFactory.FromText(key, Array.Empty<VpnConnection>())).Message);

    [Fact]
    public void CutKey_Explained()
    {
        var key = Key(Server("amnezia-awg", "awg", AwgConfig));
        Assert.Contains("не читается", Assert.Throws<ConnectionParseException>(() => AmneziaVpnKey.ToConf(key[..(key.Length / 2)])).Message);
    }

    [Fact]
    public void HugeKey_IsNotUnpacked()
    {
        var json = Server("amnezia-awg", "awg", AwgConfig);
        json["padding"] = new string(' ', 2 << 20);
        Assert.Contains("слишком большой", Assert.Throws<ConnectionParseException>(() => AmneziaVpnKey.ToConf(Key(json))).Message);
    }
}

public class SubscriptionTests
{
    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Base64List_KeepsGoodSkipsBad()
    {
        var body = Base64(string.Join("\n", SampleConnections.VlessReality, "vless://broken", SampleConnections.Trojan, ""));

        var content = Subscriptions.Parse(body);

        Assert.Equal(new[] { SampleConnections.VlessReality, SampleConnections.Trojan }, content.Links);
        Assert.Equal(1, content.Skipped);
    }

    [Fact]
    public void PlainList_Works()
    {
        var content = Subscriptions.Parse(SampleConnections.Tuic + "\r\n" + SampleConnections.Socks + "\r\n");
        Assert.Equal(2, content.Links.Count);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html></html>", "веб-страница")]
    [InlineData("{\"outbounds\": []}", "sing-box")]
    [InlineData("proxies:\n  - name: a", "Clash")]
    [InlineData("", "не нашлось")]
    public void OtherFormats_Explained(string body, string hint)
    {
        Assert.Contains(hint, Assert.Throws<ConnectionParseException>(() => Subscriptions.Parse(body)).Message);
    }

    [Fact]
    public void Selection_SurvivesRename()
    {
        var servers = new[] { "vless://a@h:1#Один", "vless://b@h:1#Два" };

        Assert.Equal(servers[1], Subscriptions.Pick(servers, "vless://b@h:1#Старое имя"));
        Assert.Equal(servers[0], Subscriptions.Pick(servers, "vless://gone@h:1#Нет"));
        Assert.Equal(servers[0], Subscriptions.Pick(servers, null));
        Assert.Null(Subscriptions.Pick(Array.Empty<string>(), null));
    }

    [Fact]
    public void Selection_SurvivesRename_VmessNameInsideBase64()
    {
        static string Vmess(string name, string host) => "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            new JsonObject { ["v"] = "2", ["ps"] = name, ["add"] = host, ["port"] = "443", ["id"] = "1c2b3a4d-5e6f-4a1b-9c2d-3e4f5a6b7c8d" }.ToJsonString()));
        var servers = new[] { Vmess("Один", "a.example.com"), Vmess("Два", "b.example.com") };

        Assert.Equal(servers[1], Subscriptions.Pick(servers, Vmess("Старое имя", "b.example.com")));
        Assert.Equal(servers[1], Subscriptions.Pick(servers, Subscriptions.ServerKey(Vmess("Старое имя", "b.example.com"))));
        Assert.Equal(servers[0], Subscriptions.Pick(servers, Vmess("Два", "c.example.com")));
    }

    [Fact]
    public async Task Fetch_UsesHttpAndExplainsErrors()
    {
        var ok = await Subscriptions.FetchAsync("https://sub.example.com/x", new FakeHandler(HttpStatusCode.OK, Base64(SampleConnections.Hysteria2)));
        Assert.Single(ok.Links);

        var ex = await Assert.ThrowsAsync<ConnectionParseException>(() =>
            Subscriptions.FetchAsync("https://sub.example.com/x", new FakeHandler(HttpStatusCode.Forbidden, "")));
        Assert.Contains("403", ex.Message);
        await Assert.ThrowsAsync<ConnectionParseException>(() => Subscriptions.FetchAsync("ftp://x", new FakeHandler(HttpStatusCode.OK, "")));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public FakeHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }
}

public class ConnectionOutboundTests
{
    [Fact]
    public void EveryKind_BuildsWithTag()
    {
        var connections = new[]
        {
            new VpnConnection { Name = "Ссылка", Kind = ConnectionKind.Link, Link = SampleConnections.VlessReality },
            new VpnConnection
            {
                Name = "Подписка", Kind = ConnectionKind.Subscription,
                SubscriptionServers = { SampleConnections.Trojan, SampleConnections.Tuic }, SelectedServer = SampleConnections.Tuic
            },
            new VpnConnection { Name = "WG", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.WireGuard },
            new VpnConnection { Name = "v2rayN", Kind = ConnectionKind.LocalProxy, ProxyPort = 10808 },
            new VpnConnection { Name = "Clash", Kind = ConnectionKind.LocalProxy, ProxyPort = 7890, ProxyProtocol = ProxyProtocol.Http }
        };
        var types = connections.Select(c => (string)ConnectionOutbound.Build(c, "t")["type"]!).ToArray();

        Assert.Equal(new[] { "vless", "tuic", "wireguard", "socks", "http" }, types);
        Assert.All(connections, c => Assert.Equal("t", (string)ConnectionOutbound.Build(c, "t")["tag"]!));
    }

    [Fact]
    public void Unconfigured_Explained()
    {
        Assert.Throws<ConnectionParseException>(() => ConnectionOutbound.Resolve(new VpnConnection { Kind = ConnectionKind.Link }));
        Assert.Throws<ConnectionParseException>(() => ConnectionOutbound.Resolve(new VpnConnection { Kind = ConnectionKind.Subscription }));
        Assert.Throws<ConnectionParseException>(() => ConnectionOutbound.Resolve(new VpnConnection { Kind = ConnectionKind.LocalProxy }));
    }
}

public class ConnectionTesterTests
{
    [Fact]
    public void Config_ProxyInFrontOfServer()
    {
        var config = ConnectionTester.Config(ConnectionLinks.Parse(SampleConnections.Trojan).Outbound, 12345);

        Assert.Equal("mixed", (string)config["inbounds"]![0]!["type"]!);
        Assert.Equal("127.0.0.1", (string)config["inbounds"]![0]!["listen"]!);
        Assert.Equal("check", (string)config["outbounds"]![0]!["tag"]!);
        Assert.Equal("check", (string)config["route"]!["final"]!);
        Assert.Null(config["endpoints"]);
    }

    [Fact]
    public void Config_WireGuardIsEndpoint()
    {
        var config = ConnectionTester.Config(WireGuardConf.Parse(SampleConnections.WireGuard).Outbound, 1);

        Assert.Equal("check", (string)config["endpoints"]![0]!["tag"]!);
        Assert.Equal("direct", (string)config["outbounds"]![0]!["type"]!);
    }

    [Theory]
    [InlineData("ERROR connection: dial tcp 1.2.3.4:443: connectex: No connection could be made because the target machine actively refused it.", "отклонил")]
    [InlineData("ERROR dial tcp: lookup bad.example: no such host", "по имени")]
    [InlineData("ERROR reality verification failed", "Reality")]
    [InlineData("ERROR i/o timeout", "не отвечает")]
    [InlineData("ERROR tls: failed to verify certificate: x509: certificate signed by unknown authority", "Сертификат")]
    [InlineData("ERROR something odd: boom", "boom")]
    [InlineData("INFO nothing else", "ключ доступа")]
    public void Explain_ReadsCoreLog(string line, string hint)
    {
        Assert.Contains(hint, ConnectionTester.Explain(new List<string> { "INFO started", line }));
    }

    [Fact]
    public void RouteLines_OnlyConnectionsAndErrors()
    {
        var log = new List<string> { "INFO started", "INFO endpoint/wireguard[check]: outbound connection to 1.2.3.4:80", "ERROR boom" };
        Assert.Equal(new[] { log[1], log[2] }, ConnectionTester.RouteLines(log));
    }

    [Fact]
    public void Explain_WireGuardSilence_MentionsKeyInUseElsewhere()
    {
        Assert.Contains("приложении WireGuard", ConnectionTester.Explain(new List<string> { "INFO started" }, wireGuard: true));
        Assert.Contains("параметры AmneziaWG", ConnectionTester.Explain(new List<string> { "INFO started" }, wireGuard: true, amnezia: true));
        Assert.Contains("подписка", ConnectionTester.Explain(new List<string> { "INFO started" }));
    }

    [Fact]
    public async Task ViaCore_ProxyWithConnectionAndKey()
    {
        using var proxy = new FakeProxy("c1", "ключ");
        var tester = new ConnectionTester(Path.Combine(Path.GetTempPath(), "нет-ядра.exe"),
            new Uri("http://check.test/generate_204"), new Uri("http://check.test/cdn-cgi/trace"));
        var server = WireGuardConf.Parse(SampleConnections.AmneziaWg).Outbound;

        var result = await tester.CheckAsync(server, new CoreCheckRoute(new CoreCheckProxy(proxy.Port, "ключ"), "c1"));

        Assert.True(result.Ok, result.Error);
        Assert.Equal(("203.0.113.7", "DE"), (result.Ip, result.CountryCode));
        Assert.True(proxy.Challenged); // без ключа прокси ядра не пускает
        Assert.Contains("GET http://check.test/cdn-cgi/trace", proxy.Authorized);
    }

    [Fact]
    public async Task ViaCore_ServerSilent_Explained()
    {
        using var proxy = new FakeProxy("c1", "ключ") { Status = "502 Bad Gateway" };
        var tester = new ConnectionTester(Path.Combine(Path.GetTempPath(), "нет-ядра.exe"),
            new Uri("http://check.test/generate_204"), new Uri("http://check.test/cdn-cgi/trace"));

        var result = await tester.CheckAsync(WireGuardConf.Parse(SampleConnections.AmneziaWg).Outbound,
            new CoreCheckRoute(new CoreCheckProxy(proxy.Port, "ключ"), "c1"));

        Assert.False(result.Ok);
        Assert.Contains("AmneziaWG", result.Error);
        Assert.Contains("работает в лаунчере", result.Error);
    }

    /// <summary>Прокси ядра для проверки: без имени и ключа отвечает 407, с ними — ответы проверочных страниц.</summary>
    private sealed class FakeProxy : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _authorization;

        public FakeProxy(string user, string password)
        {
            _authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string Status { get; init; } = "200 OK";

        public bool Challenged { get; private set; }

        public List<string> Authorized { get; } = new();

        private async Task AcceptLoop()
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;
                }

                using (client)
                {
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var buffer = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer) == 1)
                        head.Append((char)buffer[0]);
                    var lines = head.ToString().Split("\r\n");
                    string reply;
                    if (!lines.Any(l => l.Equals("Proxy-Authorization: " + _authorization, StringComparison.OrdinalIgnoreCase)))
                    {
                        Challenged = true;
                        reply = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"proxy\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    }
                    else
                    {
                        lock (Authorized)
                            Authorized.Add(lines[0][..lines[0].LastIndexOf(' ')]);
                        var body = lines[0].Contains("trace", StringComparison.Ordinal) ? "ip=203.0.113.7\nloc=DE\n" : string.Empty;
                        reply = Status != "200 OK" ? $"HTTP/1.1 {Status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                            : lines[0].Contains("generate_204", StringComparison.Ordinal) ? "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"
                            : $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                    }

                    await stream.WriteAsync(Encoding.UTF8.GetBytes(reply));
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public void CountryName_FromCode()
    {
        Assert.False(string.IsNullOrEmpty(ConnectionTester.CountryName("DE")));
        Assert.Null(ConnectionTester.CountryName("XX"));
        Assert.Null(ConnectionTester.CountryName(null));
    }
}
