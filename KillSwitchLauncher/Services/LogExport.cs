using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.Services;

/// <summary>Готовый период выгрузки: id варианта, подпись в окне, границы в UTC.</summary>
public sealed record ExportPeriod(string Preset, string Title, DateTime FromUtc, DateTime ToUtc);

/// <summary>Варианты периода выгрузки (концепция §5.2) и выбор по умолчанию.</summary>
public static class ExportPeriods
{
    public const string Last5 = "last-5m";
    public const string Last15 = "last-15m";
    public const string LastHour = "last-hour";
    public const string Last3Hours = "last-3h";
    public const string Today = "today";
    public const string Session = "session";
    public const string Mark = "mark";
    public const string Previous = "previous";
    public const string Custom = "custom";

    public static readonly TimeSpan AroundMark = TimeSpan.FromMinutes(15);

    /// <summary>Самый короткий период выгрузки (решение Павла 14.10): и среди готовых вариантов, и для своего промежутка.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(5);

    /// <summary>Отметка не старше этого: вариант «Вокруг отметки» выбран по умолчанию.</summary>
    public static readonly TimeSpan RecentMark = TimeSpan.FromHours(2);

    /// <summary>Порядок фиксированный, от короткого к длинному: выбор по умолчанию не переставляется наверх.</summary>
    public static IReadOnlyList<ExportPeriod> Build(DateTime nowUtc, DateTime sessionStartUtc, PreviousSession? previous, DateTime? markUtc)
    {
        var todayUtc = nowUtc.ToLocalTime().Date.ToUniversalTime();
        var list = new List<ExportPeriod>
        {
            new(Last5, "Последние 5 минут", nowUtc - MinDuration, nowUtc),
            new(Last15, "Последние 15 минут", nowUtc.AddMinutes(-15), nowUtc),
            new(LastHour, "Последний час", nowUtc.AddHours(-1), nowUtc),
            new(Last3Hours, "Последние 3 часа", nowUtc.AddHours(-3), nowUtc),
            new(Today, "Сегодня", todayUtc, nowUtc),
            new(Session, $"Текущий сеанс лаунчера (с {Local(sessionStartUtc)})", sessionStartUtc, nowUtc)
        };
        if (markUtc is { } mark)
        {
            var to = mark + AroundMark;
            list.Add(new(Mark, $"Вокруг отметки {Local(mark)} (±15 мин)", mark - AroundMark, to < nowUtc ? to : nowUtc));
        }
        if (previous is not null)
        {
            list.Add(new(Previous, previous.CrashType is null ? "Предыдущий сеанс (закрылся аварийно)" : "Предыдущий сеанс (закрылся с ошибкой)",
                previous.StartUtc, sessionStartUtc));
        }
        return list;
    }

    /// <summary>Выбор по умолчанию: просьба вызвавшего (ошибка подключения → 15 минут), свежая отметка, сбой прошлого сеанса, последний час.</summary>
    public static string DefaultPreset(IReadOnlyList<ExportPeriod> periods, DateTime nowUtc, DateTime? markUtc, string? requested)
    {
        bool Has(string preset) => periods.Any(p => p.Preset == preset);
        if (requested is not null && Has(requested))
            return requested;
        if (markUtc is { } mark && nowUtc - mark <= RecentMark && Has(Mark))
            return Mark;
        if (Has(Previous))
            return Previous;
        return LastHour;
    }

    /// <summary>Имя архива: SafeLauncher-logs-20261014-1141-60m.zip (конец периода, местное время, и длительность).</summary>
    public static string FileName(DateTime fromUtc, DateTime toUtc) =>
        $"SafeLauncher-logs-{toUtc.ToLocalTime():yyyyMMdd-HHmm}-{Duration(toUtc - fromUtc)}.zip";

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span < TimeSpan.FromHours(2))
            return Math.Max(1, (int)Math.Round(span.TotalMinutes)) + "m";
        if (span < TimeSpan.FromHours(48))
            return (int)Math.Round(span.TotalHours) + "h";
        return (int)Math.Round(span.TotalDays) + "d";
    }

    private static string Local(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm", CultureInfo.InvariantCulture) : local.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
    }
}

/// <summary>Галочки окна выгрузки (концепция §5.4).</summary>
public sealed class ExportOptions
{
    public ExportPeriod Period { get; init; } = new(ExportPeriods.LastHour, "Последний час", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

    public bool HideAddresses { get; init; }

    public bool HideSites { get; init; }

    public bool IncludeDns { get; init; }

    /// <summary>Строки ядра только WARN/ERROR, кроме ±15 минут вокруг отметок (для очень больших выгрузок, §5.5).</summary>
    public bool CoreErrorsOnly { get; init; }

    public string? Note { get; init; }
}

/// <summary>Файл, который кладется в архив как есть (после маскировки): конфиг ядра, описание падения.</summary>
public sealed record ExportFile(string Name, DateTime Utc, string Content);

/// <summary>Журнал DNS приложения за период.</summary>
public sealed record ExportDnsJournal(string AppName, IReadOnlyList<DnsEntry> Entries);

/// <summary>Все, что собрано для выгрузки. Собирает лаунчер (журналы, служба, снимки), пишет <see cref="LogExport.Write"/>.</summary>
public sealed class ExportInput
{
    public IReadOnlyList<LogLine> Launcher { get; init; } = Array.Empty<LogLine>();

    /// <summary>Строки службы и ядра (источники service и core).</summary>
    public IReadOnlyList<LogLine> Service { get; init; } = Array.Empty<LogLine>();

    /// <summary>Почему журналов службы нет (служба старая, не установлена, не ответила), иначе null.</summary>
    public string? ServiceProblem { get; init; }

    /// <summary>Очищенные конфиги ядра и описания падений службы.</summary>
    public IReadOnlyList<ExportFile> ServiceFiles { get; init; } = Array.Empty<ExportFile>();

    /// <summary>Описания падений лаунчера.</summary>
    public IReadOnlyList<ExportFile> LauncherCrashes { get; init; } = Array.Empty<ExportFile>();

    public IReadOnlyList<ActivityEntry> Events { get; init; } = Array.Empty<ActivityEntry>();

    public IReadOnlyList<ExportDnsJournal> Dns { get; init; } = Array.Empty<ExportDnsJournal>();

    public JsonObject? Settings { get; init; }

    public JsonObject? Environment { get; init; }

    public JsonObject? State { get; init; }

    public JsonObject? Tags { get; init; }

    public LogSettings Logging { get; init; } = new();

    public bool MaskKeyShared { get; init; }

    public string LauncherVersion { get; init; } = string.Empty;

    public string? ServiceVersion { get; init; }

    public int? Protocol { get; init; }

    public long LauncherDropped { get; init; }

    public long? ServiceDropped { get; init; }
}

/// <summary>Итог сборки архива.</summary>
public sealed record ExportResult(int Lines, IReadOnlyList<string> Files, bool Empty);

/// <summary>
/// Выгрузка для анализа (концепция §5.3): zip со сводкой, общей лентой, журналами по источникам, Журналом событий, снимками
/// настроек, окружения и состояния, конфигами ядра и описаниями падений. Секреты не попадают никогда; адреса и имена сайтов
/// скрываются по галочкам.
/// </summary>
public static class LogExport
{
    public const int Format = 1;

    /// <summary>Больше этого (несжатого) окно выгрузки советует выбрать период короче.</summary>
    public const long LargeBytes = 100L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Примерный размер несжатых журналов (для подписи в окне и совета взять период короче).</summary>
    public static long EstimateBytes(IEnumerable<LogLine> lines) =>
        lines.Sum(l => (long)(l.Text.Length * 2 + 60));

    /// <summary>Сжатый архив примерно во столько раз меньше (текст журналов сжимается в 8–15 раз).</summary>
    public static long Compressed(long bytes) => bytes / 9 + 4096;

    public static ExportResult Write(Stream output, ExportOptions options, ExportInput input, LogMasker masker, DateTime createdUtc)
    {
        var period = options.Period;
        string Clean(string? text)
        {
            var result = options.HideAddresses && !input.Logging.HideAddresses ? masker.MaskAddressesNow(text) : masker.MaskSecretsOnly(text);
            return options.HideSites ? masker.MaskDomains(result) : result;
        }

        var all = new List<LogLine>(input.Launcher.Count + input.Service.Count);
        all.AddRange(input.Launcher.Where(l => InPeriod(l.TimeUtc, period)));
        all.AddRange(input.Service.Where(l => InPeriod(l.TimeUtc, period)));
        LogStore.Sort(all);

        var markers = all.Where(l => l.Code == LogCodes.Marker).ToList();
        if (options.CoreErrorsOnly)
        {
            all = all.Where(l => l.Source != LogSources.Core || l.Level >= LogLevel.Warn ||
                                 markers.Any(m => (l.TimeUtc - m.TimeUtc).Duration() <= ExportPeriods.AroundMark)).ToList();
        }

        var clean = all.Select(l => l with { Text = Clean(l.Text) }).ToList();
        var files = new List<string>();

        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Utf8))
        {
            void Text(string name, string content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(createdUtc).ToLocalTime();
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, Utf8);
                writer.Write(content);
                files.Add(name);
            }

            void Lines(string name, IEnumerable<LogLine> lines)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, Utf8);
                foreach (var line in lines)
                {
                    writer.Write(line.Format());
                    writer.Write('\n');
                }
                files.Add(name);
            }

            void Json(string name, JsonNode? node)
            {
                if (node is null)
                    return;
                var sanitized = ConfigSanitizer.Sanitize(node.DeepClone(), masker, options.HideAddresses || input.Logging.HideAddresses);
                if (options.HideSites)
                    sanitized = MaskSites(sanitized, masker);
                Text(name, sanitized?.ToJsonString(Pretty) ?? "null");
            }

            Lines("timeline.log", clean);
            Lines("launcher.log", clean.Where(l => l.Source == LogSources.Launcher));
            Lines("service.log", clean.Where(l => l.Source == LogSources.Service));
            Lines("core.log", clean.Where(l => l.Source == LogSources.Core));

            var events = new StringBuilder();
            foreach (var e in input.Events.Where(e => InPeriod(e.Time.ToUniversalTime(), period)))
                events.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\t').Append(e.Kind).Append('\t')
                    .Append(Clean(e.Text)).Append("\r\n");
            Text("events.txt", events.ToString());

            Json("settings.json", input.Settings);
            Json("environment.json", input.Environment);
            Json("state.json", input.State);

            // Конфиги ядра: действовавший в конце периода — core-config.json, все за период — в core-configs/.
            var configs = input.ServiceFiles.Where(f => f.Name.StartsWith("core-config", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Utc).ToList();
            var configNames = new Dictionary<ExportFile, string>();
            foreach (var config in configs)
            {
                var name = "core-configs/" + config.Name;
                configNames[config] = name;
                Text(name, CleanConfig(config.Content, masker, options, input.Logging.HideAddresses));
            }
            if (configs.LastOrDefault(c => c.Utc <= period.ToUtc) is { } last)
                Text("core-config.json", CleanConfig(last.Content, masker, options, input.Logging.HideAddresses));

            foreach (var crash in input.LauncherCrashes.Concat(input.ServiceFiles.Where(f => f.Name.StartsWith(CrashReport.FilePrefix, StringComparison.OrdinalIgnoreCase))))
            {
                var prefix = input.LauncherCrashes.Contains(crash) ? "launcher-" : "service-";
                Text("crashes/" + prefix + crash.Name, Clean(crash.Content));
            }

            if (options.IncludeDns)
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var journal in input.Dns)
                {
                    var name = SafeName(journal.AppName);
                    var unique = name;
                    for (var i = 2; !used.Add(unique); i++)
                        unique = name + "-" + i;
                    var text = new StringBuilder();
                    foreach (var e in journal.Entries.Where(e => InPeriod(e.Time.ToUniversalTime(), period)))
                    {
                        text.Append(LogLine.FormatTime(e.Time.ToUniversalTime())).Append('\t');
                        if (e.IsMarker)
                            text.Append("---- ").Append(Clean(e.Domain)).Append(" ----");
                        else
                            text.Append(e.ProcessName).Append(" (").Append(e.ProcessId).Append(")\t").Append(e.TypeText).Append('\t').Append(Clean(e.Domain));
                        text.Append('\n');
                    }
                    Text("dns/" + unique + ".log", text.ToString());
                }
            }

            if (input.ServiceProblem is { } problem)
                Text("notes.txt", "Журналов службы и ядра в архиве нет: " + problem + "\r\n");

            var summary = Summary(options, input, clean, markers.Select(m => m with { Text = Clean(m.Text) }).ToList(), configs, configNames, createdUtc, Clean);
            files.Insert(0, "summary.json");
            summary["files"] = new JsonArray(files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
            var entry = zip.CreateEntry("summary.json", CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream, Utf8))
                writer.Write(summary.ToJsonString(Pretty));
        }

        return new ExportResult(clean.Count, files, clean.Count == 0);
    }

    private static JsonObject Summary(ExportOptions options, ExportInput input, IReadOnlyList<LogLine> lines, IReadOnlyList<LogLine> markers,
        IReadOnlyList<ExportFile> configs, IReadOnlyDictionary<ExportFile, string> configNames, DateTime createdUtc, Func<string?, string> clean)
    {
        JsonObject Coverage(string source)
        {
            var own = lines.Where(l => l.Source == source).ToList();
            return new JsonObject
            {
                ["fromUtc"] = own.Count > 0 ? LogLine.FormatTime(own[0].TimeUtc) : null,
                ["toUtc"] = own.Count > 0 ? LogLine.FormatTime(own[^1].TimeUtc) : null,
                ["lines"] = own.Count
            };
        }

        var coreStarts = new JsonArray();
        foreach (var start in lines.Where(l => l.Code == LogCodes.CoreStart || l.Code == LogCodes.CoreRestart))
        {
            if (start.Source != LogSources.Service)
                continue;
            var config = configs.Where(c => (c.Utc - start.TimeUtc).Duration() <= TimeSpan.FromSeconds(10))
                .OrderBy(c => (c.Utc - start.TimeUtc).Duration()).FirstOrDefault();
            coreStarts.Add(new JsonObject
            {
                ["utc"] = LogLine.FormatTime(start.TimeUtc),
                ["cid"] = start.Cid,
                ["text"] = start.Text,
                ["configFile"] = config is null ? null : configNames[config]
            });
        }

        var tz = TimeZoneInfo.Local;
        return new JsonObject
        {
            ["format"] = Format,
            ["createdUtc"] = LogLine.FormatTime(createdUtc),
            ["timeZone"] = tz.Id,
            ["utcOffsetMinutes"] = (int)tz.GetUtcOffset(createdUtc).TotalMinutes,
            ["period"] = new JsonObject
            {
                ["preset"] = options.Period.Preset,
                ["title"] = options.Period.Title,
                ["fromUtc"] = LogLine.FormatTime(options.Period.FromUtc),
                ["toUtc"] = LogLine.FormatTime(options.Period.ToUtc)
            },
            ["coverage"] = new JsonObject
            {
                ["launcher"] = Coverage(LogSources.Launcher),
                ["service"] = Coverage(LogSources.Service),
                ["core"] = Coverage(LogSources.Core)
            },
            ["masking"] = new JsonObject
            {
                ["ips"] = options.HideAddresses || input.Logging.HideAddresses,
                ["ipsInStoredLogs"] = input.Logging.HideAddresses,
                ["domains"] = options.HideSites,
                ["maskKeyShared"] = input.MaskKeyShared
            },
            ["versions"] = new JsonObject
            {
                ["launcher"] = input.LauncherVersion,
                ["service"] = input.ServiceVersion,
                ["protocol"] = input.Protocol,
                ["windows"] = System.Environment.OSVersion.Version.ToString(),
                [".net"] = System.Environment.Version.ToString()
            },
            ["logging"] = new JsonObject
            {
                ["storage"] = input.Logging.Storage == LogStorage.Memory ? "memory" : "files",
                ["retentionDays"] = input.Logging.RetentionDays,
                ["detailed"] = new JsonObject
                {
                    ["on"] = input.Logging.IsDetailed(createdUtc),
                    ["untilUtc"] = input.Logging.DetailedUntilUtc is { } until ? LogLine.FormatTime(until) : null
                }
            },
            ["markers"] = new JsonArray(markers.Select(m => (JsonNode)new JsonObject
            {
                ["utc"] = LogLine.FormatTime(m.TimeUtc),
                ["source"] = m.Source,
                ["cid"] = m.Cid,
                ["text"] = m.Text
            }).ToArray()),
            ["coreStarts"] = coreStarts,
            ["tags"] = input.Tags?.DeepClone(),
            ["dropped"] = new JsonObject
            {
                ["launcher"] = input.LauncherDropped,
                ["service"] = input.ServiceDropped
            },
            ["coreErrorsOnly"] = options.CoreErrorsOnly,
            ["serviceProblem"] = input.ServiceProblem,
            ["userNote"] = string.IsNullOrWhiteSpace(options.Note) ? null : clean(options.Note.Trim())
        };
    }

    private static string CleanConfig(string content, LogMasker masker, ExportOptions options, bool storedHidden)
    {
        var text = ConfigSanitizer.Sanitize(content, masker, options.HideAddresses || storedHidden);
        if (!options.HideSites)
            return text;
        try
        {
            return MaskSites(JsonNode.Parse(text), masker)?.ToJsonString(Pretty) ?? text;
        }
        catch (JsonException)
        {
            return masker.MaskDomains(text);
        }
    }

    /// <summary>Имена сайтов во всех строках JSON (правила маршрутов ядра, исключения доменов).</summary>
    private static JsonNode? MaskSites(JsonNode? node, LogMasker masker)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                    obj[key] = MaskSites(value?.DeepClone(), masker);
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    array[i] = MaskSites(array[i]?.DeepClone(), masker);
                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(masker.MaskDomains(text));
            default:
                return node;
        }
    }

    private static bool InPeriod(DateTime utc, ExportPeriod period) => utc >= period.FromUtc && utc <= period.ToUtc;

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var result = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return result.Length == 0 ? "app" : result;
    }
}
