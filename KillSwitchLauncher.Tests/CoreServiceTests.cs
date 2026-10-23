using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KillSwitchLauncher.Services;
using Xunit;
using Svc = SafeLauncher.Service;

namespace KillSwitchLauncher.Tests;

/// <summary>Настройки ядра, которые служба собирает из запроса лаунчера, и что она отказывается запускать.</summary>
public class CoreConfigBuilderTests
{
    private static JsonObject Socks(string tag = "vpn") => new()
    {
        ["type"] = "socks",
        ["tag"] = tag,
        ["server"] = "127.0.0.1",
        ["server_port"] = 1080
    };

    private static Svc.CoreRequest Request(params string[] directDomains) => new()
    {
        Outbounds = { Socks() },
        Apps = { new Svc.CoreAppRoute { Tag = "discord", Outbound = "vpn", DirectDomains = directDomains.ToList() } }
    };

    private static JsonArray Rules(JsonObject config) => config["route"]!["rules"]!.AsArray();

    [Fact]
    public void AppGoesThroughItsConnection_EverythingElseDirect()
    {
        var config = Svc.CoreConfigBuilder.Build(Request());

        Assert.Equal("direct", (string)config["route"]!["final"]!);
        var rules = Rules(config);
        Assert.Equal("sniff", (string)rules[0]!["action"]!);
        var appRule = rules.Last()!;
        Assert.Equal("route", (string)appRule["action"]!);
        Assert.Equal("vpn", (string)appRule["outbound"]!);
        Assert.Equal(@"^launcher:discord\|", (string)appRule["process_path_regex"]![0]!);

        var outbounds = config["outbounds"]!.AsArray();
        Assert.Equal("direct", (string)outbounds[0]!["type"]!);
        Assert.Contains(outbounds, o => (string)o!["tag"]! == "vpn");
    }

    [Fact]
    public void Adapter_IsStrictAndOwnedByService()
    {
        var tun = Svc.CoreConfigBuilder.Build(Request())["inbounds"]![0]!;

        Assert.Equal("tun", (string)tun["type"]!);
        Assert.Equal(Svc.CoreConfigBuilder.InterfaceName, (string)tun["interface_name"]!);
        Assert.True((bool)tun["auto_route"]!);
        Assert.True((bool)tun["strict_route"]!);
        Assert.Single(tun["address"]!.AsArray());
    }

    [Fact]
    public void Ipv6_AddsAddressOnlyWhenAsked()
    {
        var request = Request();
        request.Ipv6 = true;

        var addresses = Svc.CoreConfigBuilder.Build(request)["inbounds"]![0]!["address"]!.AsArray();

        Assert.Equal(2, addresses.Count);
        Assert.Contains(addresses, a => ((string)a!).Contains(':'));
    }

    [Fact]
    public void DirectDomains_GoBeforeConnection()
    {
        var rules = Rules(Svc.CoreConfigBuilder.Build(Request("example.com", "cdn.example.org")));

        // 0 — распознать имя сайта, 1 — DNS-запросы отвечает ядро
        var domainRule = rules[2]!;
        Assert.Equal("direct", (string)domainRule["outbound"]!);
        Assert.Equal(new[] { "example.com", "cdn.example.org" }, domainRule["domain_suffix"]!.AsArray().Select(d => (string)d!));
        Assert.Equal("vpn", (string)rules[3]!["outbound"]!);
    }

    [Fact]
    public void NoApps_OnlyDnsAnsweredByCore()
    {
        var config = Svc.CoreConfigBuilder.Build(new Svc.CoreRequest { Outbounds = { Socks() } });

        var rule = Assert.Single(Rules(config))!;
        Assert.Equal("hijack-dns", (string)rule["action"]!);
    }

    [Fact]
    public void AppDns_GoesThroughItsConnection()
    {
        var config = Svc.CoreConfigBuilder.Build(Request());

        var server = config["dns"]!["servers"]!.AsArray().Single(s => (string)s!["tag"]! == "dns-vpn")!;
        Assert.Equal("vpn", (string)server["detour"]!);
        Assert.Equal(Svc.CoreConfigBuilder.RemoteDnsServer, (string)server["server"]!);
        Assert.Equal("tcp", (string)server["type"]!);
        var rule = config["dns"]!["rules"]!.AsArray().Last()!;
        Assert.Equal("dns-vpn", (string)rule["server"]!);
        Assert.Equal("local", (string)config["dns"]!["final"]!);
    }

    [Fact]
    public void OwnDns_FromConnection_IsUsedThroughTunnel()
    {
        var request = Request();
        request.OutboundDns = new Dictionary<string, string> { ["vpn"] = "10.0.0.53" };

        var server = Svc.CoreConfigBuilder.Build(request)["dns"]!["servers"]!.AsArray().Single(s => (string)s!["tag"]! == "dns-vpn")!;

        Assert.Equal(("udp", "10.0.0.53", "vpn"), ((string)server["type"]!, (string)server["server"]!, (string)server["detour"]!));
    }

    [Theory]
    [InlineData("corp.local")]
    [InlineData("10.0.0.53:53")]
    [InlineData("10.0.0.53 ")]
    public void OwnDns_NotAnAddress_IsRefused(string value)
    {
        var request = Request();
        request.OutboundDns = new Dictionary<string, string> { ["vpn"] = value };

        Assert.Throws<Svc.CoreConfigException>(() => Svc.CoreConfigBuilder.Build(request));
    }

    [Fact]
    public void HideDns_Off_NothingForOtherPrograms()
    {
        var config = Svc.CoreConfigBuilder.Build(Request());

        Assert.DoesNotContain(config["dns"]!["servers"]!.AsArray(), s => (string)s!["type"]! == "fakeip");
        Assert.DoesNotContain(Rules(config), r => r!["port"] is not null);
    }

    [Fact]
    public void HideDns_SystemQueriesGetSubstituteAddresses()
    {
        var request = Request();
        request.HideDns = true;

        var config = Svc.CoreConfigBuilder.Build(request);

        // любой DNS-запрос на порт 53 распознаётся и отвечается ядром
        var rules = Rules(config);
        var sniff = rules.Single(r => r!["port"] is not null)!;
        Assert.Equal(("sniff", 53), ((string)sniff["action"]!, (int)sniff["port"]![0]!));
        Assert.True(rules.IndexOf(sniff) < rules.IndexOf(rules.Single(r => (string)r!["action"]! == "hijack-dns")));

        var dns = config["dns"]!;
        var fakeIp = dns["servers"]!.AsArray().Single(s => (string)s!["type"]! == "fakeip")!;
        Assert.Equal(Svc.CoreConfigBuilder.FakeIpRange4, (string)fakeIp["inet4_range"]!);
        Assert.Null(fakeIp["inet6_range"]); // без IPv6 в сети — без подменных IPv6

        var dnsRules = dns["rules"]!.AsArray();
        // свои правила приложений — раньше подмены: их запросы идут через подключение
        var appRule = dnsRules.First(r => r!["process_path_regex"] is not null)!;
        var fakeRule = dnsRules.Single(r => (string?)r!["server"] == Svc.CoreConfigBuilder.FakeIpDnsTag)!;
        Assert.True(dnsRules.IndexOf(appRule) < dnsRules.IndexOf(fakeRule));
        Assert.Equal(new[] { "A", "AAAA" }, fakeRule["query_type"]!.AsArray().Select(t => (string)t!));
        Assert.Equal("dns", (string)fakeRule["protocol"]![0]!); // поиск адресов самим ядром — настоящий
        Assert.Equal(1, (int)fakeRule["rewrite_ttl"]!);

        // проверка интернета Windows, локальные и однословные имена — по-настоящему, и раньше подмены
        var excluded = dnsRules.Single(r => r!["domain_suffix"] is { } d && d.AsArray().Any(x => (string)x! == "msftconnecttest.com"))!;
        Assert.True(dnsRules.IndexOf(excluded) < dnsRules.IndexOf(fakeRule));
        Assert.Equal("local", (string)excluded["server"]!);

        // записи HTTPS/SVCB не выдают имя провайдеру
        var https = dnsRules.Single(r => (string)r!["action"]! == "predefined")!;
        Assert.Contains("HTTPS", https["query_type"]!.AsArray().Select(t => (string)t!));
        Assert.True(dnsRules.IndexOf(https) < dnsRules.IndexOf(fakeRule));
    }

    [Fact]
    public void HideDns_WithIpv6_AlsoSubstitutesIpv6()
    {
        var request = Request();
        request.HideDns = true;
        request.Ipv6 = true;

        var fakeIp = Svc.CoreConfigBuilder.Build(request)["dns"]!["servers"]!.AsArray().Single(s => (string)s!["type"]! == "fakeip")!;

        Assert.Equal(Svc.CoreConfigBuilder.FakeIpRange6, (string)fakeIp["inet6_range"]!);
    }

    [Fact]
    public void HideDns_RareQueries_GoThroughChosenConnection()
    {
        var request = Request();
        request.HideDns = true;
        var rules = Svc.CoreConfigBuilder.Build(request)["dns"]!["rules"]!.AsArray();
        // без подключения для DNS последнее правило — подменные адреса, остальное уходит к DNS компьютера (final)
        Assert.Equal(Svc.CoreConfigBuilder.FakeIpDnsTag, (string)rules.Last()!["server"]!);

        request.Outbounds.Add(Socks("main"));
        request.DnsOutbound = "main";
        var dns = Svc.CoreConfigBuilder.Build(request)["dns"]!;

        // DNS-сервер подключения есть, хотя ни одно приложение через него не ходит
        var server = dns["servers"]!.AsArray().Single(s => (string)s!["tag"]! == "dns-main")!;
        Assert.Equal(("tcp", Svc.CoreConfigBuilder.RemoteDnsServer, "main"),
            ((string)server["type"]!, (string)server["server"]!, (string)server["detour"]!));
        // после подмены адресов, пустых HTTPS и служебных имён: все остальные запросы программ, но не поиск адресов самим ядром
        rules = dns["rules"]!.AsArray();
        var rare = rules.Last()!;
        Assert.Equal("dns-main", (string)rare["server"]!);
        Assert.Equal("dns", (string)rare["protocol"]![0]!);
        Assert.Null(rare["query_type"]);
        Assert.Null(rare["domain_suffix"]);
        Assert.True(rules.IndexOf(rules.Single(r => (string?)r!["server"] == Svc.CoreConfigBuilder.FakeIpDnsTag)) < rules.IndexOf(rare));
        Assert.Equal(Svc.CoreConfigBuilder.LocalDnsTag, (string)dns["final"]!);
    }

    [Fact]
    public void HideDns_RareQueries_ShareAppConnectionDns()
    {
        var request = Request();
        request.HideDns = true;
        request.DnsOutbound = "vpn";
        request.OutboundDns = new Dictionary<string, string> { ["vpn"] = "10.0.0.53" };

        var dns = Svc.CoreConfigBuilder.Build(request)["dns"]!;

        var server = Assert.Single(dns["servers"]!.AsArray(), s => ((string)s!["tag"]!).StartsWith("dns-"))!;
        Assert.Equal(("udp", "10.0.0.53"), ((string)server["type"]!, (string)server["server"]!));
        Assert.Equal("dns-vpn", (string)dns["rules"]!.AsArray().Last()!["server"]!);
    }

    [Fact]
    public void DnsConnection_WithoutHideDns_ChangesNothing()
    {
        var request = Request();
        var before = Svc.CoreConfigBuilder.Build(request).ToJsonString();

        request.DnsOutbound = "vpn";

        Assert.Equal(before, Svc.CoreConfigBuilder.Build(request).ToJsonString());
    }

    [Fact]
    public void WireGuard_IsEndpoint()
    {
        var request = Request();
        request.Outbounds[0] = new JsonObject { ["type"] = "wireguard", ["tag"] = "vpn", ["private_key"] = "x" };

        var config = Svc.CoreConfigBuilder.Build(request);

        Assert.Single(config["endpoints"]!.AsArray());
        Assert.Single(config["outbounds"]!.AsArray()); // только direct
    }

    [Fact]
    public void RequestIsNotChanged()
    {
        var request = Request("example.com");
        var before = JsonSerializer.Serialize(request, Svc.ControlProtocol.JsonOptions);

        Svc.CoreConfigBuilder.Build(request);

        Assert.Equal(before, JsonSerializer.Serialize(request, Svc.ControlProtocol.JsonOptions));
    }

    public static IEnumerable<object[]> BadRequests()
    {
        // неизвестный или опасный тип подключения
        yield return new object[] { new Svc.CoreRequest { Outbounds = { new JsonObject { ["type"] = "selector", ["tag"] = "vpn" } } } };
        yield return new object[] { new Svc.CoreRequest { Outbounds = { new JsonObject { ["tag"] = "vpn" } } } };
        // зарезервированное, недопустимое или повторяющееся имя
        yield return new object[] { new Svc.CoreRequest { Outbounds = { Socks("direct") } } };
        yield return new object[] { new Svc.CoreRequest { Outbounds = { Socks("vpn|x") } } };
        yield return new object[] { new Svc.CoreRequest { Outbounds = { Socks(), Socks() } } };
        // путь к файлу где угодно внутри подключения
        var withFile = Socks();
        withFile["tls"] = new JsonObject { ["enabled"] = true, ["certificate_path"] = @"C:\Windows\System32\config\SAM" };
        yield return new object[] { new Svc.CoreRequest { Outbounds = { withFile } } };
        // цепочка на неизвестное подключение
        var detour = Socks();
        detour["detour"] = "direct";
        yield return new object[] { new Svc.CoreRequest { Outbounds = { detour } } };
        // DNS-запросы — только через подключение из этого же запроса
        yield return new object[] { new Svc.CoreRequest { Outbounds = { Socks() }, HideDns = true, DnsOutbound = "direct" } };
        // приложение: подключения нет, метка с недопустимыми символами, повтор, плохой домен
        yield return new object[] { new Svc.CoreRequest { Outbounds = { Socks() }, Apps = { new Svc.CoreAppRoute { Tag = "a", Outbound = "other" } } } };
        yield return new object[] { new Svc.CoreRequest { Outbounds = { Socks() }, Apps = { new Svc.CoreAppRoute { Tag = "a.*", Outbound = "vpn" } } } };
        yield return new object[]
        {
            new Svc.CoreRequest
            {
                Outbounds = { Socks() },
                Apps = { new Svc.CoreAppRoute { Tag = "a", Outbound = "vpn" }, new Svc.CoreAppRoute { Tag = "a", Outbound = "vpn" } }
            }
        };
        yield return new object[]
        {
            new Svc.CoreRequest { Outbounds = { Socks() }, Apps = { new Svc.CoreAppRoute { Tag = "a", Outbound = "vpn", DirectDomains = { "bad domain\"" } } } }
        };
    }

    [Theory]
    [MemberData(nameof(BadRequests))]
    public void BadRequest_IsRefused(Svc.CoreRequest request)
    {
        Assert.Throws<Svc.CoreConfigException>(() => Svc.CoreConfigBuilder.Build(request));
    }

    [Fact]
    public void TooManyApps_IsRefused()
    {
        var request = new Svc.CoreRequest { Outbounds = { Socks() } };
        for (var i = 0; i < 300; i++)
            request.Apps.Add(new Svc.CoreAppRoute { Tag = "app" + i, Outbound = "vpn" });

        Assert.Throws<Svc.CoreConfigException>(() => Svc.CoreConfigBuilder.Build(request));
    }

    // ---- процессы программ приложения без метки (копии, запущенные мимо лаунчера) ----

    private const string Tagged = @"^launcher:discord\|";
    private const string CopyTagged = @"^launcher:discord-copy\|"; // помощники копии и программа того же издателя (метка копий)
    private const string DiscordPattern = @"(?i)^C:\\Apps\\Discord\\(?:[^\\]+\\)*Discord\.exe$";
    private const string ToolPattern = @"(?i)^D:\\Tools \(x86\)\\tool\+1\.exe$";

    private static Svc.CoreProgram Program(string folder, string name, bool subfolders = false) =>
        new() { Folder = folder, Name = name, Subfolders = subfolders };

    private static Svc.CoreRequest WithPrograms(bool block, params Svc.CoreProgram[] programs)
    {
        var request = Request("example.com");
        request.Apps[0].Programs = programs.ToList();
        request.Apps[0].BlockOutside = block;
        return request;
    }

    private static string[] Patterns(JsonNode rule) => rule["process_path_regex"]!.AsArray().Select(p => (string)p!).ToArray();

    [Fact]
    public void OutsideCopies_StoreApp_AnyVersionOfItsPackage()
    {
        // Приложение Store, обновляясь, переезжает в папку новой версии и перезапускается оттуда мимо лаунчера.
        var config = Svc.CoreConfigBuilder.Build(WithPrograms(true,
            Program(@"C:\Program Files\WindowsApps\Claude_1.0.100.0_x64__pzs8sxrjxfjjc\app", "claude.exe", subfolders: true)));

        var patterns = Patterns(Rules(config).Single(r => (string)r!["action"]! == "reject")!);
        Assert.Equal(CopyTagged, patterns[^1]);
        var pattern = Assert.Single(patterns[..^1]);
        Assert.Equal(@"(?i)^C:\\Program Files\\WindowsApps\\Claude_\d+(?:\.\d+){3}_[^\\_]*_[^\\_]*_pzs8sxrjxfjjc\\app\\(?:[^\\]+\\)*claude\.exe$", pattern);
        Assert.Matches(pattern, @"C:\Program Files\WindowsApps\Claude_1.0.100.0_x64__pzs8sxrjxfjjc\app\claude.exe");
        Assert.Matches(pattern, @"C:\Program Files\WindowsApps\Claude_1.2.0.0_x64__pzs8sxrjxfjjc\app\claude.exe");
        Assert.DoesNotMatch(pattern, @"C:\Program Files\WindowsApps\Claude_1.2.0.0_x64__aaaaaaaaaaaaa\app\claude.exe");
        Assert.DoesNotMatch(pattern, @"C:\Program Files\WindowsApps\Other_1.2.0.0_x64__pzs8sxrjxfjjc\app\claude.exe");
    }

    [Fact]
    public void OutsideCopies_GoLikeTheApp()
    {
        var config = Svc.CoreConfigBuilder.Build(WithPrograms(false,
            Program(@"C:\Apps\Discord", "Discord.exe", subfolders: true), Program(@"D:\Tools (x86)\", "tool+1.exe")));

        var rules = Rules(config);
        // имя сайта распознаётся и у копий: без него для них не сработали бы исключения доменов
        Assert.Equal(new[] { "^launcher:", DiscordPattern, ToolPattern, CopyTagged }, Patterns(rules[0]!));
        var exceptions = rules.Single(r => r!["domain_suffix"] is not null)!;
        Assert.Equal("direct", (string)exceptions["outbound"]!);
        Assert.Equal(new[] { Tagged, DiscordPattern, ToolPattern, CopyTagged }, Patterns(exceptions));
        var route = rules.Last()!;
        Assert.Equal("vpn", (string)route["outbound"]!);
        Assert.Equal(new[] { Tagged, DiscordPattern, ToolPattern, CopyTagged }, Patterns(route));
        Assert.DoesNotContain(rules, r => (string)r!["action"]! == "reject");

        var dns = config["dns"]!["rules"]!.AsArray().Last()!;
        Assert.Equal("dns-vpn", (string)dns["server"]!);
        Assert.Equal(new[] { Tagged, DiscordPattern, ToolPattern, CopyTagged }, Patterns(dns));
    }

    [Fact]
    public void OutsideCopies_Blocked_RightAfterTheApp()
    {
        var config = Svc.CoreConfigBuilder.Build(WithPrograms(true, Program(@"C:\Apps\Discord", "Discord.exe", subfolders: true)));

        var rules = Rules(config);
        Assert.Equal(new[] { "^launcher:" }, Patterns(rules[0]!));
        var route = rules.Single(r => (string?)r!["outbound"] == "vpn")!;
        Assert.Equal(new[] { Tagged }, Patterns(route));
        var reject = rules.Last()!;
        Assert.Equal("reject", (string)reject["action"]!);
        Assert.Equal(new[] { DiscordPattern, CopyTagged }, Patterns(reject));
        Assert.Equal(rules.IndexOf(route) + 1, rules.IndexOf(reject));
        Assert.All(config["dns"]!["rules"]!.AsArray().Where(r => r!["process_path_regex"] is not null),
            r => Assert.Equal(new[] { Tagged }, Patterns(r!)));
    }

    [Fact]
    public void OutsideCopies_SystemProgramsAndWholeDrive_NotTouched()
    {
        var services = new[] { @"C:\Program Files\Vendor\bin\agent.exe" };
        var request = WithPrograms(false,
            Program(@"C:\Windows\System32", "notepad.exe"),
            Program(@"C:\Tools", "svchost.exe"),
            Program(@"C:\", "app.exe", subfolders: true), // весь диск: так «копией» оказалась бы любая программа с этим именем
            Program(@"C:\Program Files\Vendor", "agent.exe", subfolders: true), // программа службы
            Program(@"D:\", "tool.exe"));

        Assert.Equal(new[] { Tagged, @"(?i)^D:\\tool\.exe$", CopyTagged }, Patterns(Rules(Svc.CoreConfigBuilder.Build(request, null, services)).Last()!));

        // не осталось ни одной программы — как без программ, и блокировать нечего
        request.Apps[0].Programs!.RemoveAt(4);
        request.Apps[0].BlockOutside = true;
        var rules = Rules(Svc.CoreConfigBuilder.Build(request, null, services));
        Assert.Equal(new[] { Tagged }, Patterns(rules.Last()!));
        Assert.DoesNotContain(rules, r => (string)r!["action"]! == "reject");
    }

    [Theory]
    [InlineData(@"C:\Apps\Discord\Discord.exe", true)]
    [InlineData(@"c:\apps\discord\app-1.0.9\DISCORD.EXE", true)]
    [InlineData(@"C:\Apps\Discord2\Discord.exe", false)]
    [InlineData(@"C:\Apps\Discord\Discord.exe.old", false)]
    [InlineData(@"C:\Apps\Discord\Update.exe", false)]
    [InlineData(@"launcher:a1|C:\Apps\Discord\Discord.exe", false)]
    [InlineData(@"launcher:discord|C:\Apps\Helper\helper.exe", false)]
    [InlineData(@"launcher:discord-copy|C:\Users\me\AppData\Local\Helper\helper.exe", true)]
    [InlineData(@"launcher:discord-copyx|C:\Users\me\AppData\Local\Helper\helper.exe", false)]
    [InlineData(@"D:\Tools (x86)\tool+1.exe", true)]
    [InlineData(@"D:\Tools (x86)\sub\tool+1.exe", false)]
    [InlineData(@"D:\Tools (x86)\toolx1.exe", false)]
    public void OutsidePatterns_MatchOnlyAppPrograms(string path, bool expected)
    {
        var route = Rules(Svc.CoreConfigBuilder.Build(WithPrograms(false,
            Program(@"C:\Apps\Discord", "Discord.exe", subfolders: true), Program(@"D:\Tools (x86)", "tool+1.exe")))).Last()!;

        // Выражения ядра (Go RE2): эти конструкции .NET понимает так же.
        Assert.Equal(expected, Patterns(route).Skip(1).Any(p => Regex.IsMatch(path, p, RegexOptions.CultureInvariant)));
    }

    public static IEnumerable<object[]> BadPrograms()
    {
        yield return new object[] { @"Apps\Discord", "Discord.exe" }; // не полный путь
        yield return new object[] { @"\\server\share", "Discord.exe" }; // сетевая папка
        yield return new object[] { @"C:\Apps\..\Windows", "Discord.exe" };
        yield return new object[] { @"C:\Apps\Dis*", "Discord.exe" };
        yield return new object[] { "C:\\Apps\\Discord\n", "Discord.exe" };
        yield return new object[] { @"C:\Apps\Discord", "Discord" }; // не exe
        yield return new object[] { @"C:\Apps\Discord", ".exe" };
        yield return new object[] { @"C:\Apps\Discord", @"app\Discord.exe" }; // путь вместо имени
    }

    [Theory]
    [MemberData(nameof(BadPrograms))]
    public void OutsideCopies_BadProgram_IsRefused(string folder, string name)
    {
        Assert.Throws<Svc.CoreConfigException>(() => Svc.CoreConfigBuilder.Build(WithPrograms(false, Program(folder, name))));
    }

    [Fact]
    public void OutsideCopies_TooManyPrograms_IsRefused()
    {
        var programs = Enumerable.Range(0, 40).Select(i => Program($@"C:\Apps\App{i}", "app.exe")).ToArray();

        Assert.Throws<Svc.CoreConfigException>(() => Svc.CoreConfigBuilder.Build(WithPrograms(false, programs)));
    }

    [Theory]
    [InlineData("FATAL[0000] start service: start clash-server: external controller listen error: listen tcp 127.0.0.1:45678: bind: Only one usage of each socket address (protocol/network address/port) is normally permitted.", true)]
    [InlineData("FATAL[0000] start service: start inbound/mixed[check-in]: listen tcp 127.0.0.1:45679: bind: address already in use", true)]
    [InlineData("FATAL[0000] start service: start inbound/mixed[check-in]: listen tcp 127.0.0.1:4567: bind: address already in use", false)]
    [InlineData("FATAL[0000] start service: start inbound/tun[tun-in]: configure tun interface: Access is denied.", false)]
    [InlineData("ERROR outbound/socks[vpn]: dial tcp 127.0.0.1:45678: connectex: No connection could be made", false)]
    public void PortTaken_OnlyCoreOwnPorts(string line, bool expected)
    {
        Assert.Equal(expected, Svc.ControlServer.PortTaken(new[] { "INFO network: updated default interface", line }, new Svc.CoreApi(45678, "ключ", 45679)));
    }
}

/// <summary>Лаунчер и служба понимают сообщения друг друга (у каждого своё описание тех же полей).</summary>
public class CoreServiceProtocolTests
{
    [Fact]
    public void StartCommand_ReachesServiceIntact()
    {
        var message = new CoreServiceClient.Message
        {
            Id = 7,
            Op = Svc.ControlProtocol.OpStart,
            OwnerPipe = OwnerPipeServer.NamePrefix + new string('a', 32),
            Request = new CoreStartRequest
            {
                Outbounds = { new JsonObject { ["type"] = "vless", ["tag"] = "vpn", ["server"] = "example.com", ["server_port"] = 443 } },
                Apps = { new CoreAppRoute { Tag = "discord", Outbound = "vpn", DirectDomains = { "example.com" } } },
                Ipv6 = true
            }
        };

        var json = JsonSerializer.Serialize(message, CoreServiceClient.JsonOptions);
        var received = JsonSerializer.Deserialize<Svc.ControlMessage>(json, Svc.ControlProtocol.JsonOptions)!;

        Assert.Equal(7, received.Id);
        Assert.Equal("start", received.Op);
        Assert.Equal(message.OwnerPipe, received.OwnerPipe);
        Assert.True(received.Request!.Ipv6);
        Assert.Equal("example.com", (string)received.Request.Outbounds[0]["server"]!);
        var app = Assert.Single(received.Request.Apps);
        Assert.Equal(("discord", "vpn"), (app.Tag, app.Outbound));
        Assert.Equal(new[] { "example.com" }, app.DirectDomains);

        // и служба соглашается собрать из него настройки
        Svc.CoreConfigBuilder.Build(received.Request);
    }

    [Fact]
    public void Replies_ReachLauncherIntact()
    {
        var reply = new Svc.ControlReply { Id = 3, Ok = false, Error = "нет", CorePid = 42, Graceful = true, Log = new() { "строка" } };
        var exited = new Svc.ControlReply { Event = Svc.ControlProtocol.EventCoreExited, ExitCode = 1 };

        var received = JsonSerializer.Deserialize<CoreServiceClient.Reply>(JsonSerializer.Serialize(reply, Svc.ControlProtocol.JsonOptions), CoreServiceClient.JsonOptions)!;
        var receivedEvent = JsonSerializer.Deserialize<CoreServiceClient.Reply>(JsonSerializer.Serialize(exited, Svc.ControlProtocol.JsonOptions), CoreServiceClient.JsonOptions)!;

        Assert.Equal((3, false, "нет", 42, true), (received.Id!.Value, received.Ok, received.Error, received.CorePid!.Value, received.Graceful!.Value));
        Assert.Equal(new[] { "строка" }, received.Log);
        Assert.Null(receivedEvent.Id);
        Assert.Equal("coreExited", receivedEvent.Event);
        Assert.Equal(1, receivedEvent.ExitCode);
    }

    [Fact]
    public void Protocol_ReachesLauncher_AndMatchesRequirement()
    {
        var reply = new Svc.ControlReply { Id = 1, Ok = true, Protocol = Svc.ControlProtocol.Revision, ApiPort = 9090, ApiSecret = "ключ", CheckPort = 9091 };
        var received = JsonSerializer.Deserialize<CoreServiceClient.Reply>(JsonSerializer.Serialize(reply, Svc.ControlProtocol.JsonOptions), CoreServiceClient.JsonOptions)!;

        Assert.Equal(Svc.ControlProtocol.Revision, received.Protocol);
        Assert.Equal((9090, "ключ", 9091), (received.ApiPort!.Value, received.ApiSecret, received.CheckPort!.Value));
        // лаунчер требует не больше, чем умеет служба из этого же репозитория
        Assert.True(CoreServiceClient.RequiredProtocol <= Svc.ControlProtocol.Revision);
    }

    [Fact]
    public void OldService_HasNoProtocol()
    {
        // служба до поля Protocol: в ответе status его нет
        var received = JsonSerializer.Deserialize<CoreServiceClient.Reply>("{\"id\":1,\"ok\":true,\"running\":false,\"version\":\"2.0.0\"}", CoreServiceClient.JsonOptions)!;

        Assert.Null(received.Protocol);
        Assert.True((received.Protocol ?? 1) < CoreServiceClient.RequiredProtocol);
    }

    [Fact]
    public void StartCommand_CarriesDnsSettings()
    {
        var message = new CoreServiceClient.Message
        {
            Op = Svc.ControlProtocol.OpStart,
            Request = new CoreStartRequest { HideDns = true, OutboundDns = new Dictionary<string, string> { ["vpn"] = "10.0.0.1" }, DnsOutbound = "vpn" }
        };

        var received = JsonSerializer.Deserialize<Svc.ControlMessage>(JsonSerializer.Serialize(message, CoreServiceClient.JsonOptions),
            Svc.ControlProtocol.JsonOptions)!;

        Assert.True(received.Request!.HideDns);
        Assert.Equal("10.0.0.1", received.Request.OutboundDns!["vpn"]);
        Assert.Equal("vpn", received.Request.DnsOutbound);
        // без подключения для DNS поля в сообщении нет: служба до ревизии 6 его и не ждёт
        Assert.DoesNotContain("dnsOutbound", JsonSerializer.Serialize(new CoreStartRequest { HideDns = true }, CoreServiceClient.JsonOptions));
    }

    [Fact]
    public void StartCommand_CarriesAppPrograms()
    {
        var message = new CoreServiceClient.Message
        {
            Op = Svc.ControlProtocol.OpStart,
            Request = new CoreStartRequest
            {
                Outbounds = { new JsonObject { ["type"] = "socks", ["tag"] = "vpn", ["server"] = "127.0.0.1", ["server_port"] = 1080 } },
                Apps =
                {
                    new CoreAppRoute
                    {
                        Tag = "discord",
                        Outbound = "vpn",
                        Programs = new List<CoreProgram> { new() { Folder = @"C:\Users\u\AppData\Local\Discord", Name = "Discord.exe", Subfolders = true } },
                        BlockOutside = true
                    },
                    new CoreAppRoute { Tag = "chat", Outbound = "vpn" }
                }
            }
        };

        var json = JsonSerializer.Serialize(message, CoreServiceClient.JsonOptions);
        var received = JsonSerializer.Deserialize<Svc.ControlMessage>(json, Svc.ControlProtocol.JsonOptions)!;

        var app = received.Request!.Apps.Single(a => a.Tag == "discord");
        Assert.True(app.BlockOutside);
        var program = Assert.Single(app.Programs!);
        Assert.Equal((@"C:\Users\u\AppData\Local\Discord", "Discord.exe", true), (program.Folder, program.Name, program.Subfolders));
        Assert.Null(received.Request.Apps.Single(a => a.Tag == "chat").Programs);
        // у приложения без программ поля в сообщении нет
        Assert.Single(Regex.Matches(json, "\"programs\""));

        // и служба соглашается собрать из него настройки
        Assert.Contains(Svc.CoreConfigBuilder.Build(received.Request)["route"]!["rules"]!.AsArray(), r => (string)r!["action"]! == "reject");
    }

    [Fact]
    public void GuardCommand_ReachesServiceIntact()
    {
        var message = new CoreServiceClient.Message
        {
            Id = 5,
            Op = Svc.ControlProtocol.OpGuard,
            Guard = new GuardRequest
            {
                Apps =
                {
                    new GuardRule { Paths = { @"C:\Apps\Чат\chat.exe" }, ViaCore = true, LoopbackPorts = new List<int> { 10808, 10809 } },
                    new GuardRule { Paths = { @"C:\Games\game.exe" }, Interfaces = { 12, 31 } }
                },
                DnsInterfaces = new List<int> { 12 },
                DnsPaths = new List<string> { @"C:\Games\game.exe" }
            }
        };

        var json = JsonSerializer.Serialize(message, CoreServiceClient.JsonOptions);
        Assert.DoesNotContain("isEmpty", json);
        var received = JsonSerializer.Deserialize<Svc.ControlMessage>(json, Svc.ControlProtocol.JsonOptions)!;

        Assert.Equal(("guard", 5), (received.Op, received.Id));
        Assert.Equal(2, received.Guard!.Apps.Count);
        Assert.Equal(@"C:\Apps\Чат\chat.exe", received.Guard.Apps[0].Paths.Single());
        Assert.True(received.Guard.Apps[0].ViaCore);
        Assert.Equal(new[] { 10808, 10809 }, received.Guard.Apps[0].LoopbackPorts);
        Assert.Equal(new[] { 12, 31 }, received.Guard.Apps[1].Interfaces);
        Assert.Null(received.Guard.Apps[1].LoopbackPorts);
        Assert.DoesNotContain("loopbackPorts\":null", json);
        Assert.Equal(new[] { 12 }, received.Guard.DnsInterfaces);
        Assert.Equal(new[] { @"C:\Games\game.exe" }, received.Guard.DnsPaths);
    }

    [Fact]
    public void GuardWarning_ReachesLauncher()
    {
        var reply = new Svc.ControlReply { Id = 2, Ok = true, Warning = "замечание", Unguarded = new() { @"\\nas\apps\app.exe" } };

        var received = JsonSerializer.Deserialize<CoreServiceClient.Reply>(JsonSerializer.Serialize(reply, Svc.ControlProtocol.JsonOptions), CoreServiceClient.JsonOptions)!;

        Assert.Equal("замечание", received.Warning);
        Assert.Equal(new[] { @"\\nas\apps\app.exe" }, received.Unguarded);
    }

    [Fact]
    public void Names_Match()
    {
        Assert.Equal(Svc.ControlProtocol.ServiceName, CoreServiceClient.ServiceName);
        Assert.Equal(Svc.ControlProtocol.PipeName, CoreServiceClient.PipeName);
        Assert.StartsWith("SafeLauncher-Owner-", OwnerPipeServer.NamePrefix);
    }
}
