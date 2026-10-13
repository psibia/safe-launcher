using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Блокировка трафика мимо VPN: что и когда лаунчер отправляет службе.</summary>
public class VpnGuardTests
{
    private sealed class FakeGuardBackend : IGuardBackend
    {
        public readonly List<GuardRequest> Applied = new();
        public string? Fail;
        public string? Notice;
        public List<string> Unguarded = new();
        public bool Connected = true;
        public bool Disposed;

        public bool IsConnected => Connected && !Disposed;

        public event Action? Lost;

        public Task<GuardReply> ApplyAsync(GuardRequest request, CancellationToken cancellation)
        {
            if (Fail is { } fail)
                throw new CoreServiceException(fail);
            lock (Applied)
                Applied.Add(request);
            return Task.FromResult(new GuardReply(Notice, Unguarded));
        }

        public void RaiseLost()
        {
            Connected = false;
            Lost?.Invoke();
        }

        public void Dispose() => Disposed = true;
    }

    private static GuardRequest Request(string path = @"C:\App\app.exe", params int[] interfaces) => new()
    {
        Apps = { new GuardRule { Paths = { path }, Interfaces = interfaces.ToList() } }
    };

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task NothingToBlock_ServiceNotContacted()
    {
        var created = 0;
        using var guard = new VpnGuard(() =>
        {
            created++;
            return new FakeGuardBackend();
        });

        Assert.Null(await guard.ApplyNowAsync(new GuardRequest()));
        guard.Submit(new GuardRequest { DnsInterfaces = new List<int>() });
        await Task.Delay(50);

        Assert.Equal(0, created);
        Assert.True(guard.IsApplied);
    }

    [Fact]
    public async Task ApplyNow_SendsOnce_SameSetNotResent()
    {
        var backend = new FakeGuardBackend();
        using var guard = new VpnGuard(() => backend);

        Assert.Null(await guard.ApplyNowAsync(Request(interfaces: new[] { 3, 7 })));
        // тот же набор в другом порядке и регистре — не отправляется повторно
        Assert.Null(await guard.ApplyNowAsync(Request(@"c:\app\APP.exe", 7, 3)));

        Assert.Single(backend.Applied);
        Assert.True(guard.IsApplied);
    }

    [Fact]
    public async Task ProxyPorts_PartOfTheSet()
    {
        var backend = new FakeGuardBackend();
        using var guard = new VpnGuard(() => backend);
        GuardRequest WithPorts(params int[] ports)
        {
            var request = Request();
            request.Apps[0].ViaCore = true;
            request.Apps[0].LoopbackPorts = ports.Length == 0 ? null : ports.ToList();
            return request;
        }

        await guard.ApplyNowAsync(WithPorts(10808, 10809));
        await guard.ApplyNowAsync(WithPorts(10809, 10808)); // тот же набор
        await guard.ApplyNowAsync(WithPorts()); // прокси выключили — фильтр меняется

        Assert.Equal(2, backend.Applied.Count);
        Assert.Null(backend.Applied[1].Apps[0].LoopbackPorts);
    }

    [Fact]
    public void SystemProxy_OnlyThisComputer()
    {
        Assert.Equal(new[] { 10808 }, SystemProxy.Parse(true, "127.0.0.1:10808", null)!.Ports);
        Assert.Equal("127.0.0.1:10808", SystemProxy.Parse(true, "127.0.0.1:10808", null)!.Display);

        var split = SystemProxy.Parse(true, "http=127.0.0.1:10809;https=127.0.0.1:10809;socks=127.0.0.1:10808", null)!;
        Assert.Equal(new[] { 10808, 10809 }, split.Ports);
        Assert.Equal("127.0.0.1:10809, 127.0.0.1:10808", split.Display);

        Assert.Equal(new[] { 3128 }, SystemProxy.Parse(true, "localhost:3128", null)!.Ports);
        Assert.Equal(new[] { 1080 }, SystemProxy.Parse(true, "socks=[::1]:1080", null)!.Ports);
        Assert.Equal(new[] { 80 }, SystemProxy.Parse(true, "http://127.0.0.1", null)!.Ports); // без порта — 80, как у Windows

        // PAC-файл с этого компьютера действует и без «Использовать прокси-сервер».
        var pac = SystemProxy.Parse(false, "127.0.0.1:10808", "http://127.0.0.1:10810/pac/?t=1")!;
        Assert.Equal(new[] { 10810 }, pac.Ports);
        Assert.Equal("127.0.0.1:10810", pac.Display);

        // Сам PAC-файл отправляет на другие порты: закрываются и они.
        const string script = "function FindProxyForURL(url, host) { var proxy = \"PROXY 127.0.0.1:10809; SOCKS5 localhost:10808; DIRECT\";\n" +
                              "  // HTTPS requests too\n  if (isPlainHostName(host)) return \"DIRECT\"; return proxy; }\n" +
                              "var corp = \"PROXY proxy.corp.local:8080\";";
        Assert.Equal(new[] { 10809, 10808 }, SystemProxy.PacPorts(script));
        var withPorts = SystemProxy.Parse(false, null, "http://127.0.0.1:10810/pac", SystemProxy.PacPorts(script))!;
        Assert.Equal(new[] { 10808, 10809, 10810 }, withPorts.Ports);
        Assert.Equal("127.0.0.1:10810, 127.0.0.1:10809, 127.0.0.1:10808", withPorts.Display);
        Assert.Null(SystemProxy.Parse(false, null, "http://wpad.corp.local/wpad.dat", new[] { 10809 })); // PAC не с этого компьютера

        Assert.Null(SystemProxy.Parse(false, "127.0.0.1:10808", null)); // выключен
        Assert.Null(SystemProxy.Parse(true, "proxy.corp.local:8080", "http://wpad.corp.local/wpad.dat")); // не на этом компьютере
        Assert.Null(SystemProxy.Parse(true, "", ""));
    }

    [Fact]
    public async Task SystemProxyWatcher_ReportsChanges()
    {
        LocalProxy? value = null;
        using var watcher = new SystemProxyWatcher(() => Volatile.Read(ref value), TimeSpan.FromMilliseconds(20));
        var changes = 0;
        watcher.Changed += () => Interlocked.Increment(ref changes);
        Assert.Null(watcher.Current);

        Volatile.Write(ref value, SystemProxy.Parse(true, "127.0.0.1:10808", null));
        await Until(() => Volatile.Read(ref changes) == 1);
        Assert.Equal(new[] { 10808 }, watcher.Current!.Ports);

        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref changes)); // не изменился — не сообщает

        Volatile.Write(ref value, null);
        await Until(() => Volatile.Read(ref changes) == 2);
        Assert.Null(watcher.Current);
    }

    [Fact]
    public async Task ApplyNow_Failure_ReturnsReason()
    {
        var backend = new FakeGuardBackend { Fail = "служба недоступна" };
        using var guard = new VpnGuard(() => backend);

        Assert.Equal("служба недоступна", await guard.ApplyNowAsync(Request()));
        Assert.False(guard.IsApplied);

        backend.Fail = null;
        Assert.Null(await guard.ApplyNowAsync(Request()));
        Assert.Single(backend.Applied);
    }

    [Fact]
    public async Task Submit_LatestWins_AndFailureReportedOnce()
    {
        var backend = new FakeGuardBackend { Fail = "нет связи" };
        using var guard = new VpnGuard(() => backend);
        var failures = new List<string>();
        var restored = 0;
        guard.Failed += failures.Add;
        guard.Restored += () => Interlocked.Increment(ref restored);

        guard.Submit(Request());
        await Until(() => failures.Count == 1);
        guard.Submit(Request(@"C:\Other\other.exe"));
        await Task.Delay(100);
        Assert.Single(failures); // одна и та же причина — один раз

        backend.Fail = null;
        guard.Submit(Request(@"C:\Third\third.exe"));
        await Until(() => guard.IsApplied);
        await Until(() => Volatile.Read(ref restored) == 1);
        lock (backend.Applied)
            Assert.Equal(@"C:\Third\third.exe", backend.Applied.Last().Apps[0].Paths[0]);
    }

    [Fact]
    public async Task ServiceLost_SetIsSentAgain()
    {
        var backends = new List<FakeGuardBackend>();
        using var guard = new VpnGuard(() =>
        {
            var backend = new FakeGuardBackend();
            lock (backends)
                backends.Add(backend);
            return backend;
        });

        Assert.Null(await guard.ApplyNowAsync(Request()));
        backends[0].RaiseLost(); // служба перезапустилась: её фильтры сняты

        await Until(() =>
        {
            lock (backends)
                return backends.Count == 2 && backends[1].Applied.Count == 1;
        });
        Assert.True(backends[0].Disposed);
    }

    [Fact]
    public async Task Notice_ReportedOnce()
    {
        var backend = new FakeGuardBackend { Notice = "Системные программы не ограничиваются: svchost.exe." };
        using var guard = new VpnGuard(() => backend);
        var notices = new List<string>();
        guard.Notice += notices.Add;

        await guard.ApplyNowAsync(Request());
        await guard.ApplyNowAsync(Request(@"C:\Other\other.exe"));

        Assert.Single(notices);
    }

    [Fact]
    public async Task SystemProxyWatcher_FirstReadAwaited()
    {
        using var gate = new ManualResetEventSlim();
        using var watcher = new SystemProxyWatcher(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(5)); // PAC-файл читается долго
            return new LocalProxy("127.0.0.1:10808", new[] { 10808 });
        }, TimeSpan.FromHours(1));
        Assert.Null(watcher.Current);

        var wait = watcher.WaitFirstReadAsync(TimeSpan.FromSeconds(5));
        Assert.False(wait.IsCompleted);
        gate.Set();
        await wait;
        Assert.Equal(new[] { 10808 }, watcher.Current!.Ports);
        await watcher.WaitFirstReadAsync(TimeSpan.Zero); // дальше — сразу
    }

    [Fact]
    public async Task Unguarded_RememberedUntilNextReply()
    {
        var backend = new FakeGuardBackend { Unguarded = { @"\\nas\apps\app.exe" } };
        using var guard = new VpnGuard(() => backend);

        await guard.ApplyNowAsync(Request(@"\\nas\apps\app.exe"));
        Assert.True(guard.IsUnguarded(@"\\NAS\Apps\app.exe")); // регистр пути не важен
        Assert.False(guard.IsUnguarded(@"C:\App\app.exe"));

        backend.Unguarded = new List<string>();
        await guard.ApplyNowAsync(Request());
        Assert.False(guard.IsUnguarded(@"\\nas\apps\app.exe"));
    }

    [Fact]
    public async Task DnsPaths_ChangeResends()
    {
        var backend = new FakeGuardBackend();
        using var guard = new VpnGuard(() => backend);
        var request = new GuardRequest { DnsInterfaces = new List<int> { 12 } };

        await guard.ApplyNowAsync(request);
        await guard.ApplyNowAsync(new GuardRequest { DnsInterfaces = new List<int> { 12 }, DnsPaths = new List<string> { @"C:\App\app.exe" } });
        await guard.ApplyNowAsync(new GuardRequest { DnsInterfaces = new List<int> { 12 }, DnsPaths = new List<string> { @"c:\app\APP.exe" } });

        Assert.Equal(2, backend.Applied.Count); // регистр пути не важен
        Assert.Equal(new[] { @"C:\App\app.exe" }, backend.Applied[1].DnsPaths);
    }

    [Fact]
    public async Task DisposedWhileConnecting_NotReportedAsApplied()
    {
        var backend = new FakeGuardBackend();
        VpnGuard? guard = null;
        guard = new VpnGuard(() =>
        {
            guard!.Dispose(); // лаунчер закрылся, пока шло подключение к службе
            return backend;
        });

        Assert.NotNull(await guard.ApplyNowAsync(Request()));
        Assert.True(backend.Disposed);
        Assert.Empty(backend.Applied);
    }

    [Fact]
    public async Task ServiceGone_ReportedAsText_NotThrown()
    {
        var guard = new VpnGuard(() => new FakeGuardBackend { Fail = "служба остановлена" });
        Assert.Equal("служба остановлена", await guard.ApplyNowAsync(Request()));

        guard.Dispose();
        Assert.NotNull(await guard.ApplyNowAsync(Request())); // лаунчер закрывается: сообщение, а не исключение
    }

    [Fact]
    public async Task Dispose_ClosesConnection_FiltersRemovedByService()
    {
        var backend = new FakeGuardBackend();
        var guard = new VpnGuard(() => backend);
        await guard.ApplyNowAsync(Request());

        guard.Dispose();

        Assert.True(backend.Disposed);
    }

    [Fact]
    public void ProxyVariables_RemovedFromLaunchedApps()
    {
        var variables = new Hashtable
        {
            ["PATH"] = @"C:\Windows",
            ["https_proxy"] = "http://127.0.0.1:10808",
            ["All_Proxy"] = "socks5://127.0.0.1:10808",
            ["NO_PROXY"] = "localhost"
        };

        var block = ProcessTracker.EnvironmentWithoutProxy(variables)!;
        var names = block.TrimEnd('\0').Split('\0').Select(v => v[..v.IndexOf('=')]).ToList();

        Assert.Equal(new[] { "NO_PROXY", "PATH" }, names); // по алфавиту, как требует Windows
        Assert.EndsWith("\0\0", block);
        Assert.Null(ProcessTracker.EnvironmentWithoutProxy(new Hashtable { ["PATH"] = "x" })); // нечего убирать — окружение как есть
    }
}
