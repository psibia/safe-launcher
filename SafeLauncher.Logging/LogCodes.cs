using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SafeLauncher.Logging;

/// <summary>Источник записи: кто ее сделал. В интерфейсе три источника: Лаунчер, Служба, Ядро.</summary>
public static class LogSources
{
    public const string Launcher = "launcher";
    public const string Service = "service";
    public const string Core = "core";

    public static readonly string[] All = { Launcher, Service, Core };

    public static string DisplayName(string source) => source switch
    {
        Launcher => "Лаунчер",
        Service => "Служба",
        Core => "Ядро",
        _ => source
    };
}

/// <summary>Область записи (закрытый список, см. notes/logging/01-system-concept.md §4).</summary>
public static class LogAreas
{
    // общие
    public const string Session = "session";
    public const string Marker = "marker";

    // лаунчер
    public const string Event = "event";
    public const string Settings = "settings";
    public const string App = "app";
    public const string KillSwitch = "killswitch";
    public const string Network = "network";
    public const string Guard = "guard";
    public const string Owner = "owner";
    public const string Monitor = "monitor";
    public const string Check = "check";
    public const string Subs = "subs";
    public const string Dns = "dns";
    public const string Update = "update";

    // служба
    public const string Control = "control";
    public const string Core = "core";
    public const string Wfp = "wfp";
    public const string Net = "net";

    // ядро
    public const string Main = "main";

    public static IReadOnlyList<string> All { get; } = typeof(LogAreas)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();
}

/// <summary>
/// Коды событий <c>объект.действие</c>. Код не меняется при правке текста, по нему ищут (<c>code:core.restart</c>) и
/// разбирают журнал. Новые коды добавлять только сюда; тест проверяет, что коды уникальны.
/// </summary>
public static class LogCodes
{
    public const string SessionStart = "session.start";
    public const string SessionEnd = "session.end";
    public const string SessionCrashed = "session.crashed";
    public const string Crash = "crash";
    public const string CrashTask = "crash.task";
    public const string LogConfig = "log.config";
    public const string LogDropped = "log.dropped";
    public const string LogDetailed = "log.detailed";
    public const string LogClear = "log.clear";
    public const string Marker = "marker";

    public const string Event = "event";
    public const string SettingsChange = "settings.change";

    public const string AppLaunch = "app.launch";
    public const string AppStarted = "app.started";
    public const string AppReject = "app.reject";
    public const string AppExit = "app.exit";
    public const string AppAdopt = "app.adopt";
    public const string AppProcesses = "app.processes";
    public const string KsClose = "ks.close";

    public const string CoreStart = "core.start";
    public const string CoreRestart = "core.restart";
    public const string CoreReady = "core.ready";
    public const string CoreStop = "core.stop";
    public const string CoreIdle = "core.idle";
    public const string CoreExit = "core.exit";
    public const string CoreLost = "core.lost";
    public const string CoreFail = "core.fail";
    public const string CoreFreeze = "core.freeze";
    public const string CoreConfig = "core.config";
    public const string CoreLine = "core.line";
    public const string DnsFlush = "dns.flush";
    public const string ProbeOk = "probe.ok";
    public const string ProbeFail = "probe.fail";
    public const string ConnDown = "conn.down";
    public const string ConnUp = "conn.up";
    public const string Ipv6Detect = "ipv6.detect";

    public const string GuardApply = "guard.apply";
    public const string GuardFail = "guard.fail";
    public const string GuardRestored = "guard.restored";
    public const string WfpApply = "wfp.apply";
    public const string WfpFail = "wfp.fail";

    public const string OwnerStats = "owner.stats";
    public const string OwnerUnknown = "owner.unknown";
    public const string OwnerError = "owner.error";

    public const string AdapterChange = "adapter.change";
    public const string NetSnapshot = "net.snapshot";
    public const string ProxyChange = "proxy.change";

    public const string CheckStart = "check.start";
    public const string CheckResult = "check.result";
    public const string CheckFail = "check.fail";

    public const string SubsUpdate = "subs.update";
    public const string SubsFail = "subs.fail";

    public const string DnsJournal = "dns.journal";
    public const string UpdateCheck = "update.check";
    public const string UpdateInstall = "update.install";
    public const string UpdateFail = "update.fail";

    public const string CmdStart = "cmd.start";
    public const string CmdStop = "cmd.stop";
    public const string CmdStatus = "cmd.status";
    public const string CmdGuard = "cmd.guard";
    public const string CmdLog = "cmd.log";
    public const string CmdError = "cmd.error";
    public const string CmdReject = "cmd.reject";
    public const string ClientConnect = "client.connect";
    public const string ClientDisconnect = "client.disconnect";

    public static IReadOnlyList<string> All { get; } = typeof(LogCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();
}

/// <summary>Группа «Типы событий» в окне технического журнала: подпись и области, которые она включает.</summary>
public sealed record LogAreaGroup(string Key, string Title, IReadOnlyList<string> Areas);

public static class LogAreaGroups
{
    public static IReadOnlyList<LogAreaGroup> All { get; } = new[]
    {
        // Названия и состав как в фильтре «Типы событий» окна технического журнала (02-user-journeys-ui.md, UI-3).
        new LogAreaGroup("apps", "Запуски приложений", new[] { LogAreas.App }),
        new LogAreaGroup("killswitch", "Kill-Switch", new[] { LogAreas.KillSwitch }),
        new LogAreaGroup("network", "Подключения", new[] { LogAreas.Network, LogAreas.Check, LogAreas.Subs }),
        new LogAreaGroup("guard", "Защита", new[] { LogAreas.Guard, LogAreas.Wfp, LogAreas.Owner }),
        new LogAreaGroup("settings", "Настройки", new[] { LogAreas.Settings }),
        new LogAreaGroup("system", "Сеть Windows", new[] { LogAreas.Monitor, LogAreas.Net, LogAreas.Dns }),
        new LogAreaGroup("update", "Обновления", new[] { LogAreas.Update }),
        new LogAreaGroup("service", "Служба", new[] { LogAreas.Control, LogAreas.Core, LogAreas.Session }),
        new LogAreaGroup("events", "Журнал событий", new[] { LogAreas.Event }),
        new LogAreaGroup("marker", "Отметки", new[] { LogAreas.Marker })
    };

    public static LogAreaGroup? Of(string area) => All.FirstOrDefault(g => g.Areas.Contains(area, StringComparer.Ordinal));
}
