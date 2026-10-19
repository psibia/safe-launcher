using System;
using System.Collections.Generic;
using System.IO;
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

/// <summary>Настройки и приложения для тестов подключений лаунчера.</summary>
internal static class NetworkSamples
{
    public const string Trojan = "trojan://secret@5.6.7.8:443?sni=example.com#Сервер";

    public static VpnConnection Link(string name, string link = Trojan) => new() { Name = name, Kind = ConnectionKind.Link, Link = link };

    public static AppEntry App(string name, AppNetworkMode mode = AppNetworkMode.Inherit, Guid? connection = null,
        ProtectionOverride closeOnDrop = ProtectionOverride.Inherit, params string[] domains) => new()
    {
        Name = name,
        ExePath = $@"C:\Apps\{name}\{name}.exe",
        Network = new AppNetwork
        {
            Mode = mode,
            ConnectionId = connection,
            CloseOnDrop = closeOnDrop,
            DirectDomains = domains.ToList()
        }
    };

    /// <summary>Общее подключение лаунчера и приложения «как в общих».</summary>
    public static AppSettings Settings(VpnConnection connection, params AppEntry[] apps)
    {
        var settings = new AppSettings
        {
            NetworkMode = GlobalNetworkMode.LauncherConnection,
            ConnectionId = connection.Id,
            Connections = { connection }
        };
        settings.Apps.AddRange(apps);
        return settings;
    }
}

public class CorePlanTests
{
    [Fact]
    public void AppsThroughLauncher_GetTagsAndSharedConnection()
    {
        var connection = NetworkSamples.Link("Германия");
        var discord = NetworkSamples.App("Discord", domains: "Example.COM");
        var telegram = NetworkSamples.App("Telegram");
        var plan = CorePlan.Build(NetworkSamples.Settings(connection, discord, telegram), ipv6: false);

        Assert.Single(plan.Request.Outbounds);
        Assert.Equal(CorePlan.ConnectionTag(connection.Id), (string)plan.Request.Outbounds[0]["tag"]!);
        Assert.Equal(2, plan.Request.Apps.Count);
        Assert.Equal(CorePlan.AppTag(discord.Id), plan.Apps[discord.Id].Tag);
        Assert.Equal(new[] { "example.com" }, plan.Request.Apps.Single(a => a.Tag == plan.Apps[discord.Id].Tag).DirectDomains);
        Assert.Empty(plan.Problems);
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", plan.Apps[discord.Id].Tag);
    }

    [Fact]
    public void SystemVpnApps_AreNotInCore()
    {
        var connection = NetworkSamples.Link("Германия");
        var game = NetworkSamples.App("Игра", AppNetworkMode.OwnAdapters);
        var settings = NetworkSamples.Settings(connection, game);

        var plan = CorePlan.Build(settings, ipv6: false);

        Assert.Empty(plan.Apps);
        Assert.Empty(plan.Request.Outbounds);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void MissingOrBrokenConnection_IsAProblemNotACrash()
    {
        var broken = NetworkSamples.Link("Сломанное", "vless://нет");
        var good = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A", AppNetworkMode.OwnConnection, broken.Id);
        var b = NetworkSamples.App("B", AppNetworkMode.OwnConnection, Guid.NewGuid());
        var c = NetworkSamples.App("C", AppNetworkMode.OwnConnection, good.Id);
        var settings = NetworkSamples.Settings(good, a, b, c);
        settings.Connections.Add(broken);

        var plan = CorePlan.Build(settings, ipv6: false);

        Assert.True(plan.Problems.ContainsKey(a.Id));
        Assert.Contains("не выбрано подключение", plan.Problems[b.Id]);
        Assert.True(plan.Apps.ContainsKey(c.Id));
        Assert.Single(plan.Request.Outbounds);
    }

    [Fact]
    public void Fingerprint_ChangesOnlyWithCoreSettings()
    {
        var connection = NetworkSamples.Link("Германия");
        var app = NetworkSamples.App("Discord");
        var settings = NetworkSamples.Settings(connection, app);
        var first = CorePlan.Build(settings, ipv6: false);

        Assert.Equal(first.Fingerprint, CorePlan.Build(settings, ipv6: false).Fingerprint);

        app.Network.DirectDomains.Add("example.com");
        Assert.NotEqual(first.Fingerprint, CorePlan.Build(settings, ipv6: false).Fingerprint);
    }

    [Fact]
    public void Fingerprint_DoesNotDependOnTileOrder()
    {
        var vless = NetworkSamples.Link("VLESS", SampleConnections.VlessReality);
        var wireGuard = new VpnConnection { Name = "WireGuard", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.WireGuard };
        var amnezia = new VpnConnection { Name = "AmneziaWG", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.AmneziaWg };
        var settings = NetworkSamples.Settings(vless,
            NetworkSamples.App("A", AppNetworkMode.OwnConnection, wireGuard.Id),
            NetworkSamples.App("B", AppNetworkMode.OwnConnection, amnezia.Id),
            NetworkSamples.App("C", domains: "example.com"));
        settings.Connections.Add(wireGuard);
        settings.Connections.Add(amnezia);
        var before = CorePlan.Build(settings, ipv6: false);
        Assert.Equal(3, before.Request.Outbounds.Count);

        // Перетаскивание плиток меняет порядок приложений в настройках.
        settings.Apps.Reverse();
        settings.Connections.Reverse();

        Assert.Equal(before.Fingerprint, CorePlan.Build(settings, ipv6: false).Fingerprint);
    }

    [Fact]
    public void SameWireGuardTwice_OneTunnel()
    {
        // Один и тот же файл, добавленный дважды: для сервера это один клиент, два туннеля отбивали бы друг у друга связь.
        var first = new VpnConnection { Name = "Дом", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.AmneziaWg };
        var copy = new VpnConnection { Name = "Дом 2", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.AmneziaWg };
        var other = new VpnConnection { Name = "WireGuard", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.WireGuard };
        var settings = NetworkSamples.Settings(NetworkSamples.Link("VLESS", SampleConnections.VlessReality),
            NetworkSamples.App("A", AppNetworkMode.OwnConnection, first.Id),
            NetworkSamples.App("B", AppNetworkMode.OwnConnection, copy.Id),
            NetworkSamples.App("C", AppNetworkMode.OwnConnection, other.Id),
            NetworkSamples.App("D"));
        settings.Connections.Add(first);
        settings.Connections.Add(copy);
        settings.Connections.Add(other);

        var plan = CorePlan.Build(settings, ipv6: false);

        var a = CorePlan.ConnectionTag(first.Id);
        var b = CorePlan.ConnectionTag(copy.Id);
        var shared = string.CompareOrdinal(a, b) < 0 ? a : b;
        Assert.Equal(shared, plan.ConnectionTags[first.Id]);
        Assert.Equal(shared, plan.ConnectionTags[copy.Id]);
        string Outbound(string app) => plan.Request.Apps.Single(r => r.Tag == plan.Apps.Values.Single(x => x.Name == app).Tag).Outbound;
        Assert.Equal(shared, Outbound("A"));
        Assert.Equal(shared, Outbound("B"));
        Assert.Equal(CorePlan.ConnectionTag(other.Id), Outbound("C"));
        Assert.Equal(3, plan.Request.Outbounds.Count);
        Assert.Single(plan.Request.Outbounds, o => (string?)o["tag"] == shared);
        Assert.All(plan.Request.OutboundDns ?? new Dictionary<string, string>(),
            dns => Assert.Contains(plan.Request.Outbounds, o => (string?)o["tag"] == dns.Key));

        settings.Apps.Reverse();
        settings.Connections.Reverse();
        Assert.Equal(plan.Fingerprint, CorePlan.Build(settings, ipv6: false).Fingerprint);

        var request = new Svc.CoreRequest
        {
            Outbounds = plan.Request.Outbounds,
            Apps = plan.Request.Apps.Select(r => new Svc.CoreAppRoute { Tag = r.Tag, Outbound = r.Outbound, DirectDomains = r.DirectDomains }).ToList(),
            OutboundDns = plan.Request.OutboundDns
        };
        Assert.Single(Svc.CoreConfigBuilder.Build(request, new Svc.CoreApi(9090, "ключ"))["endpoints"]!.AsArray(),
            e => (string?)e!["tag"] == shared);
    }

    [Fact]
    public void SameWireGuardKey_OtherSettingsDiffer_OneTunnel()
    {
        // Тот же ключ, но в копии файла поменяли MTU: сервер всё равно видит одного клиента, туннель должен быть один.
        var first = new VpnConnection { Name = "Дом", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.WireGuard };
        var edited = new VpnConnection
        {
            Name = "Дом 2", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.WireGuard.Replace("MTU = 1420", "MTU = 1280")
        };
        var settings = NetworkSamples.Settings(NetworkSamples.Link("VLESS", SampleConnections.VlessReality),
            NetworkSamples.App("A", AppNetworkMode.OwnConnection, first.Id),
            NetworkSamples.App("B", AppNetworkMode.OwnConnection, edited.Id));
        settings.Connections.Add(first);
        settings.Connections.Add(edited);

        var plan = CorePlan.Build(settings, ipv6: false);

        Assert.Equal(plan.ConnectionTags[first.Id], plan.ConnectionTags[edited.Id]);
        Assert.Single(plan.Request.Outbounds, o => (string?)o["type"] == "wireguard");
    }

    [Fact]
    public void ServiceAcceptsThePlan()
    {
        var connection = NetworkSamples.Link("Германия");
        var settings = NetworkSamples.Settings(connection, NetworkSamples.App("Discord", domains: "example.com"), NetworkSamples.App("Telegram"));
        var plan = CorePlan.Build(settings, ipv6: true);

        var request = new Svc.CoreRequest
        {
            Outbounds = plan.Request.Outbounds,
            Apps = plan.Request.Apps.Select(a => new Svc.CoreAppRoute { Tag = a.Tag, Outbound = a.Outbound, DirectDomains = a.DirectDomains }).ToList(),
            Ipv6 = plan.Request.Ipv6
        };
        var config = Svc.CoreConfigBuilder.Build(request, new Svc.CoreApi(9090, "ключ"));

        Assert.Equal(2, config["dns"]!["rules"]!.AsArray().Count(r => (string)r!["server"]! != Svc.CoreConfigBuilder.LocalDnsTag));
    }

    [Fact]
    public void HideDns_RareQueriesThroughMainConnection()
    {
        var main = NetworkSamples.Link("Германия");
        var settings = NetworkSamples.Settings(main, NetworkSamples.App("Discord"));
        Assert.Null(CorePlan.Build(settings, ipv6: false).Request.DnsOutbound);

        settings.HideDns = true;
        var plan = CorePlan.Build(settings, ipv6: false);

        Assert.Equal(CorePlan.ConnectionTag(main.Id), plan.Request.DnsOutbound);
        Assert.Equal("Германия", plan.DnsConnectionName);
        Assert.Single(plan.Request.Outbounds);
    }

    [Fact]
    public void HideDns_MainConnectionJoinsCore_EvenIfNoAppUsesIt()
    {
        // Все приложения идут через свои подключения: основное попадает в ядро только ради DNS.
        var main = NetworkSamples.Link("Германия");
        var own = NetworkSamples.Link("Своё", SampleConnections.VlessReality);
        var settings = NetworkSamples.Settings(main, NetworkSamples.App("Discord", AppNetworkMode.OwnConnection, own.Id));
        settings.Connections.Add(own);
        var before = CorePlan.Build(settings, ipv6: false);
        Assert.Single(before.Request.Outbounds);

        settings.HideDns = true;
        var plan = CorePlan.Build(settings, ipv6: false);

        Assert.Equal(CorePlan.ConnectionTag(main.Id), plan.Request.DnsOutbound);
        Assert.Equal(2, plan.Request.Outbounds.Count);
        Assert.Equal(CorePlan.ConnectionTag(own.Id), plan.Request.Apps.Single().Outbound);
        Assert.Single(plan.Apps);
        Assert.Contains("редкие DNS-запросы: DNS компьютера → через «Германия»", CorePlanDiff.Describe(before, plan));

        // и служба собирает из такого плана настройки ядра
        var request = new Svc.CoreRequest
        {
            Outbounds = plan.Request.Outbounds,
            Apps = plan.Request.Apps.Select(a => new Svc.CoreAppRoute { Tag = a.Tag, Outbound = a.Outbound, DirectDomains = a.DirectDomains }).ToList(),
            HideDns = plan.Request.HideDns,
            OutboundDns = plan.Request.OutboundDns,
            DnsOutbound = plan.Request.DnsOutbound
        };
        var dns = Svc.CoreConfigBuilder.Build(request, new Svc.CoreApi(9090, "ключ"))["dns"]!;
        Assert.Equal("dns-" + plan.Request.DnsOutbound, (string)dns["rules"]!.AsArray().Last()!["server"]!);
    }

    [Theory]
    [InlineData("системный VPN")]
    [InlineData("нет приложений через ядро")]
    [InlineData("основное не выбрано")]
    [InlineData("основное не разобрать")]
    public void HideDns_WithoutMainConnection_RareQueriesAsBefore(string situation)
    {
        var main = NetworkSamples.Link("Германия");
        var own = NetworkSamples.Link("Своё", SampleConnections.VlessReality);
        var app = NetworkSamples.App("Discord", AppNetworkMode.OwnConnection, own.Id);
        var settings = NetworkSamples.Settings(main, app);
        settings.Connections.Add(own);
        settings.HideDns = true;
        switch (situation)
        {
            case "системный VPN":
                settings.NetworkMode = GlobalNetworkMode.SystemVpn;
                break;
            case "нет приложений через ядро":
                app.Network.Mode = AppNetworkMode.OwnAdapters;
                break;
            case "основное не выбрано":
                settings.ConnectionId = null;
                break;
            default:
                main.Link = "vless://нет";
                break;
        }

        var plan = CorePlan.Build(settings, ipv6: false);

        Assert.Null(plan.Request.DnsOutbound);
        Assert.Null(plan.DnsConnectionName);
        Assert.DoesNotContain(plan.Request.Outbounds, o => (string?)o["tag"] == CorePlan.ConnectionTag(main.Id));
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void HideDns_MainIsSameWireGuardAsApp_OneTunnel()
    {
        var main = new VpnConnection { Name = "Дом", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.AmneziaWg };
        var copy = new VpnConnection { Name = "Дом 2", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.AmneziaWg };
        var settings = NetworkSamples.Settings(main, NetworkSamples.App("A", AppNetworkMode.OwnConnection, copy.Id));
        settings.Connections.Add(copy);
        settings.HideDns = true;

        var plan = CorePlan.Build(settings, ipv6: false);

        var tunnel = (string)Assert.Single(plan.Request.Outbounds)["tag"]!;
        Assert.Equal(tunnel, plan.Request.DnsOutbound);
        Assert.Equal(tunnel, plan.Request.Apps.Single().Outbound);
    }

    /// <summary>Программы приложения без файловой системы: папка C:\Apps\имя, exe по имени.</summary>
    internal static IReadOnlyList<CoreProgram> ProgramsOf(AppEntry app) =>
        new[] { new CoreProgram { Folder = $@"C:\Apps\{app.Name}", Name = app.Name + ".exe", Subfolders = true } };

    [Fact]
    public void OutsideCopies_ProgramsGoWithTheApp()
    {
        var connection = NetworkSamples.Link("Германия");
        var settings = NetworkSamples.Settings(connection, NetworkSamples.App("Discord"));

        var route = CorePlan.Build(settings, false, ProgramsOf).Request.Apps.Single();
        Assert.Equal(@"C:\Apps\Discord", Assert.Single(route.Programs!).Folder);
        Assert.False(route.BlockOutside);

        settings.OutsideCopies = OutsideCopies.Block;
        route = CorePlan.Build(settings, false, ProgramsOf).Request.Apps.Single();
        Assert.Single(route.Programs!);
        Assert.True(route.BlockOutside);

        settings.OutsideCopies = OutsideCopies.Allow;
        var asked = 0;
        route = CorePlan.Build(settings, false, app => { asked++; return ProgramsOf(app); }).Request.Apps.Single();
        Assert.Null(route.Programs);
        Assert.False(route.BlockOutside);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void OutsideCopies_ViaLauncher_CoreLeadsCopiesLikeVpn()
    {
        // Пока лаунчер не взял копию под контроль, ядро ведёт её через подключение приложения, как при «Через VPN».
        var connection = NetworkSamples.Link("Германия");
        var settings = NetworkSamples.Settings(connection, NetworkSamples.App("Discord"));
        var vpn = CorePlan.Build(settings, false, ProgramsOf);

        settings.OutsideCopies = OutsideCopies.Launcher;
        var launcher = CorePlan.Build(settings, false, ProgramsOf);

        var route = launcher.Request.Apps.Single();
        Assert.Single(route.Programs!);
        Assert.False(route.BlockOutside);
        Assert.Equal(vpn.Fingerprint, launcher.Fingerprint);
    }

    [Fact]
    public void OutsideCopies_ProgramOfSystemVpnApp_NotTaken()
    {
        // Одна программа на двух плитках: через ядро и под системным VPN. Её процессы без метки — не обязательно копии
        // приложения через ядро, и уводить их в его подключение нельзя.
        var connection = NetworkSamples.Link("Германия");
        var viaCore = NetworkSamples.App("Chat");
        var other = NetworkSamples.App("Discord");
        var plan = CorePlan.Build(NetworkSamples.Settings(connection, viaCore, NetworkSamples.App("Chat", AppNetworkMode.OwnAdapters), other), false, ProgramsOf);

        Assert.Null(plan.Request.Apps.Single(r => r.Tag == CorePlan.AppTag(viaCore.Id)).Programs);
        Assert.Single(plan.Request.Apps.Single(r => r.Tag == CorePlan.AppTag(other.Id)).Programs!);
    }

    [Fact]
    public void OutsideCopies_ChangeIsDescribed()
    {
        var connection = NetworkSamples.Link("Германия");
        var settings = NetworkSamples.Settings(connection, NetworkSamples.App("Discord"));
        var vpn = CorePlan.Build(settings, false, ProgramsOf);
        settings.OutsideCopies = OutsideCopies.Block;
        var block = CorePlan.Build(settings, false, ProgramsOf);
        settings.OutsideCopies = OutsideCopies.Allow;
        var allow = CorePlan.Build(settings, false, ProgramsOf);
        settings.OutsideCopies = OutsideCopies.Vpn;
        var moved = CorePlan.Build(settings, false,
            app => new[] { ProgramsOf(app)[0], new CoreProgram { Folder = @"D:\Discord", Name = "Discord.exe" } });

        Assert.NotEqual(vpn.Fingerprint, block.Fingerprint);
        Assert.Equal("копии приложений мимо лаунчера: через VPN → без сети", CorePlanDiff.Describe(vpn, block));
        Assert.Equal("копии приложений мимо лаунчера: без сети → напрямую", CorePlanDiff.Describe(block, allow));
        Assert.Equal("«Discord»: программы для копий мимо лаунчера 1 → 2", CorePlanDiff.Describe(vpn, moved));
    }

    [Fact]
    public void OutsideCopies_AppOverridesGlobal()
    {
        var connection = NetworkSamples.Link("Германия");
        var blocked = NetworkSamples.App("Discord");
        blocked.Network.OutsideCopies = OutsideCopiesOverride.Block;
        var allowed = NetworkSamples.App("Telegram");
        allowed.Network.OutsideCopies = OutsideCopiesOverride.Allow;
        var inherit = NetworkSamples.App("Chat");
        var settings = NetworkSamples.Settings(connection, blocked, allowed, inherit);

        var request = CorePlan.Build(settings, false, ProgramsOf).Request;
        CoreAppRoute Route(AppEntry app) => request.Apps.Single(r => r.Tag == CorePlan.AppTag(app.Id));
        Assert.Single(Route(blocked).Programs!);
        Assert.True(Route(blocked).BlockOutside);
        Assert.Null(Route(allowed).Programs);
        Assert.False(Route(allowed).BlockOutside);
        Assert.Single(Route(inherit).Programs!);
        Assert.False(Route(inherit).BlockOutside);

        // Общее «Разрешить» не трогает приложение со своим значением.
        settings.OutsideCopies = OutsideCopies.Allow;
        request = CorePlan.Build(settings, false, ProgramsOf).Request;
        Assert.True(Route(blocked).BlockOutside);
        Assert.Null(Route(inherit).Programs);

        // Своё «Через VPN» при общем «Разрешить»: программы других приложений под системным VPN всё так же не в счёт.
        blocked.Network.OutsideCopies = OutsideCopiesOverride.Vpn;
        settings.Apps.Add(NetworkSamples.App("Discord", AppNetworkMode.OwnAdapters));
        request = CorePlan.Build(settings, false, ProgramsOf).Request;
        Assert.Null(Route(blocked).Programs);
    }

    [Fact]
    public void OfflineApp_NotInCore_ItsProgramsNotTaken()
    {
        // «Без сети» — не через ядро. Та же программа у приложения через ядро: её процессы без метки — не обязательно его копии.
        var connection = NetworkSamples.Link("Германия");
        var viaCore = NetworkSamples.App("Chat");
        var offline = NetworkSamples.App("Chat");
        offline.Network.Mode = AppNetworkMode.Offline;

        var plan = CorePlan.Build(NetworkSamples.Settings(connection, viaCore, offline), false, ProgramsOf);

        var route = Assert.Single(plan.Request.Apps);
        Assert.Equal(CorePlan.AppTag(viaCore.Id), route.Tag);
        Assert.Null(route.Programs);
        Assert.False(plan.Apps.ContainsKey(offline.Id));
        Assert.False(plan.Problems.ContainsKey(offline.Id));
        Assert.Empty(CorePlan.Build(NetworkSamples.Settings(connection, offline), false, ProgramsOf).Request.Apps);
    }

    [Fact]
    public void OutsideCopies_AppChangeIsDescribed()
    {
        var connection = NetworkSamples.Link("Германия");
        var discord = NetworkSamples.App("Discord");
        var settings = NetworkSamples.Settings(connection, discord, NetworkSamples.App("Telegram"));
        var vpn = CorePlan.Build(settings, false, ProgramsOf);
        discord.Network.OutsideCopies = OutsideCopiesOverride.Block;
        var discordBlocked = CorePlan.Build(settings, false, ProgramsOf);
        settings.OutsideCopies = OutsideCopies.Block;
        var allBlocked = CorePlan.Build(settings, false, ProgramsOf);

        Assert.Equal("«Discord»: копии мимо лаунчера через VPN → без сети", CorePlanDiff.Describe(vpn, discordBlocked));
        Assert.Equal("«Telegram»: копии мимо лаунчера через VPN → без сети", CorePlanDiff.Describe(discordBlocked, allBlocked));
        Assert.Equal("копии приложений мимо лаунчера: через VPN → без сети", CorePlanDiff.Describe(vpn, allBlocked));
    }

    [Fact]
    public void Programs_AppFolderAndItsSubfolders()
    {
        using var dir = new TempDir();
        var exe = dir.File("Telegram Desktop", "Telegram.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");

        var programs = CorePlan.Programs(new AppEntry { ExePath = exe });

        Assert.Contains(programs, p => string.Equals(p.Folder, Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase) && p.Subfolders);
        Assert.All(programs, p => Assert.Equal("Telegram.exe", p.Name));
    }

    [Fact]
    public void Programs_StoreAppsAndEngines_None()
    {
        Assert.Empty(CorePlan.Programs(new AppEntry { ExePath = @"C:\Program Files\WindowsApps\Vendor.App_1.0_x64\app.exe", AppUserModelId = "Vendor.App_abc!App" }));
        Assert.Empty(CorePlan.Programs(new AppEntry { ExePath = @"C:\Program Files\Java\jre\bin\javaw.exe" }));
        Assert.Empty(CorePlan.Programs(new AppEntry { ExePath = "" }));
    }
}

public class CoreConfigDnsTests
{
    private static Svc.CoreRequest Request(params string[] domains) => new()
    {
        Outbounds = { new JsonObject { ["type"] = "socks", ["tag"] = "vpn", ["server"] = "127.0.0.1", ["server_port"] = 1080 } },
        Apps = { new Svc.CoreAppRoute { Tag = "discord", Outbound = "vpn", DirectDomains = domains.ToList() } }
    };

    [Fact]
    public void AppTraffic_GoesToSiteByName()
    {
        var sniff = Svc.CoreConfigBuilder.Build(Request())["route"]!["rules"]![0]!;

        Assert.Equal("sniff", (string)sniff["action"]!);
        Assert.True((bool)sniff["override_destination"]!);
        Assert.Equal("^launcher:", (string)sniff["process_path_regex"]![0]!);
    }

    [Fact]
    public void AppDns_GoesThroughItsConnection_ExceptionsLocally()
    {
        var dns = Svc.CoreConfigBuilder.Build(Request("example.com"))["dns"]!;

        var servers = dns["servers"]!.AsArray();
        Assert.Equal(Svc.CoreConfigBuilder.LocalDnsTag, (string)dns["final"]!);
        var remote = servers.Single(s => (string)s!["tag"]! == "dns-vpn")!;
        Assert.Equal("vpn", (string)remote["detour"]!);
        Assert.Equal("tcp", (string)remote["type"]!);

        var rules = dns["rules"]!.AsArray();
        Assert.Equal(Svc.CoreConfigBuilder.LocalDnsTag, (string)rules[0]!["server"]!);
        Assert.Equal("example.com", (string)rules[0]!["domain_suffix"]![0]!);
        Assert.Equal("dns-vpn", (string)rules[1]!["server"]!);
        Assert.Equal(@"^launcher:discord\|", (string)rules[1]!["process_path_regex"]![0]!);
    }

    [Fact]
    public void Api_OnlyOnLoopbackWithSecret()
    {
        var api = Svc.CoreConfigBuilder.Build(Request(), new Svc.CoreApi(45678, "ключ"))["experimental"]!["clash_api"]!;

        Assert.Equal("127.0.0.1:45678", (string)api["external_controller"]!);
        Assert.Equal("ключ", (string)api["secret"]!);
        Assert.Null(Svc.CoreConfigBuilder.Build(Request())["experimental"]);
    }

    [Fact]
    public void CheckProxy_OnLoopbackWithKey_UserChoosesConnection()
    {
        var request = Request();
        request.Outbounds.Add(new JsonObject { ["type"] = "wireguard", ["tag"] = "wg", ["address"] = new JsonArray("10.0.0.2/32"), ["private_key"] = "k" });
        var config = Svc.CoreConfigBuilder.Build(request, new Svc.CoreApi(45678, "ключ", 45679));

        var check = config["inbounds"]!.AsArray().Single(i => (string?)i!["tag"] == Svc.CoreConfigBuilder.CheckInboundTag)!;
        Assert.Equal(("mixed", "127.0.0.1", 45679), ((string)check["type"]!, (string)check["listen"]!, (int)check["listen_port"]!));
        Assert.Equal(new[] { "vpn:ключ", "wg:ключ" }, check["users"]!.AsArray().Select(u => $"{u!["username"]}:{u["password"]}"));

        // Правила проверки — первыми: соединение проверки не попадёт под правила приложений и DNS.
        var rules = config["route"]!["rules"]!.AsArray();
        Assert.All(rules.Take(3), r => Assert.Equal(Svc.CoreConfigBuilder.CheckInboundTag, (string)r!["inbound"]![0]!));
        Assert.Equal(("vpn", "vpn"), ((string)rules[0]!["auth_user"]![0]!, (string)rules[0]!["outbound"]!));
        Assert.Equal(("wg", "wg"), ((string)rules[1]!["auth_user"]![0]!, (string)rules[1]!["outbound"]!));
        Assert.Equal("reject", (string)rules[2]!["action"]!);
        Assert.Null(rules[2]!["auth_user"]);

        Assert.Single(Svc.CoreConfigBuilder.Build(Request(), new Svc.CoreApi(45678, "ключ"))["inbounds"]!.AsArray());
        Assert.Single(Svc.CoreConfigBuilder.Build(new Svc.CoreRequest(), new Svc.CoreApi(45678, "ключ", 45679))["inbounds"]!.AsArray());
    }
}

public class LauncherNetworkTests
{
    private sealed class FakeBackend : ICoreBackend
    {
        public int Starts;
        public int Stops;
        public bool FailStart;
        public Func<string, int?> Delay = _ => 40;
        public CoreStartRequest? Last;

        public event Action<string, IReadOnlyList<string>>? Lost;

        public Task StartAsync(CoreStartRequest request, CancellationToken cancellation)
        {
            Starts++;
            Last = request;
            return FailStart ? Task.FromException(new CoreServiceException("не вышло")) : Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellation)
        {
            Stops++;
            return Task.CompletedTask;
        }

        public int Probes;
        public List<string> CoreLog = new();

        public Task<CoreProbe> ProbeAsync(string outboundTag, TimeSpan timeout, CancellationToken cancellation)
        {
            Probes++;
            var delay = Delay(outboundTag);
            return Task.FromResult(delay is null ? CoreProbe.Failed("ядро не смогло связаться с сервером") : new CoreProbe(delay, null));
        }

        public Task<IReadOnlyList<string>> LogTailAsync(CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<string>>(CoreLog);

        /// <summary>Соединения через ядро; null — ядро не ответило. Тест заменяет список целиком (проверка идёт в другом потоке).</summary>
        public volatile List<CoreConnection>? Connections = new();
        public int ConnectionQueries;

        public Task<CoreConnectionList> ConnectionsAsync(TimeSpan timeout, CancellationToken cancellation)
        {
            Interlocked.Increment(ref ConnectionQueries);
            var connections = Connections;
            return Task.FromResult(connections is null ? CoreConnectionList.Failed("нет ответа") : new CoreConnectionList(connections, null));
        }

        public void Crash() => Lost?.Invoke("упало", new List<string>());

        public CoreCheckProxy? CheckProxy { get; set; } = new(45679, "ключ");

        public bool Disposed;

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeProcesses : IAppProcesses
    {
        public readonly HashSet<Guid> Running = new();
        public readonly List<Guid> Killed = new();
        public readonly List<Guid> Blocked = new();
        public int Frozen;
        public int Thawed;
        public readonly List<Guid> FrozenApps = new();

        public IReadOnlyList<Guid> RunningApps()
        {
            lock (Running)
                return Running.ToList();
        }

        public IDisposable Freeze(IReadOnlyCollection<Guid> appIds)
        {
            Frozen++;
            FrozenApps.AddRange(appIds);
            return new Thaw(this);
        }

        public KillReport KillApps(IReadOnlyCollection<Guid> appIds, bool blockRelaunch)
        {
            var names = new List<string>();
            lock (Running)
            {
                foreach (var id in appIds.Where(Running.Contains).ToList())
                {
                    Running.Remove(id);
                    Killed.Add(id);
                    names.Add(id.ToString());
                    if (blockRelaunch)
                        Blocked.Add(id);
                }
            }

            return new KillReport(names, Array.Empty<string>());
        }

        public void StopBlocking(Guid? appId = null)
        {
            lock (Running)
                Blocked.RemoveAll(id => appId is null || id == appId);
        }

        private sealed class Thaw : IDisposable
        {
            private readonly FakeProcesses _owner;

            public Thaw(FakeProcesses owner) => _owner = owner;

            public void Dispose() => _owner.Thawed++;
        }
    }

    private readonly FakeBackend _backend = new();
    private readonly FakeProcesses _processes = new();

    private LauncherNetwork Network(TimeSpan? idle = null) =>
        new(_processes, () => _backend, idle ?? TimeSpan.FromHours(1), TimeSpan.FromHours(1))
        {
            ProbeRetryPause = TimeSpan.FromMilliseconds(10),
            QuickCheckDelay = Timeout.InfiniteTimeSpan,
            NetworkAvailable = () => true
        };

    /// <summary>Строка журнала ядра, которое не видит сеть, со временем <paramref name="at"/> (как её пишет служба).</summary>
    private static string NoRoute(DateTime at) =>
        at.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) +
        " ERROR [7 0ms] connection: open connection to 1.2.3.4:443 using outbound/vless[c1]: dial tcp 5.6.7.8:8443: no route to internet";

    [Fact]
    public void CoreSeesNoNetwork_OnlyFreshNoRouteLines()
    {
        var since = new DateTime(2026, 10, 19, 15, 52, 50);
        Assert.True(LauncherNetwork.CoreSeesNoNetwork(new[] { "15:52:49.900 INFO x", NoRoute(since.AddSeconds(2)) }, since));
        Assert.True(LauncherNetwork.CoreSeesNoNetwork(new[] { NoRoute(since.AddMilliseconds(-500)) }, since)); // секунда в запас
        Assert.False(LauncherNetwork.CoreSeesNoNetwork(new[] { NoRoute(since.AddSeconds(-20)) }, since)); // сети не было раньше
        Assert.False(LauncherNetwork.CoreSeesNoNetwork(new[] { "15:52:55.000 ERROR dial tcp: i/o timeout" }, since));
        Assert.False(LauncherNetwork.CoreSeesNoNetwork(new[] { "no route to internet" }, since));

        // Проверка началась до полуночи, строка — после.
        var late = new DateTime(2026, 10, 19, 23, 59, 59);
        Assert.True(LauncherNetwork.CoreSeesNoNetwork(new[] { NoRoute(late.AddSeconds(2)) }, late));
    }

    [Fact]
    public async Task BlindCore_LaunchRestartsCore_ThenLaunches()
    {
        // Компьютер уснул сразу после отключения кабеля: ядро не заметило, что сеть вернулась, и на всё отвечает
        // «no route to internet». Сервер при этом работает: после перезапуска ядра он отвечает.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var b = NetworkSamples.App("B");
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a, b), false));
        Assert.Null(await network.PrepareLaunchAsync(a.Id));
        _processes.Running.Add(a.Id);

        _backend.Delay = _ => _backend.Starts >= 2 ? 40 : null;
        _backend.CoreLog.Add(NoRoute(DateTime.Now.AddSeconds(30)));
        var failed = false;
        network.ProbeFailed += _ => failed = true;

        Assert.Null(await network.PrepareLaunchAsync(b.Id));
        Assert.Equal(2, _backend.Starts);
        Assert.Contains(a.Id, _processes.FrozenApps); // работающее приложение на время перезапуска приостановлено
        Assert.Equal(_processes.Frozen, _processes.Thawed);
        Assert.False(failed);
    }

    [Fact]
    public async Task BlindCore_HealthCheck_RestartsInsteadOfMiss_ThenGivesUp()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A", closeOnDrop: ProtectionOverride.On);
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        Assert.Null(await network.PrepareLaunchAsync(a.Id));
        _processes.Running.Add(a.Id);
        ConnectionDownInfo? down = null;
        network.ConnectionDown += info => down = info;

        _backend.Delay = _ => null;
        _backend.CoreLog.Add(NoRoute(DateTime.Now.AddSeconds(30)));

        // Два перезапуска за 5 минут: промахи не засчитываются, приложение работает.
        await network.CheckHealthAsync();
        await network.CheckHealthAsync();
        Assert.Equal(3, _backend.Starts);
        Assert.Empty(_processes.Killed);
        Assert.Null(down);

        // Перезапуск не помогает: дальше как при обрыве.
        await network.CheckHealthAsync();
        await network.CheckHealthAsync();
        Assert.Equal(3, _backend.Starts);
        Assert.Equal(new[] { a.Id }, _processes.Killed);
        Assert.False(down!.NoNetwork);
    }

    [Fact]
    public async Task NoNetworkOnComputer_NoRestart_PlainMessage()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network();
        network.NetworkAvailable = () => false;
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        _backend.Delay = _ => null;
        _backend.CoreLog.Add(NoRoute(DateTime.Now.AddSeconds(30)));
        ProbeFailure? failure = null;
        network.ProbeFailed += f => failure = f;

        Assert.Contains("не подключён к сети", await network.PrepareLaunchAsync(a.Id));
        Assert.Equal(1, _backend.Starts);
        Assert.Equal("компьютер не подключён к сети", failure!.LocalCause);
    }

    [Fact]
    public async Task OldNoRouteLines_ServerBlamed_NoRestart()
    {
        // Сети не было минуту назад, теперь она есть, а сервер не отвечает: ядро ни при чём.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        _backend.Delay = _ => null;
        _backend.CoreLog.Add(NoRoute(DateTime.Now.AddMinutes(-1)));
        ProbeFailure? failure = null;
        network.ProbeFailed += f => failure = f;

        Assert.Contains("не отвечает", await network.PrepareLaunchAsync(a.Id));
        Assert.Equal(1, _backend.Starts);
        Assert.Null(failure!.LocalCause);
    }

    [Fact]
    public async Task QuickCheck_NoMisses_ButServerBackIsNoticed()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A", closeOnDrop: ProtectionOverride.Off);
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        Assert.Null(await network.PrepareLaunchAsync(a.Id));
        _processes.Running.Add(a.Id);
        string? up = null;
        network.ConnectionUp += (_, name) => up = name;

        _backend.Delay = _ => null;
        for (var i = 0; i < 3; i++)
            await network.CheckHealthAsync(quick: true);
        Assert.Equal(CoreAppState.Protected, network.StateOf(a.Id)); // сеть после смены появляется не сразу

        await network.CheckHealthAsync();
        await network.CheckHealthAsync();
        Assert.Equal(CoreAppState.Waiting, network.StateOf(a.Id));

        _backend.Delay = _ => 50;
        await network.CheckHealthAsync(quick: true);
        Assert.Equal("Германия", up);
        Assert.Equal(CoreAppState.Protected, network.StateOf(a.Id));
    }

    [Fact]
    public async Task FirstLaunch_StartsCore_SecondDoesNotRestart()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var b = NetworkSamples.App("B");
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a, b), false));

        Assert.Null(await network.PrepareLaunchAsync(a.Id));
        _processes.Running.Add(a.Id);
        Assert.Null(await network.PrepareLaunchAsync(b.Id));

        Assert.Equal(1, _backend.Starts);
        Assert.Equal(0, _processes.Frozen);
        Assert.Equal(CoreAppState.Protected, network.StateOf(a.Id));
        Assert.Equal(CorePlan.AppTag(b.Id), network.TagFor(b.Id));
    }

    [Fact]
    public async Task NewSettings_RestartWhileRunningAppsArePaused()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);

        a.Network.DirectDomains.Add("example.com");
        network.Update(CorePlan.Build(settings, false));
        Assert.Null(await network.PrepareLaunchAsync(a.Id));

        Assert.Equal(2, _backend.Starts);
        Assert.Equal(1, _processes.Frozen);
        Assert.Equal(1, _processes.Thawed);
    }

    [Fact]
    public async Task OtherChanges_WaitWhileAppsRun()
    {
        // Работает приложение через WireGuard. Запуск приложения через VLESS не перезапускает ядро (перезапуск оборвал бы
        // соединения WireGuard), хотя с тех пор изменилось то, что запускаемого не касается. Здесь проверяется только запуск:
        // в лаунчере добавленное приложение с копиями мимо лаунчера применяется сразу (KeepRunningAsync).
        var vless = NetworkSamples.Link("VLESS", SampleConnections.VlessReality);
        var wireGuard = new VpnConnection { Name = "WireGuard", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.WireGuard };
        var wg = NetworkSamples.App("WG", AppNetworkMode.OwnConnection, wireGuard.Id);
        var browser = NetworkSamples.App("Браузер", AppNetworkMode.OwnConnection, vless.Id);
        var other = NetworkSamples.App("Другое", AppNetworkMode.OwnConnection, vless.Id);
        var settings = NetworkSamples.Settings(vless, wg, browser, other);
        settings.Connections.Add(wireGuard);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false));
        Assert.Null(await network.PrepareLaunchAsync(wg.Id));
        _processes.Running.Add(wg.Id);

        // В сети появился IPv6, у другого приложения новые исключения, добавлено приложение, плитки переставлены.
        other.Network.DirectDomains.Add("example.com");
        var added = NetworkSamples.App("Новое", AppNetworkMode.OwnConnection, wireGuard.Id);
        settings.Apps.Add(added);
        settings.Apps.Reverse();
        network.Update(CorePlan.Build(settings, ipv6: true));

        Assert.Null(await network.PrepareLaunchAsync(browser.Id));
        Assert.Equal(1, _backend.Starts);
        Assert.Equal(0, _processes.Frozen);
        Assert.Equal(CorePlan.AppTag(browser.Id), network.TagFor(browser.Id));
        Assert.Null(network.TagFor(added.Id));
        _processes.Running.Add(browser.Id);

        // Новому приложению нынешних настроек ядра не хватает: перезапуск, и с ним остальные изменения.
        Assert.Null(await network.PrepareLaunchAsync(added.Id));
        Assert.Equal(2, _backend.Starts);
        Assert.Equal(1, _processes.Frozen);
        Assert.True(_backend.Last!.Ipv6);
        Assert.Equal(CorePlan.AppTag(added.Id), network.TagFor(added.Id));
    }

    [Theory]
    [InlineData("сервер подключения")]
    [InlineData("скрывать DNS")]
    public async Task ChangesForLaunchedApp_Restart(string change)
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var b = NetworkSamples.App("B");
        var settings = NetworkSamples.Settings(connection, a, b);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);

        if (change == "скрывать DNS")
            settings.HideDns = true;
        else
            connection.Link = "trojan://secret@5.6.7.9:443?sni=example.com#Другой";
        network.Update(CorePlan.Build(settings, false));

        Assert.Null(await network.PrepareLaunchAsync(b.Id));
        Assert.Equal(2, _backend.Starts);
        Assert.Equal(1, _processes.Frozen);
    }

    [Fact]
    public async Task MainConnectionOnlyForDns_ChangeWaitsWhileAppsRun()
    {
        var main = NetworkSamples.Link("Германия");
        var own = NetworkSamples.Link("Своё", SampleConnections.VlessReality);
        var a = NetworkSamples.App("A", AppNetworkMode.OwnConnection, own.Id);
        var b = NetworkSamples.App("B", AppNetworkMode.OwnConnection, own.Id);
        var settings = NetworkSamples.Settings(main, a, b);
        settings.Connections.Add(own);
        settings.HideDns = true;
        using var network = Network();
        network.Update(CorePlan.Build(settings, false));
        Assert.Null(await network.PrepareLaunchAsync(a.Id));
        _processes.Running.Add(a.Id);
        Assert.Equal(CorePlan.ConnectionTag(main.Id), _backend.Last!.DnsOutbound);

        // Через основное подключение идут только редкие DNS-запросы: из-за смены его сервера обрывать A незачем.
        main.Link = "trojan://secret@5.6.7.9:443?sni=example.com#Другой";
        network.Update(CorePlan.Build(settings, false));

        Assert.Null(await network.PrepareLaunchAsync(b.Id));
        Assert.Equal(1, _backend.Starts);
        Assert.Equal(0, _processes.Frozen);
    }

    [Fact]
    public async Task CheckRoute_WireGuardOfRunningCore_ThroughCore()
    {
        // Второй туннель с ключом, который уже работает в ядре, отбил бы у ядра связь с сервером: такой WireGuard
        // (и его копия в другом подключении) проверяется через ядро. Остальное — отдельной копией ядра, как раньше.
        var home = new VpnConnection { Name = "Дом", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.AmneziaWg };
        var copy = new VpnConnection { Name = "Дом 2", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.AmneziaWg };
        var other = new VpnConnection { Name = "WireGuard", Kind = ConnectionKind.WireGuard, WireGuardConfig = SampleConnections.WireGuard };
        var vless = NetworkSamples.Link("VLESS", SampleConnections.VlessReality);
        var app = NetworkSamples.App("A", AppNetworkMode.OwnConnection, home.Id);
        var browser = NetworkSamples.App("Браузер");
        var settings = NetworkSamples.Settings(vless, app, browser);
        settings.Connections.Add(home);
        settings.Connections.Add(copy);
        settings.Connections.Add(other);
        JsonObject Server(VpnConnection connection) => ConnectionOutbound.Resolve(connection).Outbound;
        using var network = Network();
        network.Update(CorePlan.Build(settings, false));

        Assert.Null(network.CheckRouteFor(Server(home)));

        Assert.Null(await network.PrepareLaunchAsync(app.Id));
        var route = network.CheckRouteFor(Server(home));
        Assert.Equal(CorePlan.ConnectionTag(home.Id), route?.Tag);
        Assert.Equal(_backend.CheckProxy, route?.Proxy);
        Assert.Equal(route, network.CheckRouteFor(Server(copy)));
        Assert.Null(network.CheckRouteFor(Server(other)));
        Assert.Null(network.CheckRouteFor(Server(vless)));

        _backend.CheckProxy = null; // служба старше протокола 7
        Assert.Null(network.CheckRouteFor(Server(home)));
        _backend.CheckProxy = new CoreCheckProxy(45679, "ключ");

        await network.StopAsync();
        Assert.Null(network.CheckRouteFor(Server(home)));
    }

    [Fact]
    public async Task NothingRunning_NextLaunchTakesNewSettings()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var b = NetworkSamples.App("B");
        var settings = NetworkSamples.Settings(connection, a, b);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false));
        await network.PrepareLaunchAsync(a.Id);

        // A уже закрыто, ядро ещё не выключилось, других программ через него нет: обрывать нечего, и ядро сразу берёт новые настройки.
        network.Update(CorePlan.Build(settings, ipv6: true));
        Assert.Null(await network.PrepareLaunchAsync(b.Id));

        Assert.Equal(2, _backend.Starts);
        Assert.Equal(0, _processes.Frozen);
        Assert.True(_backend.Last!.Ipv6);
    }

    [Fact]
    public async Task ServerDown_ClosesByCurrentSettings()
    {
        // «Закрывать при обрыве» включили, когда ядро уже работало: ядро ради этого не перезапускается,
        // но при обрыве приложение закрывается.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A", closeOnDrop: ProtectionOverride.Off);
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);

        a.Network.CloseOnDrop = ProtectionOverride.On;
        network.Update(CorePlan.Build(settings, false));
        _backend.Delay = _ => null;
        await network.CheckHealthAsync();
        await network.CheckHealthAsync();

        Assert.Equal(1, _backend.Starts);
        Assert.Equal(new[] { a.Id }, _processes.Killed);
    }

    [Fact]
    public async Task OfflineRun_NotClosedOrFrozenByCore()
    {
        // «B» по настройкам через подключение, но запущено разово без сети: ядро его не ведёт, и при обрыве сервера
        // закрывается только «A».
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A", closeOnDrop: ProtectionOverride.On);
        var b = NetworkSamples.App("B", closeOnDrop: ProtectionOverride.On);
        var settings = NetworkSamples.Settings(connection, a, b);
        var processes = new CoreAppProcesses(_processes);
        processes.SetOffline(new[] { b.Id });
        using var network = new LauncherNetwork(processes, () => _backend, TimeSpan.FromHours(1), TimeSpan.FromHours(1))
        {
            ProbeRetryPause = TimeSpan.FromMilliseconds(10)
        };
        network.Update(CorePlan.Build(settings, false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        _processes.Running.Add(b.Id);

        Assert.Equal(new[] { a.Id }, processes.RunningApps());
        _backend.Delay = _ => null;
        await network.CheckHealthAsync();
        await network.CheckHealthAsync();

        Assert.Equal(new[] { a.Id }, _processes.Killed);
        Assert.Contains(b.Id, _processes.Running);

        processes.SetOffline(Array.Empty<Guid>());
        Assert.Equal(new[] { b.Id }, processes.RunningApps());
    }

    [Fact]
    public async Task ServerNotAnswering_LaunchRefused()
    {
        var connection = NetworkSamples.Link("Германия");
        var strict = NetworkSamples.App("Строгое");
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, strict), false));
        _backend.Delay = _ => null;
        _backend.CoreLog.AddRange(new[]
        {
            "+0300 2026-10-12 20:14:50 INFO [1 0ms] outbound/vless[c1]: outbound connection to cp.cloudflare.com:443",
            "+0300 2026-10-12 20:14:50 ERROR [2 5ms] dns: exchange failed for example.org IN A: timeout",
            "+0300 2026-10-12 20:14:51 INFO sing-box started (0.4s)"
        });
        ProbeFailure? failure = null;
        network.ProbeFailed += f => failure = f;

        Assert.Contains("не отвечает", await network.PrepareLaunchAsync(strict.Id));
        Assert.Equal(4, _backend.Probes); // сразу после запуска ядра сеть может быть не готова: несколько попыток
        Assert.Equal(connection.Id, failure!.ConnectionId);
        Assert.Contains("ядро не смогло связаться", failure.Summary);
        Assert.Contains("dns: exchange failed", Assert.Single(failure.CoreLog));
    }

    [Fact]
    public async Task ServerAnswersOnRetry_LaunchAllowed()
    {
        var connection = NetworkSamples.Link("Германия");
        var app = NetworkSamples.App("A");
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, app), false));
        var calls = 0;
        _backend.Delay = _ => ++calls < 3 ? null : 70;
        var failed = false;
        network.ProbeFailed += _ => failed = true;

        Assert.Null(await network.PrepareLaunchAsync(app.Id));
        Assert.Equal(3, _backend.Probes);
        Assert.False(failed);
        Assert.Equal(CoreAppState.Protected, network.StateOf(app.Id));
    }

    [Fact]
    public void ImportantLines_ErrorsOrLastLines()
    {
        var plain = new[] { "a", "b", "c", "d" };
        Assert.Equal(new[] { "b", "c", "d" }, LauncherNetwork.ImportantLines(plain));

        var withErrors = new[] { "x WARN y", "info", "x ERROR z", "info2" };
        Assert.Equal(new[] { "x WARN y", "x ERROR z" }, LauncherNetwork.ImportantLines(withErrors));
        Assert.Empty(LauncherNetwork.ImportantLines(Array.Empty<string>()));

        // Строки о проверяемом подключении важнее ошибок чужих программ, идущих напрямую.
        var noisy = new[] { "a ERROR open connection to wpad.example:80 using outbound/direct[direct]: lookup", "endpoint/wireguard[c-1]: sending handshake", "b ERROR other" };
        Assert.Equal(new[] { "endpoint/wireguard[c-1]: sending handshake" }, LauncherNetwork.ImportantLines(noisy, "c-1"));
        Assert.Equal(new[] { "b ERROR other" }, LauncherNetwork.ImportantLines(noisy));
    }

    [Fact]
    public async Task Problem_ReturnedWithoutStartingCore()
    {
        var connection = NetworkSamples.Link("Германия");
        var app = NetworkSamples.App("A", AppNetworkMode.OwnConnection, Guid.NewGuid());
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, app), false));

        Assert.Contains("не выбрано подключение", await network.PrepareLaunchAsync(app.Id));
        Assert.Equal(0, _backend.Starts);
        Assert.True(network.IsViaCore(app.Id));
    }

    [Fact]
    public async Task ServerDown_ClosesOrWaits_ThenRecovers()
    {
        var connection = NetworkSamples.Link("Германия");
        var closing = NetworkSamples.App("Закрывается");
        var waiting = NetworkSamples.App("Ждёт", closeOnDrop: ProtectionOverride.Off);
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, closing, waiting), false));
        await network.PrepareLaunchAsync(closing.Id);
        _processes.Running.Add(closing.Id);
        _processes.Running.Add(waiting.Id);
        ConnectionDownInfo? down = null;
        string? up = null;
        network.ConnectionDown += info => down = info;
        network.ConnectionUp += (_, name) => up = name;

        _backend.Delay = _ => null;
        await network.CheckHealthAsync();
        Assert.Null(down); // один промах — ещё не обрыв
        await network.CheckHealthAsync();

        Assert.NotNull(down);
        Assert.Equal(new[] { closing.Id }, _processes.Killed);
        Assert.Equal(new[] { "Ждёт" }, down!.Waiting);
        Assert.Equal(CoreAppState.Waiting, network.StateOf(waiting.Id));

        _backend.Delay = _ => 50;
        await network.CheckHealthAsync();

        Assert.Equal("Германия", up);
        Assert.Equal(CoreAppState.Protected, network.StateOf(waiting.Id));
        Assert.Empty(_processes.Blocked);
    }

    [Fact]
    public async Task FailedRestart_ClosesPausedApps()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);

        a.Network.DirectDomains.Add("example.com");
        network.Update(CorePlan.Build(settings, false));
        _backend.FailStart = true;

        Assert.Contains("не вышло", await network.PrepareLaunchAsync(a.Id));
        Assert.Equal(new[] { a.Id }, _processes.Killed);
        Assert.False(network.IsCoreRunning);
    }

    [Fact]
    public async Task CoreStops_WhenLastAppExits_AndNoAppsThroughCoreInSettings()
    {
        // Приложение перевели на системный VPN (или убрали), пока оно работало: ядро больше не нужно его копиям.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network(TimeSpan.FromMilliseconds(100));
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection), false));

        network.OnProcessesChanged();
        await Task.Delay(400);
        Assert.Equal(0, _backend.Stops); // приложение ещё работает

        lock (_processes.Running)
            _processes.Running.Clear();
        network.OnProcessesChanged();
        Assert.True(await Wait.UntilAsync(() => _backend.Stops == 1, TimeSpan.FromSeconds(5)));
        Assert.False(network.IsCoreRunning);
    }

    [Fact]
    public async Task CoreKeptOn_WhileOtherProgramsHaveConnections()
    {
        // Пока ядро работает, через него идут и соединения других программ: выключение ядра их оборвало бы.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network(TimeSpan.FromMilliseconds(100));
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false, NoCopies));
        await network.PrepareLaunchAsync(a.Id);
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection), false, NoCopies)); // приложений через ядро в настройках не осталось
        _backend.Connections = new List<CoreConnection>
        {
            new("tcp", 443, @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            new("udp", 53, @"C:\Windows\System32\svchost.exe")
        };

        network.OnProcessesChanged(); // A закрылось
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref _backend.ConnectionQueries) >= 3, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, _backend.Stops);
        Assert.True(network.IsCoreRunning);

        // Остался только запрос DNS: он разовый, ядро выключается.
        _backend.Connections = new List<CoreConnection> { new("udp", 53, @"C:\Windows\System32\svchost.exe") };
        Assert.True(await Wait.UntilAsync(() => _backend.Stops == 1, TimeSpan.FromSeconds(5)));
        Assert.False(network.IsCoreRunning);
    }

    [Fact]
    public async Task ConnectionsUnknown_CoreStopsAsBefore()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network(TimeSpan.FromMilliseconds(100));
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false, NoCopies));
        await network.PrepareLaunchAsync(a.Id);
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection), false, NoCopies));
        _backend.Connections = null;

        network.OnProcessesChanged();

        Assert.True(await Wait.UntilAsync(() => _backend.Stops == 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void OtherPrograms_WithoutLauncherAppsAndLookups()
    {
        var all = new[]
        {
            new CoreConnection("tcp", 443, @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            new CoreConnection("udp", 3478, @"C:\Users\u\AppData\Local\Discord\app-1.0\Discord.exe"),
            new CoreConnection("tcp", 3389, ""),
            new CoreConnection("tcp", 53, @"C:\Windows\System32\svchost.exe"),
            new CoreConnection("udp", 53, @"C:\Windows\System32\svchost.exe"),
            new CoreConnection("udp", 123, @"C:\Windows\System32\svchost.exe"),
            new CoreConnection("udp", 7, Svc.CoreConfigBuilder.TagPrefix + @"a1|C:\Games\game.exe"),
            new CoreConnection("tcp", 443, @"C:\Program Files\Google\Chrome\Application\CHROME.EXE")
        };

        var others = LauncherNetwork.OthersOf(all);

        Assert.Equal(Svc.CoreConfigBuilder.TagPrefix, LauncherNetwork.LauncherProcessPrefix);
        Assert.Equal(new[] { 443, 3478, 3389, 53, 443 }, others.Select(c => c.Port));
        Assert.Equal("5: chrome.exe ×2, Discord.exe, svchost.exe, программа не определена", LauncherNetwork.Describe(others));
    }

    [Fact]
    public async Task OtherProgramsConnections_LaunchRestartsOnlyIfNeeded()
    {
        // Приложений нет, но через ядро идут соединения других программ: запуск приложения, которому хватает нынешних
        // настроек, ядро не перезапускает (перезапуск оборвал бы эти соединения).
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var b = NetworkSamples.App("B");
        var settings = NetworkSamples.Settings(connection, a, b);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false));
        await network.PrepareLaunchAsync(a.Id);
        _backend.Connections = new List<CoreConnection> { new("tcp", 443, @"C:\Program Files\Mozilla Firefox\firefox.exe") };

        network.Update(CorePlan.Build(settings, ipv6: true));
        Assert.Null(await network.PrepareLaunchAsync(b.Id));
        Assert.Equal(1, _backend.Starts);

        // Запускаемому нынешних настроек не хватает: перезапуск со всеми изменениями.
        b.Network.DirectDomains.Add("example.com");
        network.Update(CorePlan.Build(settings, ipv6: true));
        Assert.Null(await network.PrepareLaunchAsync(b.Id));
        Assert.Equal(2, _backend.Starts);
        Assert.True(_backend.Last!.Ipv6);
    }

    [Fact]
    public async Task CoreLost_NoAppsThroughCoreInSettings_NotReportedNotRestarted()
    {
        // Ядро работало без приложений (ждало, пока закроются соединения других программ), приложение уже убрали из
        // настроек, и ядро упало: сообщать не о чем, запускать заново незачем.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        await network.PrepareLaunchAsync(a.Id);
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection), false));
        var reported = false;
        network.CoreLost += (_, _) => reported = true;

        _backend.Crash();
        await Task.Delay(200);

        Assert.False(reported);
        Assert.False(network.IsCoreRunning);
        Assert.Equal(1, _backend.Starts);
    }

    [Fact]
    public async Task CoreLost_WithoutRunningApps_RestartedQuietly()
    {
        // Приложение через ядро есть в настройках, но не запущено: ядро нужно его копиям мимо лаунчера и запускается
        // заново, а сообщать пользователю не о чем.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        Assert.Null(await network.KeepRunningAsync("тест"));
        var reported = false;
        network.CoreLost += (_, _) => reported = true;
        network.CoreRecovered += (_, _) => reported = true;

        _backend.Crash();

        Assert.True(await Wait.UntilAsync(() => network.IsCoreRunning, TimeSpan.FromSeconds(5)));
        Assert.Equal(2, _backend.Starts);
        Assert.Equal(0, _processes.Frozen);
        Assert.False(reported);
    }

    [Fact]
    public async Task KeepRunning_StartsCoreForCopies_WithoutLaunch()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        settings.OutsideCopies = OutsideCopies.Block;
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));

        Assert.Null(await network.KeepRunningAsync("открыт лаунчер"));

        Assert.Equal(1, _backend.Starts);
        Assert.True(network.IsCoreRunning);
        Assert.True(network.OutsideBlocked(a.Id)); // копия по ярлыку сразу без сети
        Assert.Equal(0, _backend.Probes); // сервер проверяется только перед запуском приложения

        // Повторный вызов с теми же настройками ничего не делает, запуск приложения ядро не перезапускает.
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));
        Assert.Null(await network.PrepareLaunchAsync(a.Id));
        Assert.Equal(1, _backend.Starts);
    }

    [Fact]
    public async Task KeepRunning_NoAppsThroughCore_CoreNotStarted()
    {
        var connection = NetworkSamples.Link("Германия");
        var system = NetworkSamples.App("Под системным VPN", AppNetworkMode.OwnAdapters);
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, system), false));

        Assert.Null(await network.KeepRunningAsync("открыт лаунчер"));

        Assert.Equal(0, _backend.Starts);
        Assert.False(network.IsCoreRunning);
    }

    [Fact]
    public async Task KeepRunning_StartFails_ReasonReturned()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        _backend.FailStart = true;

        Assert.Equal("не вышло", await network.KeepRunningAsync("открыт лаунчер"));
        Assert.False(network.IsCoreRunning);

        // Запуск приложения пробует снова.
        _backend.FailStart = false;
        Assert.Null(await network.PrepareLaunchAsync(a.Id));
        Assert.True(network.IsCoreRunning);
    }

    /// <summary>У приложения нет программ для копий мимо лаунчера (копии идут напрямую при любых настройках).</summary>
    private static IReadOnlyList<CoreProgram> NoCopies(AppEntry app) => Array.Empty<CoreProgram>();

    private static readonly List<CoreConnection> ChromeConnection = new()
    {
        new("tcp", 443, @"C:\Program Files\Google\Chrome\Application\chrome.exe")
    };

    [Fact]
    public async Task KeepRunning_NewApp_CopiesGoAsChosenAtOnce()
    {
        // Добавили приложение, пока A работает: его копия по ярлыку должна сразу идти как выбрано, поэтому ядро
        // перезапускается сразу, а A на это время приостанавливается.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);

        var b = NetworkSamples.App("B");
        settings.Apps.Add(b);
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));

        Assert.Equal(2, _backend.Starts);
        Assert.Equal((1, 1), (_processes.Frozen, _processes.Thawed));
        Assert.False(network.OutsideBlocked(b.Id));
        Assert.Equal(CorePlan.AppTag(a.Id), network.TagFor(a.Id));
        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public async Task KeepRunning_NotUrgent_WaitsWhileSomethingGoesThrough()
    {
        // IPv6 и новый сервер подключения не срочно: прежние настройки работают, перезапуск оборвал бы соединения.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);

        connection.Link = "trojan://secret@5.6.7.9:443?sni=example.com#Другой";
        network.Update(CorePlan.Build(settings, ipv6: true, CorePlanTests.ProgramsOf));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));
        Assert.Equal(1, _backend.Starts);
        Assert.Equal(0, _processes.Frozen);

        // A закрылось, а через ядро идут соединения других программ. Копии A мимо лаунчера должны идти через новый сервер,
        // а запуска A, который применил бы его, может и не быть: перезапуск сразу.
        _processes.Running.Clear();
        _backend.Connections = ChromeConnection;
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));
        Assert.Equal(2, _backend.Starts);
        Assert.True(_backend.Last!.Ipv6);
    }

    [Fact]
    public async Task KeepRunning_ServerChanged_AppWithoutCopies_WaitsWhileSomethingGoesThrough()
    {
        // У A копии идут напрямую: новый сервер нужен только при запуске, перезапуск подождёт, пока через ядро ничего не идёт.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, NoCopies));
        Assert.Null(await network.KeepRunningAsync("открыт лаунчер"));
        _backend.Connections = ChromeConnection;

        connection.Link = "trojan://secret@5.6.7.9:443?sni=example.com#Другой";
        network.Update(CorePlan.Build(settings, ipv6: true, NoCopies));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));
        Assert.Equal(1, _backend.Starts);

        // Через ядро ничего не идёт: перезапуск с новыми настройками.
        _backend.Connections = new List<CoreConnection>();
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));
        Assert.Equal(2, _backend.Starts);
        Assert.True(_backend.Last!.Ipv6);
    }

    [Fact]
    public async Task KeepRunning_NotUrgent_AppliedOnceOtherProgramsClose()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network(TimeSpan.FromMilliseconds(30));
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        Assert.Null(await network.KeepRunningAsync("открыт лаунчер"));
        _backend.Connections = ChromeConnection;

        network.Update(CorePlan.Build(settings, ipv6: true, CorePlanTests.ProgramsOf));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));
        Assert.Equal(1, _backend.Starts);

        // Ядро проверяет снова само, без новых изменений настроек.
        _backend.Connections = new List<CoreConnection>();
        Assert.True(await Wait.UntilAsync(() => _backend.Starts == 2, TimeSpan.FromSeconds(5)));
        Assert.True(_backend.Last!.Ipv6);
    }

    [Theory]
    [InlineData("без интернета")]
    [InlineData("напрямую")]
    [InlineData("у приложения")]
    public async Task KeepRunning_OutsideCopiesChanged_AppliedAtOnce_EvenWithOtherPrograms(string change)
    {
        // Хром открыт по ярлыку и идёт через ядро как копия приложения: смена «Если открыто не из лаунчера» действует сразу,
        // хотя перезапуск оборвёт его соединения.
        var connection = NetworkSamples.Link("Германия");
        var chrome = NetworkSamples.App("Chrome");
        var settings = NetworkSamples.Settings(connection, chrome);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        Assert.Null(await network.KeepRunningAsync("открыт лаунчер"));
        Assert.False(network.OutsideBlocked(chrome.Id));
        _backend.Connections = ChromeConnection;

        if (change == "без интернета")
            settings.OutsideCopies = OutsideCopies.Block;
        else if (change == "напрямую")
            settings.OutsideCopies = OutsideCopies.Allow;
        else
            chrome.Network.OutsideCopies = OutsideCopiesOverride.Block;
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));

        Assert.Equal(2, _backend.Starts);
        Assert.Equal(change == "напрямую" ? null : true, network.OutsideBlocked(chrome.Id));
    }

    [Fact]
    public async Task KeepRunning_AppRemoved_CoreStopsAtOnce_EvenWithOtherPrograms()
    {
        // Хром убрали из лаунчера, а его копия по ярлыку шла через ядро: ядро выключается сразу, Хром идёт напрямую.
        var connection = NetworkSamples.Link("Германия");
        var chrome = NetworkSamples.App("Chrome");
        var settings = NetworkSamples.Settings(connection, chrome);
        using var network = Network(TimeSpan.FromMilliseconds(30));
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        Assert.Null(await network.KeepRunningAsync("открыт лаунчер"));
        _backend.Connections = ChromeConnection;

        settings.Apps.Remove(chrome);
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));

        Assert.Equal(1, _backend.Stops);
        Assert.False(network.IsCoreRunning);
        Assert.Null(network.TagFor(chrome.Id));
    }

    [Fact]
    public async Task KeepRunning_AppWithCopiesRemoved_RestartsWithoutIt_WhileAnotherRuns()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var chrome = NetworkSamples.App("Chrome");
        var settings = NetworkSamples.Settings(connection, a, chrome);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);

        settings.Apps.Remove(chrome);
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));

        Assert.Equal(2, _backend.Starts);
        Assert.Equal((1, 1), (_processes.Frozen, _processes.Thawed));
        Assert.DoesNotContain(_backend.Last!.Apps, r => r.Tag == CorePlan.AppTag(chrome.Id));
        Assert.Equal(CorePlan.AppTag(a.Id), network.TagFor(a.Id));
    }

    [Fact]
    public async Task KeepRunning_AppWithoutCopiesChanged_WaitsUntilLaunch()
    {
        // У B копии идут напрямую и B не запущено: его новые исключения нужны только при запуске.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var b = NetworkSamples.App("B");
        var settings = NetworkSamples.Settings(connection, a, b);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, NoCopies));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);

        b.Network.DirectDomains.Add("example.com");
        network.Update(CorePlan.Build(settings, false, NoCopies));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));
        Assert.Equal(1, _backend.Starts);

        Assert.Null(await network.PrepareLaunchAsync(b.Id));
        Assert.Equal(2, _backend.Starts);
        Assert.Equal(1, _processes.Frozen);
    }

    [Theory]
    [InlineData("другое подключение")]
    [InlineData("исключения")]
    [InlineData("системный VPN")]
    [InlineData("убрано")]
    public async Task KeepRunning_RunningAppChanged_AppliedAtOnce(string change)
    {
        // Условия работающего приложения поменяли: оно сразу идёт по новым (фильтры Windows тоже меняются сразу).
        var connection = NetworkSamples.Link("Германия");
        var other = NetworkSamples.Link("Нидерланды", SampleConnections.VlessReality);
        var a = NetworkSamples.App("A");
        var b = NetworkSamples.App("B");
        var settings = NetworkSamples.Settings(connection, a, b);
        settings.Connections.Add(other);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, NoCopies));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        _backend.Connections = ChromeConnection;

        switch (change)
        {
            case "другое подключение":
                a.Network.Mode = AppNetworkMode.OwnConnection;
                a.Network.ConnectionId = other.Id;
                break;
            case "исключения":
                a.Network.DirectDomains.Add("example.com");
                break;
            case "системный VPN":
                a.Network.Mode = AppNetworkMode.OwnAdapters;
                break;
            default:
                // Лаунчер отпускает убранное приложение (Forget) и сообщает ядру, что оно работало.
                settings.Apps.Remove(a);
                _processes.Running.Remove(a.Id);
                break;
        }
        network.Update(CorePlan.Build(settings, false, NoCopies));
        Assert.Null(await network.KeepRunningAsync("изменились настройки", released: change == "убрано" ? new[] { a.Id } : null));

        Assert.Equal(2, _backend.Starts);
        Assert.Equal(change == "убрано" ? (0, 0) : (1, 1), (_processes.Frozen, _processes.Thawed));
        Assert.True(network.IsCoreRunning);
        if (change is "системный VPN" or "убрано")
            Assert.Null(network.TagFor(a.Id));
        else
            Assert.Equal(CorePlan.AppTag(a.Id), network.TagFor(a.Id));
        if (change == "другое подключение")
            Assert.Equal(CorePlan.ConnectionTag(other.Id), _backend.Last!.Apps.Single(r => r.Tag == CorePlan.AppTag(a.Id)).Outbound);
    }

    [Fact]
    public async Task KeepRunning_RunningAppJoinsCore_PausedUntilCoreKnowsIt()
    {
        // B работало под системным VPN, его перевели на подключение лаунчера: фильтры Windows уже пускают B в адаптер ядра,
        // а ядро до перезапуска не знает его метки. Поэтому B тоже приостанавливается на время перезапуска.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var b = NetworkSamples.App("B", AppNetworkMode.OwnAdapters);
        var settings = NetworkSamples.Settings(connection, a, b);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, NoCopies));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        _processes.Running.Add(b.Id);

        b.Network.Mode = AppNetworkMode.Inherit;
        network.Update(CorePlan.Build(settings, false, NoCopies));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));

        Assert.Equal(2, _backend.Starts);
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(id => id), _processes.FrozenApps.OrderBy(id => id));
        Assert.Equal(CorePlan.AppTag(b.Id), network.TagFor(b.Id));
    }

    [Fact]
    public async Task CoreRecovered_SettingsChangedMeanwhile_AppliedAfterwards()
    {
        // Ядро упало, пока менялись настройки: оно запускается заново с прежними, а затем сразу применяет новые.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        Assert.Null(await network.KeepRunningAsync("открыт лаунчер"));

        var b = NetworkSamples.App("B");
        settings.Apps.Add(b);
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        _backend.Crash();

        Assert.True(await Wait.UntilAsync(() => _backend.Starts == 3, TimeSpan.FromSeconds(5)));
        Assert.True(await Wait.UntilAsync(() => network.OutsideBlocked(b.Id) == false, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task KeepRunning_LastAppLeavesCore_CoreStopsAtOnce()
    {
        // Единственное приложение через ядро перевели на системный VPN, пока оно работает: ядро ему больше не нужно.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, NoCopies));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        _backend.Connections = ChromeConnection;

        a.Network.Mode = AppNetworkMode.OwnAdapters;
        network.Update(CorePlan.Build(settings, false, NoCopies));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));

        Assert.Equal(1, _backend.Stops);
        Assert.False(network.IsCoreRunning);
        Assert.Null(network.TagFor(a.Id));
    }

    [Fact]
    public async Task KeepRunning_HideDnsChanged_AppliedAtOnce()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, NoCopies));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);

        settings.HideDns = true;
        network.Update(CorePlan.Build(settings, false, NoCopies));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));

        Assert.Equal(2, _backend.Starts);
        Assert.True(_backend.Last!.HideDns);
        Assert.Equal(1, _processes.Frozen);
    }

    [Fact]
    public async Task KeepRunning_UrgentRestartFails_RunningAppsClosed()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, NoCopies));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        string? lost = null;
        network.CoreLost += (reason, _) => lost = reason;

        a.Network.DirectDomains.Add("example.com");
        network.Update(CorePlan.Build(settings, false, NoCopies));
        _backend.FailStart = true;

        Assert.Equal("не вышло", await network.KeepRunningAsync("изменились настройки"));
        Assert.Equal(new[] { a.Id }, _processes.Killed);
        Assert.Equal(new[] { a.Id }, _processes.Blocked);
        Assert.NotNull(lost);
        Assert.False(network.IsCoreRunning);
        Assert.Equal((1, 1), (_processes.Frozen, _processes.Thawed));
    }

    [Fact]
    public async Task KeepRunning_UrgentRestartFails_AppLeavingCoreKeepsRunning()
    {
        // A и B работают через ядро, B перевели на системный VPN, а ядро с новыми настройками не запустилось. Закрывается
        // только A: B ядро больше не нужно.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var b = NetworkSamples.App("B");
        var settings = NetworkSamples.Settings(connection, a, b);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, NoCopies));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        _processes.Running.Add(b.Id);

        b.Network.Mode = AppNetworkMode.OwnAdapters;
        network.Update(CorePlan.Build(settings, false, NoCopies));
        _backend.FailStart = true;

        Assert.Equal("не вышло", await network.KeepRunningAsync("изменились настройки"));
        Assert.Equal(new[] { a.Id }, _processes.Killed);
        Assert.Equal(new[] { a.Id }, _processes.Blocked);
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(id => id), _processes.FrozenApps.OrderBy(id => id));
        Assert.Equal((1, 1), (_processes.Frozen, _processes.Thawed));
    }

    [Fact]
    public async Task KeepRunning_CoreNotRunning_RunningAppJoins_PausedUntilCoreKnowsIt()
    {
        // Ядро не работало (все приложения под системным VPN). Работающее B перевели на подключение лаунчера: на время
        // запуска ядра оно приостанавливается, иначе его соединения без метки ушли бы напрямую.
        var connection = NetworkSamples.Link("Германия");
        var b = NetworkSamples.App("B", AppNetworkMode.OwnAdapters);
        var settings = NetworkSamples.Settings(connection, b);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, NoCopies));
        Assert.Null(await network.KeepRunningAsync("открыт лаунчер"));
        Assert.False(network.IsCoreRunning);
        _processes.Running.Add(b.Id);

        b.Network.Mode = AppNetworkMode.Inherit;
        network.Update(CorePlan.Build(settings, false, NoCopies));
        Assert.Null(await network.KeepRunningAsync("изменились настройки"));

        Assert.Equal(1, _backend.Starts);
        Assert.Equal(new[] { b.Id }, _processes.FrozenApps);
        Assert.Equal((1, 1), (_processes.Frozen, _processes.Thawed));
        Assert.Equal(CorePlan.AppTag(b.Id), network.TagFor(b.Id));
    }

    [Fact]
    public void UrgentChange_OnlyWhatWouldGoOtherwise()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var b = NetworkSamples.App("B");
        var settings = NetworkSamples.Settings(connection, a, b);
        var before = CorePlan.Build(settings, false, CorePlanTests.ProgramsOf);
        var beforeNoCopies = CorePlan.Build(settings, false, NoCopies);
        CorePlan After(Func<AppEntry, IReadOnlyList<CoreProgram>>? programs = null, bool ipv6 = false) =>
            CorePlan.Build(settings, ipv6, programs ?? CorePlanTests.ProgramsOf);

        Assert.Null(LauncherNetwork.UrgentChange(before, After(), Array.Empty<Guid>()));
        Assert.Null(LauncherNetwork.UrgentChange(before, After(ipv6: true), new[] { a.Id }));

        // Новый сервер подключения: у запущенных приложений прежний продолжает работать, новый — при следующем запуске. Копии
        // мимо лаунчера идут через новый сервер сразу, только если ни одно приложение этого подключения не запущено.
        connection.Link = "trojan://secret@5.6.7.9:443?sni=example.com#Другой";
        Assert.Null(LauncherNetwork.UrgentChange(before, After(), new[] { a.Id, b.Id }));
        Assert.Null(LauncherNetwork.UrgentChange(before, After(), new[] { a.Id }));
        Assert.Contains(LauncherNetwork.UrgentChange(before, After(), Array.Empty<Guid>()), new[] { "копии «A» мимо лаунчера", "копии «B» мимо лаунчера" });
        Assert.Null(LauncherNetwork.UrgentChange(beforeNoCopies, After(NoCopies), Array.Empty<Guid>()));

        // Исключения B (новый сервер уже в ядре): срочно, только если B работает или его копии идут через ядро.
        before = After();
        beforeNoCopies = After(NoCopies);
        b.Network.DirectDomains.Add("example.com");
        Assert.Equal("копии «B» мимо лаунчера", LauncherNetwork.UrgentChange(before, After(), Array.Empty<Guid>()));
        Assert.Equal("работающее приложение «B»", LauncherNetwork.UrgentChange(before, After(), new[] { b.Id }));
        Assert.Null(LauncherNetwork.UrgentChange(beforeNoCopies, After(NoCopies), Array.Empty<Guid>()));
        Assert.Equal("работающее приложение «B»", LauncherNetwork.UrgentChange(beforeNoCopies, After(NoCopies), new[] { b.Id }));

        settings.HideDns = true;
        Assert.Equal("DNS-запросы всех программ", LauncherNetwork.UrgentChange(before, After(), Array.Empty<Guid>()));
    }

    [Fact]
    public async Task Idle_AppsThroughCoreInSettings_CoreKeepsRunning_AndCatchesUpWithSettings()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var settings = NetworkSamples.Settings(connection, a);
        using var network = Network(TimeSpan.FromMilliseconds(30));
        network.Update(CorePlan.Build(settings, false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        settings.Apps.Add(NetworkSamples.App("B"));
        network.Update(CorePlan.Build(settings, false));

        // Приложение закрылось: ядро не выключается, а догоняет новые настройки (через него ничего не идёт).
        _processes.Running.Clear();
        network.OnProcessesChanged();

        Assert.True(await Wait.UntilAsync(() => _backend.Starts == 2, TimeSpan.FromSeconds(5)));
        await Task.Delay(200);
        Assert.Equal(0, _backend.Stops);
        Assert.True(network.IsCoreRunning);
    }

    [Fact]
    public async Task CoreCrash_Restart_AppsPausedThenContinue()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var other = Guid.NewGuid();
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        _processes.Running.Add(other);
        var recovered = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        network.CoreRecovered += (_, apps) => recovered.TrySetResult(apps);
        var lost = false;
        network.CoreLost += (_, _) => lost = true;

        _backend.Crash();

        Assert.Equal(new[] { "A" }, await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, _backend.Starts);
        Assert.Equal((1, 1), (_processes.Frozen, _processes.Thawed));
        Assert.Empty(_processes.Killed);
        Assert.False(lost);
        Assert.True(network.IsCoreRunning);
        Assert.Equal(CoreAppState.Protected, network.StateOf(a.Id));
    }

    [Fact]
    public async Task CoreCrash_RestartFails_OnlyAppsThroughCoreClosed()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A", closeOnDrop: ProtectionOverride.Off);
        var other = Guid.NewGuid();
        using var network = Network();
        network.RecoveryBudget = TimeSpan.FromMilliseconds(300);
        network.RecoveryPause = TimeSpan.FromMilliseconds(50);
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        _processes.Running.Add(other);
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        network.CoreLost += (reason, _) => lost.TrySetResult(reason);

        _backend.FailStart = true; // служба после сбоя так и не вернулась
        _backend.Crash();

        Assert.Contains("Запустить ядро заново не удалось", await lost.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { a.Id }, _processes.Killed); // даже без «Закрывать при обрыве»
        Assert.Contains(other, _processes.Running);
        Assert.True(_backend.Starts > 2); // несколько попыток
        Assert.Equal((1, 1), (_processes.Frozen, _processes.Thawed));
        Assert.False(network.IsCoreRunning);
    }

    [Fact]
    public async Task CoreCrash_RestartFails_NothingRunning_ReportedAndRetriedLater()
    {
        // Ядро было нужно только копиям приложений мимо лаунчера, и служба не вернулась: без ядра копии идут напрямую, поэтому
        // пользователь узнаёт об этом, а лаунчер ещё раз пробует запустить ядро позже.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network();
        network.RecoveryBudget = TimeSpan.FromMilliseconds(300);
        network.RecoveryPause = TimeSpan.FromMilliseconds(50);
        network.RecoveryRetry = TimeSpan.FromMilliseconds(300);
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        Assert.Null(await network.KeepRunningAsync("тест"));
        var lost = new TaskCompletionSource<KillReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        network.CoreLost += (_, report) => lost.TrySetResult(report);

        _backend.FailStart = true;
        _backend.Crash();

        var closed = await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(closed.Killed);
        Assert.False(network.IsCoreRunning);
        _backend.FailStart = false;
        Assert.True(await Wait.UntilAsync(() => network.IsCoreRunning, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Health_CoreRestartedDuringProbe_MissNotCounted()
    {
        // Ядро упало и запустилось заново с теми же настройками, пока шла проверка: её промах относится к старому ядру.
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        var crashed = false;
        _backend.Delay = _ =>
        {
            if (!crashed)
            {
                crashed = true;
                _backend.Crash();
            }

            return null;
        };

        await network.CheckHealthAsync();
        Assert.True(await Wait.UntilAsync(() => network.IsCoreRunning, TimeSpan.FromSeconds(5)));
        await network.CheckHealthAsync();

        Assert.Equal(CoreAppState.Protected, network.StateOf(a.Id)); // засчитан один промах из двух
    }

    [Fact]
    public async Task CoreCrash_ThirdTimeInFiveMinutes_AppsClosed()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        var recovered = 0;
        network.CoreRecovered += (_, _) => Interlocked.Increment(ref recovered);
        string? reason = null;
        network.CoreLost += (r, _) => reason = r;

        _backend.Crash();
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref recovered) == 1, TimeSpan.FromSeconds(5)));
        _backend.Crash();
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref recovered) == 2, TimeSpan.FromSeconds(5)));
        _backend.Crash();

        Assert.Contains("слишком часто", reason);
        Assert.Equal(new[] { a.Id }, _processes.Killed);
        Assert.Equal(3, _backend.Starts);
        Assert.False(network.IsCoreRunning);
    }

    [Fact]
    public async Task OutsideCopies_KnownWhileCoreRuns_ChangeRestartsForLaunch()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        var b = NetworkSamples.App("B");
        var settings = NetworkSamples.Settings(connection, a, b);
        using var network = Network();
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        Assert.Null(network.OutsideBlocked(a.Id)); // ядро не работает: копии идут напрямую

        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        Assert.False(network.OutsideBlocked(a.Id));

        // Запускаемое приложение пошло бы иначе, чем с новыми настройками: перезапуск, работающее приостанавливается.
        settings.OutsideCopies = OutsideCopies.Block;
        network.Update(CorePlan.Build(settings, false, CorePlanTests.ProgramsOf));
        Assert.False(network.OutsideBlocked(a.Id));
        Assert.Null(await network.PrepareLaunchAsync(b.Id));

        Assert.Equal(2, _backend.Starts);
        Assert.Equal(1, _processes.Frozen);
        Assert.True(network.OutsideBlocked(a.Id));
        Assert.All(_backend.Last!.Apps, r => Assert.True(r.BlockOutside));
    }
}

public class CoreApiClientTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public readonly List<string> Urls = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = "{\"delay\":42}";
        public Func<string, (HttpStatusCode Status, string Body)>? Answer;
        public Exception? Throw;
        public TimeSpan Pause;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Urls.Add(request.RequestUri!.AbsoluteUri);
            if (Pause > TimeSpan.Zero)
                await Task.Delay(Pause, cancellationToken);
            if (Throw is not null)
                throw Throw;
            var (status, body) = Answer?.Invoke(request.RequestUri.AbsoluteUri) ?? (Status, Body);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Delay_AsksCoreWithSecret()
    {
        var handler = new Handler();
        using var api = new CoreApiClient(9090, "ключ", handler);

        Assert.Equal(42, await api.DelayAsync("c1", TimeSpan.FromSeconds(5)));
        Assert.Equal("127.0.0.1", handler.Request!.RequestUri!.Host);
        Assert.StartsWith("/proxies/c1/delay", handler.Request.RequestUri.AbsolutePath);
        Assert.Contains("timeout=5000", handler.Request.RequestUri.Query);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Single(handler.Urls); // первая страница открылась: вторая не нужна
    }

    [Theory]
    [InlineData(HttpStatusCode.GatewayTimeout, "{\"message\":\"Timeout\"}")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(HttpStatusCode.OK, "{\"delay\":0}")]
    public async Task Delay_NullWhenServerDoesNotAnswer(HttpStatusCode status, string body)
    {
        using var api = new CoreApiClient(9090, "ключ", new Handler { Status = status, Body = body });

        Assert.Null(await api.DelayAsync("c1", TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(HttpStatusCode.GatewayTimeout, "{\"message\":\"Timeout\"}", "не ответил за 5 с")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"message\":\"An error occurred in the delay test\"}", "An error occurred in the delay test")]
    [InlineData(HttpStatusCode.NotFound, "{\"message\":\"Resource not found\"}", "не знает это подключение")]
    [InlineData(HttpStatusCode.OK, "{\"delay\":0}", "пустую задержку")]
    [InlineData(HttpStatusCode.BadGateway, "не json", "ядро ответило 502")]
    public async Task Probe_ExplainsFailure(HttpStatusCode status, string body, string expected)
    {
        using var api = new CoreApiClient(9090, "ключ", new Handler { Status = status, Body = body });

        var probe = await api.ProbeAsync("c1", TimeSpan.FromSeconds(5));

        Assert.Null(probe.Delay);
        Assert.Contains(expected, probe.Failure);
    }

    [Fact]
    public async Task Probe_CoreUnreachable()
    {
        var handler = new Handler { Throw = new HttpRequestException("Подключение не установлено") };
        using var api = new CoreApiClient(9090, "ключ", handler);

        Assert.Contains("нет связи с управлением ядра", (await api.ProbeAsync("c1", TimeSpan.FromSeconds(5))).Failure);
        Assert.Single(handler.Urls); // вторая страница тут не поможет
    }

    [Fact]
    public async Task Probe_FirstPageFails_SecondCounts()
    {
        var handler = new Handler
        {
            Answer = url => url.Contains("cloudflare")
                ? (HttpStatusCode.ServiceUnavailable, "{\"message\":\"An error occurred in the delay test\"}")
                : (HttpStatusCode.OK, "{\"delay\":77}")
        };
        using var api = new CoreApiClient(9090, "ключ", handler);

        var probe = await api.ProbeAsync("c1", TimeSpan.FromSeconds(5));

        Assert.Equal(77, probe.Delay);
        Assert.Null(probe.Failure);
        Assert.Contains("An error occurred in the delay test", probe.Note);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains("gstatic", handler.Urls[1]);
    }

    [Fact]
    public async Task Probe_BothPagesFail_Miss()
    {
        var handler = new Handler { Status = HttpStatusCode.GatewayTimeout, Body = "{\"message\":\"Timeout\"}" };
        using var api = new CoreApiClient(9090, "ключ", handler);

        var probe = await api.ProbeAsync("c1", TimeSpan.FromSeconds(5));

        Assert.Null(probe.Delay);
        Assert.Equal("сервер не ответил за 5 с («Timeout»); вторая страница: сервер не ответил за 5 с («Timeout»)", probe.Failure);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Probe_CoreRefused_NoSecondPage(HttpStatusCode status)
    {
        var handler = new Handler { Status = status, Body = "{}" };
        using var api = new CoreApiClient(9090, "ключ", handler);

        Assert.Null((await api.ProbeAsync("c1", TimeSpan.FromSeconds(5))).Delay);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task Connections_ProgramsAndPorts()
    {
        // Так отвечает ядро (sing-box 1.14, GET /connections).
        var handler = new Handler
        {
            Body = """
                {"downloadTotal":30,"uploadTotal":12,"connections":[
                 {"id":"7a1","metadata":{"network":"tcp","type":"tun/tun-in","sourceIP":"172.31.250.1","destinationIP":"203.0.113.10",
                  "sourcePort":"50311","destinationPort":"443","host":"","dnsMode":"normal",
                  "processPath":"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"},
                  "upload":10,"download":20,"start":"2026-10-15T08:00:00Z","chains":["direct"],"rule":"final","rulePayload":""},
                 {"id":"7a2","metadata":{"network":"udp","type":"tun/tun-in","destinationPort":"7",
                  "processPath":"launcher:a1|C:\\Games\\game.exe"},"upload":2,"download":10,"chains":["c1"]}]}
                """
        };
        using var api = new CoreApiClient(9090, "ключ", handler);

        var list = await api.ConnectionsAsync(TimeSpan.FromSeconds(3));

        Assert.Null(list.Failure);
        Assert.Equal(new[]
        {
            new CoreConnection("tcp", 443, @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            new CoreConnection("udp", 7, @"launcher:a1|C:\Games\game.exe")
        }, list.Items);
        Assert.Equal("/connections", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"downloadTotal\":0,\"uploadTotal\":0,\"connections\":null}", null)]
    [InlineData(HttpStatusCode.OK, "{\"downloadTotal\":0,\"uploadTotal\":0,\"connections\":[]}", null)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"message\":\"Unauthorized\"}", "не приняло ключ")]
    [InlineData(HttpStatusCode.OK, "не json", "непонятный ответ")]
    public async Task Connections_NoneOrFailure(HttpStatusCode status, string body, string? failure)
    {
        using var api = new CoreApiClient(9090, "ключ", new Handler { Status = status, Body = body });

        var list = await api.ConnectionsAsync(TimeSpan.FromSeconds(3));

        if (failure is null)
        {
            Assert.Empty(list.Items!);
        }
        else
        {
            Assert.Null(list.Items);
            Assert.Contains(failure, list.Failure);
        }
    }

    [Fact]
    public async Task Connections_CoreSilent_GivesUp()
    {
        using var api = new CoreApiClient(9090, "ключ", new Handler { Pause = TimeSpan.FromSeconds(30) });

        var list = await api.ConnectionsAsync(TimeSpan.FromMilliseconds(200));

        Assert.Null(list.Items);
        Assert.Contains("управление ядра не ответило", list.Failure);
    }
}

public class BrowsersAndSubscriptionsTests
{
    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("MSEDGE.EXE")]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe")]
    public void KnownBrowsers(string name) => Assert.True(Browsers.IsBrowser(name));

    [Theory]
    [InlineData("Discord.exe")]
    [InlineData("msedgewebview2.exe")] // встроенный движок приложения — часть приложения
    public void NotBrowsers(string name) => Assert.False(Browsers.IsBrowser(name));

    [Theory]
    [InlineData("\"C:\\Program Files\\Yandex\\browser.exe\" --single-argument %1", "browser.exe")]
    [InlineData("C:\\Tools\\Some Browser\\sb.exe -url \"%1\"", "sb.exe")]
    [InlineData("rundll32.dll", null)]
    public void DefaultBrowserCommand(string command, string? exe) => Assert.Equal(exe, Browsers.ExeFromCommand(command));

    private static VpnConnection Subscription(params string[] servers) => new()
    {
        Name = "Подписка",
        Kind = ConnectionKind.Subscription,
        SubscriptionUrl = "https://example.com/sub",
        SubscriptionServers = servers.ToList()
    };

    [Fact]
    public void Due_WhenNeverLoadedOrOld()
    {
        var now = DateTimeOffset.Now;
        Assert.True(SubscriptionUpdater.IsDue(Subscription(), now));

        var fresh = Subscription("trojan://a@1.1.1.1:443#A");
        fresh.SubscriptionUpdatedAt = now.AddHours(-2);
        Assert.False(SubscriptionUpdater.IsDue(fresh, now));
        fresh.SubscriptionUpdatedAt = now.AddHours(-25);
        Assert.True(SubscriptionUpdater.IsDue(fresh, now));
        Assert.False(SubscriptionUpdater.IsDue(NetworkSamples.Link("ссылка"), now));
    }

    [Fact]
    public void Apply_KeepsSelectedServerEvenIfRenamed()
    {
        var connection = Subscription("trojan://a@1.1.1.1:443#Первый", "trojan://b@2.2.2.2:443#Второй");
        connection.SelectedServer = "trojan://b@2.2.2.2:443#Второй";

        var changed = SubscriptionUpdater.Apply(connection,
            new SubscriptionContent(new[] { "trojan://a@1.1.1.1:443#Первый", "trojan://b@2.2.2.2:443#Второй (новое имя)" }, 0), DateTimeOffset.Now);

        Assert.True(changed); // ссылка с новым названием — другая строка, но тот же сервер
        Assert.Equal("trojan://b@2.2.2.2:443#Второй (новое имя)", Subscriptions.Pick(connection.SubscriptionServers, connection.SelectedServer));
        Assert.NotNull(connection.SubscriptionUpdatedAt);
    }

    [Fact]
    public void Apply_SelectedGone_FallsBackToFirst()
    {
        var connection = Subscription("trojan://a@1.1.1.1:443#A", "trojan://b@2.2.2.2:443#B");
        connection.SelectedServer = "trojan://b@2.2.2.2:443#B";

        Assert.True(SubscriptionUpdater.Apply(connection, new SubscriptionContent(new[] { "trojan://c@3.3.3.3:443#C" }, 0), DateTimeOffset.Now));
        Assert.Null(connection.SelectedServer);
        Assert.Equal("trojan://c@3.3.3.3:443#C", Subscriptions.Pick(connection.SubscriptionServers, connection.SelectedServer));
    }

    [Fact]
    public void Apply_SameServers_NoChange()
    {
        var connection = Subscription("trojan://a@1.1.1.1:443#A");

        Assert.False(SubscriptionUpdater.Apply(connection, new SubscriptionContent(new[] { "trojan://a@1.1.1.1:443#A" }, 0), DateTimeOffset.Now));
    }
}

/// <summary>
/// Общие исключения: дополняются или заменяются исключениями приложения и доходят до правил ядра только у приложений
/// через подключение лаунчера. Остальной трафик по-прежнему идёт через подключение.
/// </summary>
public class CommonDomainsTests
{
    private static readonly VpnConnection Connection = NetworkSamples.Link("Германия");

    private static AppEntry Own(AppEntry app, DomainsMode mode)
    {
        app.Network.DomainsMode = mode;
        return app;
    }

    private static AppSettings Settings(params AppEntry[] apps)
    {
        var settings = NetworkSamples.Settings(Connection, apps);
        settings.DirectDomains = new List<string> { "kaspi.kz", "gosuslugi.ru" };
        return settings;
    }

    [Fact]
    public void AddToCommon_IsTheDefault_CommonFirstThenOwn_NoDuplicates()
    {
        var app = NetworkSamples.App("Discord", domains: new[] { "discord.com", "kaspi.kz" });

        Assert.Equal(DomainsMode.AddToCommon, app.Network.DomainsMode);
        Assert.Equal(new[] { "kaspi.kz", "gosuslugi.ru", "discord.com" }, NetworkPlan.For(app, Settings(app)).DirectDomains);
    }

    [Fact]
    public void OwnOnly_ReplacesCommon_EmptyMeansNoExceptions()
    {
        var own = Own(NetworkSamples.App("Банк", domains: "bank.kz"), DomainsMode.OwnOnly);
        var none = Own(NetworkSamples.App("Почта"), DomainsMode.OwnOnly);
        var settings = Settings(own, none);

        Assert.Equal(new[] { "bank.kz" }, NetworkPlan.For(own, settings).DirectDomains);
        Assert.Empty(NetworkPlan.For(none, settings).DirectDomains);
    }

    [Fact]
    public void Common_OnlyForLauncherConnection_NotSystemVpnOrOffline()
    {
        var adapters = NetworkSamples.App("Игра", AppNetworkMode.OwnAdapters);
        var offline = NetworkSamples.App("Редактор", AppNetworkMode.Offline);
        var own = NetworkSamples.App("Discord", AppNetworkMode.OwnConnection, Connection.Id);
        var settings = Settings(adapters, offline, own);

        Assert.Empty(NetworkPlan.For(adapters, settings).DirectDomains);
        Assert.Empty(NetworkPlan.For(offline, settings).DirectDomains);
        Assert.Empty(NetworkPlan.For(own, settings, offlineRun: true).DirectDomains);
        Assert.Equal(new[] { "kaspi.kz", "gosuslugi.ru" }, NetworkPlan.For(own, settings).DirectDomains);

        // Общее условие — системный VPN: приложения «как в общих» ядро не ведёт, исключений нет.
        var inherit = NetworkSamples.App("Telegram");
        settings.Apps.Add(inherit);
        settings.NetworkMode = GlobalNetworkMode.SystemVpn;
        Assert.Empty(NetworkPlan.For(inherit, settings).DirectDomains);
    }

    [Fact]
    public void NoCommonList_BehavesAsBefore()
    {
        var app = NetworkSamples.App("Discord", domains: "discord.com");
        var settings = NetworkSamples.Settings(Connection, app);

        Assert.Equal(new[] { "discord.com" }, NetworkPlan.For(app, settings).DirectDomains);
        Own(app, DomainsMode.OwnOnly);
        Assert.Equal(new[] { "discord.com" }, NetworkPlan.For(app, settings).DirectDomains);
    }

    [Fact]
    public void CoreRules_DirectOnlyForListedDomainsOfEachApp_RestThroughConnection()
    {
        var add = NetworkSamples.App("Discord", domains: "discord.com");
        var empty = Own(NetworkSamples.App("Банк"), DomainsMode.OwnOnly);
        var ownOnly = Own(NetworkSamples.App("Почта", domains: "mail.kz"), DomainsMode.OwnOnly);
        var adapters = NetworkSamples.App("Игра", AppNetworkMode.OwnAdapters, domains: "game.com");
        var plan = CorePlan.Build(Settings(add, empty, ownOnly, adapters), ipv6: false);

        Assert.False(plan.Apps.ContainsKey(adapters.Id));
        var request = new Svc.CoreRequest
        {
            Outbounds = plan.Request.Outbounds,
            Apps = plan.Request.Apps.Select(r => new Svc.CoreAppRoute { Tag = r.Tag, Outbound = r.Outbound, DirectDomains = r.DirectDomains }).ToList(),
            OutboundDns = plan.Request.OutboundDns
        };
        var rules = Svc.CoreConfigBuilder.Build(request)["route"]!["rules"]!.AsArray();

        string[] Direct(AppEntry app)
        {
            var prefix = "^" + System.Text.RegularExpressions.Regex.Escape("launcher:" + plan.Apps[app.Id].Tag) + @"\|";
            return rules.Where(r => (string?)r!["outbound"] == Svc.CoreConfigBuilder.DirectTag &&
                                    r!["process_path_regex"]!.AsArray().Any(p => (string?)p == prefix))
                .SelectMany(r => r!["domain_suffix"]!.AsArray().Select(d => (string)d!)).ToArray();
        }

        Assert.Equal(new[] { "kaspi.kz", "gosuslugi.ru", "discord.com" }, Direct(add));
        Assert.Empty(Direct(empty));
        Assert.Equal(new[] { "mail.kz" }, Direct(ownOnly));

        // Прямой маршрут — только с доменами: у каждого приложения нет правила «напрямую» без списка сайтов,
        // а последнее правило приложения ведёт всё остальное в его подключение.
        Assert.All(rules.Where(r => (string?)r!["outbound"] == Svc.CoreConfigBuilder.DirectTag && r!["process_path_regex"] is not null),
            r => Assert.NotEmpty(r!["domain_suffix"]!.AsArray()));
        var connectionTag = CorePlan.ConnectionTag(Connection.Id);
        foreach (var app in new[] { add, empty, ownOnly })
        {
            var prefix = "^" + System.Text.RegularExpressions.Regex.Escape("launcher:" + plan.Apps[app.Id].Tag) + @"\|";
            Assert.Contains(rules, r => (string?)r!["outbound"] == connectionTag && r!["domain_suffix"] is null &&
                                        r["process_path_regex"]!.AsArray().Any(p => (string?)p == prefix));
        }
    }

    [Fact]
    public void ChangingCommonList_ChangesRunningAppsRoute()
    {
        var app = NetworkSamples.App("Discord");
        var settings = Settings(app);
        var before = CorePlan.Build(settings, ipv6: false);

        settings.DirectDomains.Add("example.com");
        var after = CorePlan.Build(settings, ipv6: false);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.False(LauncherNetwork.SameRoute(before, after, app.Id));

        // «Вместо общих»: общий список приложения не касается.
        Own(app, DomainsMode.OwnOnly);
        var own = CorePlan.Build(settings, ipv6: false);
        settings.DirectDomains.Add("other.com");
        Assert.True(LauncherNetwork.SameRoute(own, CorePlan.Build(settings, ipv6: false), app.Id));
    }

    [Fact]
    public void Combine_NeverMoreThanServiceAccepts()
    {
        var common = Enumerable.Range(0, 800).Select(i => $"c{i}.com").ToList();
        var own = Enumerable.Range(0, 800).Select(i => $"o{i}.com").ToList();

        Assert.Equal(DomainList.MaxPerApp, DomainList.Combine(common, own, DomainsMode.AddToCommon).Count);
        Assert.Equal(800, DomainList.Combine(common, own, DomainsMode.OwnOnly).Count);
        Assert.Empty(DomainList.Combine(null, null, DomainsMode.AddToCommon));
    }

    [Fact]
    public void FieldText_SplitAndProblems()
    {
        Assert.Equal(new[] { "kaspi.kz", "https://gosuslugi.ru/help", "a.com", "b.com" },
            DomainList.Split("kaspi.kz\r\n https://gosuslugi.ru/help \n\na.com, b.com"));
        Assert.Null(DomainList.Problem("kaspi.kz\n*.gosuslugi.ru"));
        Assert.Null(DomainList.Problem(""));
        Assert.Contains("домен!", DomainList.Problem("kaspi.kz\nне домен!") ?? "нет");
    }
}
