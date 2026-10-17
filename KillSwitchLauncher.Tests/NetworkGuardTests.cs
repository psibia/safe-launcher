using System.Collections.Generic;
using System.Linq;
using Xunit;
using Svc = SafeLauncher.Service;

namespace KillSwitchLauncher.Tests;

/// <summary>Служба: какие программы считаются программами служб (их сеть лаунчер не ограничивает).</summary>
public class NetworkGuardTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\AnyDesk\\AnyDesk.exe\" --service", "C:\\Program Files\\AnyDesk\\AnyDesk.exe")]
    [InlineData("C:\\Program Files\\Vendor App\\agent.exe /svc", "C:\\Program Files\\Vendor App\\agent.exe")]
    [InlineData("\\??\\C:\\Tools\\svc.exe", "C:\\Tools\\svc.exe")]
    [InlineData("C:\\Tools\\a.exe.d\\svc.exe -k run", "C:\\Tools\\a.exe.d\\svc.exe")]
    [InlineData("C:\\Tools\\..\\Tools\\svc.EXE", "C:\\Tools\\svc.EXE")]
    [InlineData("system32\\svchost.exe -k netsvcs", null)] // программы Windows и так под защитой
    [InlineData("%SystemRoot%\\system32\\svchost.exe -k netsvcs", null)]
    [InlineData("\\SystemRoot\\System32\\drivers\\tcpip.sys", null)]
    [InlineData("\\\\server\\share\\svc.exe", null)]
    [InlineData("\"C:\\Broken\\svc.exe", null)]
    [InlineData("", null)]
    public void ServiceImage_ParsedFromRegistryValue(string imagePath, string? expected)
    {
        Assert.Equal(expected, Svc.NetworkGuard.ServiceImage(imagePath));
    }
}

/// <summary>Наборы фильтров службы: что остаётся после отключения лаунчера и что служба берёт из файла состояния после своего сбоя.</summary>
public class GuardSetsTests
{
    private const string Game = @"C:\Games\game.exe";
    private const string Chat = @"C:\Apps\Чат\chat.exe";

    private static Svc.GuardRequest Request(params string[] dnsPaths) => new()
    {
        Apps =
        {
            new Svc.GuardRule { Paths = { Chat }, ViaCore = true, LoopbackPorts = new List<int> { 10808 } },
            new Svc.GuardRule { Paths = { Game }, Interfaces = { 12, 31 } }
        },
        DnsInterfaces = dnsPaths.Length > 0 ? new List<int> { 12 } : null,
        DnsPaths = dnsPaths.Length > 0 ? dnsPaths.ToList() : null
    };

    [Fact]
    public void LauncherGone_OnlyDnsHidingStays()
    {
        var sets = new Svc.GuardSets();
        var launcher = new object();
        sets.Set(launcher, "S-1-5-21-1", Request(Game));

        Assert.True(sets.Release(launcher));

        Assert.Equal((0, 1), (sets.Owners, sets.Orphans));
        var left = Assert.Single(sets.All);
        Assert.Empty(left.Apps); // приложения под блокировкой Windows закрывает вместе с лаунчером
        Assert.Equal(new[] { 12 }, left.DnsInterfaces);
        Assert.Equal(new[] { Game }, left.DnsPaths);
        Assert.False(sets.Release(launcher));
    }

    [Fact]
    public void LauncherGone_WithoutDnsHiding_NothingStays()
    {
        var sets = new Svc.GuardSets();
        var launcher = new object();
        sets.Set(launcher, "S-1-5-21-1", Request());

        Assert.True(sets.Release(launcher));

        Assert.True(sets.IsEmpty);
    }

    [Fact]
    public void NewLauncherOfSameUser_ReplacesWhatWasLeft()
    {
        var sets = new Svc.GuardSets();
        var first = new object();
        var other = new object();
        sets.Set(first, "S-1-5-21-1", Request(Game));
        sets.Set(other, "S-1-5-21-2", Request(Chat));
        sets.Release(first);
        sets.Release(other);
        Assert.Equal(2, sets.Orphans);

        sets.Set(new object(), "S-1-5-21-1", null); // лаунчер того же пользователя снова подключился, пока без приложений

        Assert.Equal((0, 1), (sets.Owners, sets.Orphans));
        Assert.Equal(new[] { Chat }, Assert.Single(sets.All).DnsPaths);
    }

    [Fact]
    public void ClosedPrograms_AreRemoved()
    {
        var sets = new Svc.GuardSets();
        var gone = new object();
        sets.Set(gone, "S-1-5-21-1", Request(Game, Chat));
        sets.Set(new object(), "S-1-5-21-2", Request(Game));
        sets.Release(gone);

        Assert.True(sets.Prune(path => path == Chat, _ => true));
        Assert.Equal(new[] { Chat }, sets.All.Last().DnsPaths);
        Assert.False(sets.Prune(path => path == Chat, _ => true));

        Assert.True(sets.Prune(_ => false, _ => false));
        Assert.Equal((1, 0), (sets.Owners, sets.Orphans)); // набор подключённого лаунчера не трогается
    }

    [Fact]
    public void VpnAdapterGone_DnsHidingRemoved()
    {
        var sets = new Svc.GuardSets();
        var launcher = new object();
        var request = Request(Game);
        request.DnsInterfaces = new List<int> { 12, 31 };
        sets.Set(launcher, "S-1-5-21-1", request);
        sets.Release(launcher);

        Assert.False(sets.Prune(_ => true, _ => true));
        Assert.True(sets.Prune(_ => true, index => index == 31)); // один из двух адаптеров VPN пропал
        Assert.Equal(new[] { 31 }, Assert.Single(sets.All).DnsInterfaces);

        // VPN отключился: без его адаптера DNS не закрывается, иначе имена пропали бы на всём компьютере
        Assert.True(sets.Prune(_ => true, _ => false));
        Assert.True(sets.IsEmpty);
    }

    [Fact]
    public void VpnAdapterGone_RestoredAppRulesStay()
    {
        var sets = new Svc.GuardSets();
        sets.AddOrphans(new[] { new Svc.GuardSets.Entry("S-1-5-21-1", Request(Game)) });

        Assert.True(sets.Prune(_ => true, _ => false));

        var left = Assert.Single(sets.All);
        Assert.Equal(2, left.Apps.Count); // правила остаются: под блокировкой без VPN сети у приложения и не должно быть
        Assert.Null(left.DnsInterfaces);
        Assert.Null(left.DnsPaths);
    }

    [Fact]
    public void Snapshot_UndoesChanges()
    {
        var sets = new Svc.GuardSets();
        var launcher = new object();
        sets.Set(launcher, "S-1-5-21-1", Request(Game));
        var snapshot = sets.Snapshot();

        sets.Release(launcher);
        sets.Set(new object(), "S-1-5-21-2", Request());
        sets.Restore(snapshot);

        Assert.Equal((1, 0), (sets.Owners, sets.Orphans));
        Assert.Equal(2, Assert.Single(sets.All).Apps.Count);
    }

    [Fact]
    public void StateFile_EverythingComesBackAsLeftovers()
    {
        var sets = new Svc.GuardSets();
        var gone = new object();
        sets.Set(gone, "S-1-5-21-1", Request(Game));
        sets.Set(new object(), "S-1-5-21-2", Request());
        sets.Release(gone);

        var restored = Svc.GuardSets.Deserialize(sets.Serialize());

        Assert.Equal(2, restored.Count);
        // лаунчер был подключён: его приложения остаются под фильтрами, пока он не пришлёт набор заново
        var connected = restored.Single(e => e.User == "S-1-5-21-2").Request;
        Assert.Equal(2, connected.Apps.Count);
        Assert.Equal((Chat, true), (connected.Apps[0].Paths.Single(), connected.Apps[0].ViaCore));
        Assert.Equal(new[] { 10808 }, connected.Apps[0].LoopbackPorts);
        Assert.Equal(new[] { 12, 31 }, connected.Apps[1].Interfaces);
        Assert.Null(connected.DnsPaths);
        var left = restored.Single(e => e.User == "S-1-5-21-1").Request;
        Assert.Empty(left.Apps);
        Assert.Equal(new[] { Game }, left.DnsPaths);

        var after = new Svc.GuardSets();
        after.AddOrphans(restored);
        Assert.Equal((0, 2), (after.Owners, after.Orphans));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"sets\":null}")]
    [InlineData("{\"sets\":[null,{\"user\":\"S-1\"},{\"guard\":{\"apps\":[{\"paths\":[]},null]}}]}")]
    public void StateFile_Broken_NothingRestored(string json)
    {
        Assert.Empty(Svc.GuardSets.Deserialize(json));
    }
}
