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

/// <summary>Настройки и приложения для тестов подключений лаунчера.</summary>
internal static class NetworkSamples
{
    public const string Trojan = "trojan://secret@5.6.7.8:443?sni=example.com#Сервер";

    public static VpnConnection Link(string name, string link = Trojan) => new() { Name = name, Kind = ConnectionKind.Link, Link = link };

    public static AppEntry App(string name, AppNetworkMode mode = AppNetworkMode.Inherit, Guid? connection = null,
        ProtectionOverride closeOnDrop = ProtectionOverride.Inherit, ProtectionOverride requireVpn = ProtectionOverride.Inherit,
        params string[] domains) => new()
    {
        Name = name,
        ExePath = $@"C:\Apps\{name}\{name}.exe",
        Network = new AppNetwork
        {
            Mode = mode,
            ConnectionId = connection,
            CloseOnDrop = closeOnDrop,
            RequireVpn = requireVpn,
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

        public void Crash() => Lost?.Invoke("упало", new List<string>());

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

        public IReadOnlyList<Guid> RunningApps()
        {
            lock (Running)
                return Running.ToList();
        }

        public IDisposable Freeze(IReadOnlyCollection<Guid> appIds)
        {
            Frozen++;
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
            ProbeRetryPause = TimeSpan.FromMilliseconds(10)
        };

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
    public async Task ServerNotAnswering_LaunchRefused_WhenRequired()
    {
        var connection = NetworkSamples.Link("Германия");
        var strict = NetworkSamples.App("Строгое");
        var relaxed = NetworkSamples.App("Без проверки", requireVpn: ProtectionOverride.Off);
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, strict, relaxed), false));
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

        Assert.Null(await network.PrepareLaunchAsync(relaxed.Id));
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
    public async Task CoreCrash_ClosesEveryAppThroughCore()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A", closeOnDrop: ProtectionOverride.Off);
        var other = Guid.NewGuid();
        using var network = Network();
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);
        _processes.Running.Add(other);
        KillReport? report = null;
        network.CoreLost += (_, r) => report = r;

        _backend.Crash();

        Assert.NotNull(report);
        Assert.Equal(new[] { a.Id }, _processes.Killed); // даже без «Закрывать при обрыве»
        Assert.Contains(other, _processes.Running);
        Assert.False(network.IsCoreRunning);
        Assert.True(_backend.Disposed);
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
    public async Task CoreStops_WhenLastAppExits()
    {
        var connection = NetworkSamples.Link("Германия");
        var a = NetworkSamples.App("A");
        using var network = Network(TimeSpan.FromMilliseconds(100));
        network.Update(CorePlan.Build(NetworkSamples.Settings(connection, a), false));
        await network.PrepareLaunchAsync(a.Id);
        _processes.Running.Add(a.Id);

        network.OnProcessesChanged();
        await Task.Delay(400);
        Assert.Equal(0, _backend.Stops); // приложение ещё работает

        lock (_processes.Running)
            _processes.Running.Clear();
        network.OnProcessesChanged();
        Assert.True(await Wait.UntilAsync(() => _backend.Stops == 1, TimeSpan.FromSeconds(5)));
        Assert.False(network.IsCoreRunning);
    }
}

public class CoreApiClientTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = "{\"delay\":42}";
        public Exception? Throw;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (Throw is not null)
                return Task.FromException<HttpResponseMessage>(Throw);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
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
        using var api = new CoreApiClient(9090, "ключ", new Handler { Throw = new HttpRequestException("Подключение не установлено") });

        Assert.Contains("нет связи с управлением ядра", (await api.ProbeAsync("c1", TimeSpan.FromSeconds(5))).Failure);
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
