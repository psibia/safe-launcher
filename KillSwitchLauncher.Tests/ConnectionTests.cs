using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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

    [Theory]
    [InlineData("Jc = 4\n", "AmneziaWG")]
    [InlineData("", "")]
    public void Amnezia_IsRefused(string extra, string hint)
    {
        var conf = SampleConnections.WireGuard.Replace("MTU = 1420", "MTU = 1420\n" + extra);
        if (hint.Length == 0)
        {
            WireGuardConf.Parse(conf);
            return;
        }

        Assert.Contains(hint, Assert.Throws<ConnectionParseException>(() => WireGuardConf.Parse(conf)).Message);
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
        Assert.Contains("подписка", ConnectionTester.Explain(new List<string> { "INFO started" }));
    }

    [Fact]
    public void CountryName_FromCode()
    {
        Assert.False(string.IsNullOrEmpty(ConnectionTester.CountryName("DE")));
        Assert.Null(ConnectionTester.CountryName("XX"));
        Assert.Null(ConnectionTester.CountryName(null));
    }
}
