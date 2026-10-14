using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using SafeLauncher.Logging;
using Xunit;
using Svc = SafeLauncher.Service;

namespace KillSwitchLauncher.Tests;

/// <summary>Выгрузка для анализа, фильтр технического журнала, причина перезапуска ядра, команды журналов в протоколе службы.</summary>
public class LogExportTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    private static LogLine L(int minutesAgo, string source, LogLevel level, string area, string code, string text, string cid = "-") =>
        new(Now.AddMinutes(-minutesAgo), source, level, area, code, cid, text);

    // ---- периоды ----

    [Fact]
    public void Periods_FixedOrder_AndOptionalVariants()
    {
        var plain = ExportPeriods.Build(Now, Now.AddHours(-2), null, null);
        Assert.Equal(new[] { ExportPeriods.Last5, ExportPeriods.Last15, ExportPeriods.LastHour, ExportPeriods.Last3Hours, ExportPeriods.Today, ExportPeriods.Session },
            plain.Select(p => p.Preset));
        Assert.Equal(ExportPeriods.LastHour, ExportPeriods.DefaultPreset(plain, Now, null, null));

        var crash = new PreviousSession(Now.AddHours(-5), Now.AddHours(-3), "NullReferenceException", "crash.txt");
        var all = ExportPeriods.Build(Now, Now.AddHours(-2), crash, Now.AddMinutes(-10));
        Assert.Equal(ExportPeriods.Mark, all[6].Preset);
        Assert.Equal(ExportPeriods.Previous, all[7].Preset);
        var mark = all[6];
        Assert.Equal(Now.AddMinutes(-25), mark.FromUtc);
        Assert.Equal(Now, mark.ToUtc); // не в будущее
        Assert.Equal((Now.AddHours(-5), Now.AddHours(-2)), (all[7].FromUtc, all[7].ToUtc));

        // свежая отметка важнее сбоя; просьба вызвавшего важнее всего
        Assert.Equal(ExportPeriods.Mark, ExportPeriods.DefaultPreset(all, Now, Now.AddMinutes(-10), null));
        Assert.Equal(ExportPeriods.Previous, ExportPeriods.DefaultPreset(all, Now, Now.AddHours(-3), null));
        Assert.Equal(ExportPeriods.Last15, ExportPeriods.DefaultPreset(all, Now, Now.AddMinutes(-10), ExportPeriods.Last15));
    }

    [Fact]
    public void FileName_HasEndAndDuration()
    {
        var name = ExportPeriods.FileName(Now.AddHours(-1), Now);
        Assert.Matches(@"^SafeLauncher-logs-\d{8}-\d{4}-60m\.zip$", name);
        Assert.Equal("15m", ExportPeriods.Duration(TimeSpan.FromMinutes(15)));
        Assert.Equal("3h", ExportPeriods.Duration(TimeSpan.FromHours(3)));
        Assert.Equal("7d", ExportPeriods.Duration(TimeSpan.FromDays(7)));
    }

    // ---- архив ----

    private static ExportInput Input(string? serviceProblem = null) => new()
    {
        Launcher = new[]
        {
            L(50, LogSources.Launcher, LogLevel.Info, LogAreas.Network, LogCodes.CoreRestart, "перезапуск: изменено подключение «Работа»", "L0001"),
            L(30, LogSources.Launcher, LogLevel.Info, LogAreas.Marker, LogCodes.Marker, "Отметка пользователя: WG перестал работать", "L0002"),
            L(29, LogSources.Core, LogLevel.Debug, LogAreas.Check, LogCodes.CoreLine, "проверка: соединение с 203.0.113.7:51820"),
            L(90, LogSources.Launcher, LogLevel.Info, LogAreas.App, LogCodes.AppLaunch, "вне периода")
        },
        Service = serviceProblem is not null ? Array.Empty<LogLine>() : new[]
        {
            L(49, LogSources.Service, LogLevel.Info, LogAreas.Core, LogCodes.CoreStart, "ядро PID 4120 запущено", "L0001"),
            L(48, LogSources.Core, LogLevel.Info, LogAreas.Main, LogCodes.CoreLine, "outbound/wireguard: handshake with 203.0.113.7 via mail.example.org"),
            L(10, LogSources.Core, LogLevel.Info, LogAreas.Main, LogCodes.CoreLine, "далеко от отметки, не ошибка"),
            L(9, LogSources.Core, LogLevel.Error, LogAreas.Main, LogCodes.CoreLine, "далеко от отметки, ошибка")
        },
        ServiceProblem = serviceProblem,
        ServiceFiles = serviceProblem is not null ? Array.Empty<ExportFile>() : new[]
        {
            new ExportFile("core-config-20261014-081100-000.json", Now.AddMinutes(-49),
                """{"endpoints":[{"type":"wireguard","private_key":"СЕКРЕТ","peers":[{"address":"203.0.113.7","port":51820}]}],"route":{"rules":[{"domain":["mail.example.org"]}]}}"""),
            new ExportFile("crash-20261014-081500-000.txt", Now.AddMinutes(-45), "Тип: System.InvalidOperationException")
        },
        Events = new[] { new ActivityEntry(Now.AddMinutes(-20).ToLocalTime(), LogKind.VpnDown, "Сервер подключения «Работа» не отвечает") },
        Settings = new JsonObject { ["connections"] = new JsonArray(new JsonObject { ["name"] = "Работа", ["server"] = "203.0.113.7", ["private_key"] = "СЕКРЕТ" }) },
        Environment = new JsonObject { ["adapters"] = new JsonArray(new JsonObject { ["addresses"] = new JsonArray("192.168.1.20", "203.0.113.50") }) },
        State = new JsonObject { ["running"] = new JsonArray() },
        Tags = new JsonObject { ["c0123"] = new JsonObject { ["name"] = "Работа" } },
        Dns = new[] { new ExportDnsJournal("Chrome: рабочий", new[] { new DnsEntry { Time = Now.AddMinutes(-5).ToLocalTime(), Domain = "mail.example.org", Type = 1, ProcessId = 7, ProcessName = "chrome" } }) },
        LauncherVersion = "2.1.0",
        ServiceVersion = "2.1.0",
        Protocol = 5
    };

    private static Dictionary<string, string> Unzip(MemoryStream zip)
    {
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    private static (ExportResult Result, Dictionary<string, string> Files) Export(ExportOptions options, ExportInput input)
    {
        var masker = new LogMasker();
        masker.SetKey(new byte[32]);
        using var stream = new MemoryStream();
        var result = LogExport.Write(stream, options, input, masker, Now);
        return (result, Unzip(stream));
    }

    private static ExportOptions Options(bool hideIps = false, bool hideSites = false, bool dns = false, bool coreErrors = false, string? note = null) => new()
    {
        Period = new ExportPeriod(ExportPeriods.LastHour, "Последний час", Now.AddHours(-1), Now),
        HideAddresses = hideIps,
        HideSites = hideSites,
        IncludeDns = dns,
        CoreErrorsOnly = coreErrors,
        Note = note
    };

    [Fact]
    public void Zip_HasAllParts_ByPeriod_AndBySource()
    {
        var (result, files) = Export(Options(note: "  WG перестал работать  "), Input());

        foreach (var name in new[] { "summary.json", "timeline.log", "launcher.log", "service.log", "core.log", "events.txt", "settings.json",
                     "environment.json", "state.json", "core-config.json", "core-configs/core-config-20261014-081100-000.json",
                     "crashes/service-crash-20261014-081500-000.txt" })
            Assert.True(files.ContainsKey(name), name);
        Assert.False(files.ContainsKey("notes.txt"));
        Assert.DoesNotContain(files.Keys, k => k.StartsWith("dns/", StringComparison.Ordinal)); // не отмечено

        var timeline = files["timeline.log"].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => { LogLine.TryParse(l, out var x); return x; }).ToList();
        Assert.Equal(7, timeline.Count); // строка «вне периода» не попала
        Assert.Equal(timeline.OrderBy(l => l.TimeUtc).Select(l => l.TimeUtc), timeline.Select(l => l.TimeUtc));
        Assert.Equal(7, result.Lines);
        Assert.All(files["core.log"].Split('\n', StringSplitOptions.RemoveEmptyEntries), l => Assert.Contains("\tcore\t", l));
        Assert.Contains("проверка: соединение", files["core.log"]); // ядро проверки (писал лаунчер) тоже в core.log
        Assert.Contains("VpnDown\tСервер подключения «Работа» не отвечает", files["events.txt"]);

        // адреса по умолчанию как есть, секреты — никогда
        Assert.Contains("203.0.113.7", files["timeline.log"]);
        Assert.DoesNotContain("СЕКРЕТ", string.Concat(files.Values));
        Assert.Contains("***", files["settings.json"]);
        Assert.Contains("***", files["core-config.json"]);

        var summary = JsonNode.Parse(files["summary.json"])!;
        Assert.Equal(1, (int)summary["format"]!);
        Assert.Equal("last-hour", (string)summary["period"]!["preset"]!);
        Assert.Equal("WG перестал работать", (string)summary["userNote"]!);
        Assert.Equal(4, (int)summary["coverage"]!["core"]!["lines"]!);
        Assert.False((bool)summary["masking"]!["ips"]!);
        Assert.Equal("L0002", (string)summary["markers"]![0]!["cid"]!);
        var start = summary["coreStarts"]![0]!;
        Assert.Equal("L0001", (string)start["cid"]!);
        Assert.Equal("core-configs/core-config-20261014-081100-000.json", (string)start["configFile"]!);
        Assert.Equal("Работа", (string)summary["tags"]!["c0123"]!["name"]!);
        Assert.Equal("summary.json", (string)summary["files"]![0]!);
        Assert.Contains("timeline.log", summary["files"]!.AsArray().Select(f => (string)f!));
    }

    [Fact]
    public void Zip_HidesAddressesAndSites_WhenAsked()
    {
        var (_, files) = Export(Options(hideIps: true, hideSites: true, note: "сервер 203.0.113.7"), Input());
        var all = string.Concat(files.Values);

        Assert.DoesNotContain("203.0.113.7", all);
        Assert.DoesNotContain("203.0.113.50", all);
        Assert.DoesNotContain("mail.example.org", all);
        Assert.DoesNotContain("192.168.1.20", files["environment.json"]);
        Assert.Contains("{ip4-lan-", files["environment.json"]); // локальный адрес — своей меткой: видно, что он локальный
        Assert.Contains("{site-", files["core-config.json"]);
        Assert.Contains("{ip4", files["timeline.log"]);
        var summary = JsonNode.Parse(files["summary.json"])!;
        Assert.True((bool)summary["masking"]!["ips"]!);
        Assert.True((bool)summary["masking"]!["domains"]!);
        Assert.DoesNotContain("203.0.113.7", (string)summary["userNote"]!);
    }

    [Fact]
    public void Zip_WithoutService_SaysWhy_AndKeepsLauncher()
    {
        var (_, files) = Export(Options(), Input(ServiceLogs.OldServiceMessage));
        Assert.Contains("старой версии", files["notes.txt"]);
        Assert.Contains("перезапуск", files["launcher.log"]);
        Assert.Equal(string.Empty, files["service.log"]);
        Assert.False(files.ContainsKey("core-config.json"));
        Assert.Equal(ServiceLogs.OldServiceMessage, (string)JsonNode.Parse(files["summary.json"])!["serviceProblem"]!);
    }

    [Fact]
    public void Zip_CoreErrorsOnly_KeepsErrorsAndLinesNearMarks()
    {
        var (_, files) = Export(Options(coreErrors: true), Input());
        var core = files["core.log"];
        Assert.DoesNotContain("handshake", core);     // 18 минут от отметки, не ошибка
        Assert.Contains("проверка: соединение", core); // минута от отметки
        Assert.Contains("далеко от отметки, ошибка", core);
        Assert.DoesNotContain("далеко от отметки, не ошибка", core);
    }

    [Fact]
    public void Zip_Dns_ByApp_WithSafeNames()
    {
        var (_, files) = Export(Options(dns: true, hideSites: true), Input());
        var dns = Assert.Single(files.Keys, k => k.StartsWith("dns/", StringComparison.Ordinal));
        Assert.Equal("dns/Chrome_ рабочий.log", dns);
        Assert.Contains("chrome (7)", files[dns]);
        Assert.DoesNotContain("mail.example.org", files[dns]);
    }

    [Fact]
    public void Zip_EmptyPeriod_StillHasSnapshots()
    {
        var options = new ExportOptions { Period = new ExportPeriod(ExportPeriods.Custom, "Свой", Now.AddDays(-3), Now.AddDays(-2)) };
        var (result, files) = Export(options, Input());
        Assert.True(result.Empty);
        Assert.True(files.ContainsKey("settings.json"));
        Assert.Equal(string.Empty, files["timeline.log"]);
    }

    // ---- фильтр окна технического журнала ----

    [Fact]
    public void Filter_BySourceErrorsTypesAndSearch()
    {
        var lines = new[]
        {
            L(5, LogSources.Launcher, LogLevel.Info, LogAreas.App, LogCodes.AppLaunch, "Запуск «Discord»", "L00aa"),
            L(4, LogSources.Service, LogLevel.Warn, LogAreas.Wfp, LogCodes.WfpFail, "фильтры не применились"),
            L(3, LogSources.Core, LogLevel.Info, LogAreas.Main, LogCodes.CoreLine, "outbound/wireguard: handshake"),
            L(2, LogSources.Launcher, LogLevel.Info, LogAreas.Marker, LogCodes.Marker, "Отметка пользователя")
        };
        List<string> Run(TechLogFilter f) => lines.Where(f.Compile()).Select(l => l.Text).ToList();

        Assert.Equal(4, Run(new TechLogFilter()).Count);
        Assert.Equal(new[] { "outbound/wireguard: handshake" }, Run(new TechLogFilter { Source = LogSources.Core }));
        // только ошибки: отметки остаются, по ним ищут место проблемы
        Assert.Equal(new[] { "фильтры не применились", "Отметка пользователя" }, Run(new TechLogFilter { ErrorsOnly = true }));
        // типы: строки ядра скрыты, кроме вкладки «Ядро»
        var apps = new HashSet<string> { "apps" };
        Assert.Equal(new[] { "Запуск «Discord»", "Отметка пользователя" }, Run(new TechLogFilter { Groups = apps }));
        Assert.Equal(new[] { "outbound/wireguard: handshake" }, Run(new TechLogFilter { Groups = apps, Source = LogSources.Core }));
        // поиск: слова, точный код, id операции
        Assert.Equal(new[] { "фильтры не применились" }, Run(new TechLogFilter { Search = "code:wfp" }));
        Assert.Equal(new[] { "outbound/wireguard: handshake" }, Run(new TechLogFilter { Search = "WIREGUARD handshake" }));
        Assert.Equal(new[] { "Запуск «Discord»" }, Run(new TechLogFilter { Search = "l00aa" }));
    }

    [Fact]
    public void Filter_ShowsTagsAsNames()
    {
        var id = Guid.NewGuid();
        var tag = CorePlan.ConnectionTag(id);
        var names = new Dictionary<string, string> { [tag] = "Работа" };
        Assert.Equal("outbound/wireguard[«Работа»]: ok", TechLogFilter.WithNames($"outbound/wireguard[{tag}]: ok", names));
        var line = new LogLine(Now, LogSources.Core, LogLevel.Info, LogAreas.Main, LogCodes.CoreLine, "-", $"x {tag}");
        Assert.Equal($"# теги: {tag}=Работа", TechLogFilter.TagsComment(new[] { line }, names));
    }

    // ---- причина перезапуска ядра (§4.4) ----

    private const string Link =
        "vless://b831381d-6324-4d53-ad4f-8cda48b30811@de.example.com:443?type=tcp&security=reality&pbk=A7zccni_SyWduZFcIk3dpRLbcvJ-w6pRkQzKD5OohDU&sni=www.microsoft.com#DE";

    private static (AppSettings Settings, VpnConnection Connection) LauncherSettings()
    {
        var connection = new VpnConnection { Name = "Работа", Kind = ConnectionKind.Link, Link = Link };
        var settings = new AppSettings { NetworkMode = GlobalNetworkMode.LauncherConnection, ConnectionId = connection.Id, Connections = new List<VpnConnection> { connection } };
        settings.Apps.Add(new AppEntry { Name = "Discord", ExePath = @"C:\Discord\Discord.exe" });
        return (settings, connection);
    }

    [Fact]
    public void PlanDiff_NamesWhatChanged()
    {
        var (settings, _) = LauncherSettings();
        var before = CorePlan.Build(settings, ipv6: false);

        Assert.Equal("первый запуск ядра", CorePlanDiff.Describe(null, before));
        Assert.Equal("настройки не менялись", CorePlanDiff.Describe(before, CorePlan.Build(settings, ipv6: false)));
        Assert.Contains("IPv6: нет → да", CorePlanDiff.Describe(before, CorePlan.Build(settings, ipv6: true)));

        settings.Apps.Add(new AppEntry { Name = "Chrome", ExePath = @"C:\Chrome\chrome.exe" });
        var added = CorePlanDiff.Describe(before, CorePlan.Build(settings, ipv6: false));
        Assert.Contains("добавлено приложение «Chrome» через «Работа»", added);
    }

    [Fact]
    public void PlanDiff_ConnectionFields()
    {
        var (settings, connection) = LauncherSettings();
        var before = CorePlan.Build(settings, ipv6: false);
        connection.Link = Link.Replace(":443", ":8443", StringComparison.Ordinal);
        var diff = CorePlanDiff.Describe(before, CorePlan.Build(settings, ipv6: false));
        Assert.Contains("изменено подключение «Работа»: server_port 443 → 8443", diff);
        Assert.DoesNotContain("b831381d", diff); // uuid не пишется
    }

    // ---- протокол службы (Revision 5) ----

    [Fact]
    public void LogCommand_ReachesService()
    {
        var message = new CoreServiceClient.Message
        {
            Id = 3,
            Op = ServiceLogs.ControlOps.LogRead,
            Cid = "L1a2b",
            Logs = new ServiceLogRequest { FromUtc = Now.AddHours(-1), ToUtc = Now, Sources = new List<string> { "core" }, Cursor = "c:x", MaxBytes = 1024 }
        };
        var received = JsonSerializer.Deserialize<Svc.ControlMessage>(JsonSerializer.Serialize(message, CoreServiceClient.JsonOptions),
            Svc.ControlProtocol.JsonOptions)!;

        Assert.Equal(Svc.ControlProtocol.OpLogRead, received.Op);
        Assert.Equal("L1a2b", received.Cid);
        Assert.Equal(Now, received.Logs!.ToUtc!.Value.ToUniversalTime());
        Assert.Equal(new[] { "core" }, received.Logs.Sources);
        Assert.Equal(("c:x", 1024), (received.Logs.Cursor, received.Logs.MaxBytes));
    }

    [Fact]
    public void LogReply_ReachesLauncher()
    {
        var reply = new Svc.ControlReply
        {
            Id = 3,
            Ok = true,
            Logs = new Svc.LogReply
            {
                MaskKey = "a2V5", ServiceBytes = 10, CoreBytes = 20, Dropped = 1, Detailed = true, Lines = new List<string> { "строка" }, Cursor = "s:1",
                Files = new List<Svc.LogFileItem> { new() { Name = "core-config-x.json", Utc = Now, Content = "{}" } }, Freed = 30
            }
        };
        var received = JsonSerializer.Deserialize<CoreServiceClient.Reply>(JsonSerializer.Serialize(reply, Svc.ControlProtocol.JsonOptions),
            CoreServiceClient.JsonOptions)!;

        var logs = received.Logs!;
        Assert.Equal(("a2V5", 10L, 20L, 1L, true), (logs.MaskKey, logs.ServiceBytes!.Value, logs.CoreBytes!.Value, logs.Dropped!.Value, logs.Detailed!.Value));
        Assert.Equal(new[] { "строка" }, logs.Lines);
        Assert.Equal("core-config-x.json", Assert.Single(logs.Files!).Name);
        Assert.Equal(30L, logs.Freed);
    }

    [Fact]
    public void LogOps_Match_AndRevision()
    {
        Assert.Equal(Svc.ControlProtocol.OpLogConfig, ServiceLogs.ControlOps.LogConfig);
        Assert.Equal(Svc.ControlProtocol.OpLogRead, ServiceLogs.ControlOps.LogRead);
        Assert.Equal(Svc.ControlProtocol.OpLogMark, ServiceLogs.ControlOps.LogMark);
        Assert.Equal(Svc.ControlProtocol.OpLogClear, ServiceLogs.ControlOps.LogClear);
        Assert.Equal(Svc.ControlProtocol.OpLogFiles, ServiceLogs.ControlOps.LogFiles);
        Assert.True(Svc.ControlProtocol.Revision >= 5);
    }

    [Fact]
    public void OldService_UnknownCommand_IsExplained()
    {
        Assert.Equal(ServiceLogs.OldServiceMessage, ServiceLogs.Explain(new CoreServiceException("Неизвестная команда: logRead")));
        Assert.Equal("другое", ServiceLogs.Explain(new CoreServiceException("другое")));
    }
}
