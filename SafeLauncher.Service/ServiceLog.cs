using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SafeLauncher.Logging;

namespace SafeLauncher.Service;

/// <summary>
/// Журналы службы и ядра (notes/logging/01-system-concept.md): <c>%ProgramData%\SafeLauncher\logs\service-*.log</c> и <c>core-*.log</c>,
/// очищенные конфиги ядра <c>core-config-*.json</c>, падения <c>crash-*.txt</c>. Папка доступна только системе и администраторам;
/// лаунчер читает журналы через канал (logRead) и получает только строки периодов, когда служба работала на его пользователя.
/// Журнал Windows остается для старта и остановки службы и ошибок (на случай, если папка журналов недоступна).
/// Все методы безопасны: журнал никогда не роняет службу.
/// </summary>
internal static class ServiceLog
{
    public const long ServiceMaxBytes = 20L * 1024 * 1024;
    public const long CoreMaxBytes = 300L * 1024 * 1024;
    public const long ExtraMaxBytes = 50L * 1024 * 1024;
    public const int ServiceMemoryLines = 5_000;
    public const int CoreMemoryLines = 50_000;
    public const string ConfigPrefix = "core-config";
    public const string ConfigPattern = "core-config-*.json";

    /// <summary>Самая большая порция logRead: JSON экранирует кириллицу (\uXXXX), ответ в 3 раза больше строк.</summary>
    public const int MaxReadBytes = 512 * 1024;

    private static readonly object Sync = new();
    private static readonly LogMasker Masker = new();
    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static LogStore? _service;
    private static LogStore? _core;
    private static LogSettings _settings = new();
    private static Action<string>? _eventLog;
    private static string _directory = DefaultDirectory();
    private static DateTime _startedUtc = DateTime.UtcNow;
    private static CoreLogParser _parser = new();
    private static SessionIndex? _sessions;
    private static Timer? _timer;
    private static CoreApi? _api;
    private static int? _corePid;
    private static CancellationTokenSource? _debug;
    private static long _debugDropped;
    private static Task? _remask;

    public static string Directory => _directory;

    public static LogMasker Mask => Masker;

    public static LogSettings Settings
    {
        get
        {
            lock (Sync)
                return _settings.Clone();
        }
    }

    public static bool Detailed => Settings.IsDetailed(DateTime.UtcNow);

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SafeLauncher", "logs");

    /// <summary>Открыть журналы (служба запускается). <paramref name="eventLog"/> — Журнал Windows для важного.</summary>
    public static void Initialize(Action<string>? eventLog, string? directory = null)
    {
        lock (Sync)
        {
            _eventLog = eventLog;
            _directory = directory ?? DefaultDirectory();
            _startedUtc = DateTime.UtcNow;
            try
            {
                PrepareDirectory(_directory);
            }
            catch (Exception ex)
            {
                eventLog?.Invoke("Журналы службы: папка недоступна: " + ex.Message);
            }

            _settings = LoadSettings();
            Masker.SetKey(LoadKey(rotate: false));
            Masker.HideAddresses = _settings.HideAddresses;
            _service = new LogStore(_directory, "service", LogSources.Service, ServiceMaxBytes, ServiceMemoryLines);
            _core = new LogStore(_directory, "core", LogSources.Core, CoreMaxBytes, CoreMemoryLines);
            ApplyStorage();
            _sessions = new SessionIndex(Path.Combine(_directory, "sessions.json"));
            _sessions.CloseOpen(_startedUtc);
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        var version = typeof(ServiceLog).Assembly.GetName().Version?.ToString(3) ?? "?";
        Info(LogAreas.Session, LogCodes.SessionStart, null,
            $"Служба запущена: версия {version}, протокол {ControlProtocol.Revision}, Windows {Environment.OSVersion.Version}, " +
            $"PID {Environment.ProcessId}. {DescribeSettings(Settings)}");
    }

    public static void Shutdown()
    {
        Info(LogAreas.Session, LogCodes.SessionEnd, null, "Служба остановлена");
        StopDebugStream(null);
        lock (Sync)
        {
            _timer?.Dispose();
            _timer = null;
            _service?.Dispose();
            _core?.Dispose();
            _service = null;
            _core = null;
        }
    }

    // ---- запись ----

    public static void Debug(string area, string code, string? cid, string text) => Write(LogLevel.Debug, area, code, cid, text);

    public static void Info(string area, string code, string? cid, string text) => Write(LogLevel.Info, area, code, cid, text);

    public static void Warn(string area, string code, string? cid, string text) => Write(LogLevel.Warn, area, code, cid, text);

    public static void Error(string area, string code, string? cid, string text) => Write(LogLevel.Error, area, code, cid, text);

    /// <summary>Корреляция текущей команды лаунчера (команда выполняется в одном потоке канала).</summary>
    [ThreadStatic]
    public static string? CurrentCid;

    public static void Write(LogLevel level, string area, string code, string? cid, string text)
    {
        try
        {
            if (level == LogLevel.Debug && !Detailed)
                return;
            _service?.Append(new LogLine(DateTime.UtcNow, LogSources.Service, level, area, code, Cid(cid ?? CurrentCid), Masker.Mask(text)));
        }
        catch (Exception)
        {
            // журнал не должен мешать службе
        }
    }

    /// <summary>
    /// Строка stdout/stderr ядра: в журнал ядра. Возвращает строку для короткого хвоста (ответы лаунчеру):
    /// <c>ЧЧ:мм:сс.ммм УРОВЕНЬ текст</c>, местное время, без секретов.
    /// </summary>
    public static string CoreLine(string raw)
    {
        try
        {
            var parsed = _parser.Parse(raw);
            if (parsed is not { } p)
                return Masker.MaskSecretsOnly(raw);
            var now = DateTime.UtcNow;
            _core?.Append(new LogLine(now, LogSources.Core, p.Level, LogAreas.Main, LogLine.None, LogLine.None, Masker.Mask(p.Text)));
            return now.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + LogLine.LevelName(p.Level) + " " +
                   Masker.MaskSecretsOnly(p.Text);
        }
        catch (Exception)
        {
            return raw;
        }
    }

    /// <summary>Падение службы: строка и файл синхронно, плюс Журнал Windows.</summary>
    public static void Crash(Exception? ex, string kind, bool terminating)
    {
        try
        {
            if (terminating)
                _eventLog?.Invoke("Служба упала (" + kind + "): " + ex);
            var version = typeof(ServiceLog).Assembly.GetName().Version?.ToString(3) ?? "?";
            CrashReport.Write(_service, Masker, _directory, LogSources.Service, kind, ex, version, _startedUtc, terminating);
            if (terminating)
                _core?.Flush();
        }
        catch (Exception)
        {
            // ничего не поделать
        }
    }

    // ---- ядро ----

    /// <summary>Ядро запускается: граница в журнале ядра, описание запроса, очищенный конфиг на диск, маски серверов.</summary>
    public static void CoreStarting(CoreRequest request, string config, CoreApi api, string? cid, string reason)
    {
        try
        {
            SetServers(request);
            Masker.SetSecrets(new[] { api.Secret });
            _parser = new CoreLogParser();
            var hash = RequestHash(request);
            Info(LogAreas.Core, LogCodes.CoreStart, cid, $"Запуск ядра ({reason}): {DescribeRequest(request)}, план {hash}");
            _core?.Append(new LogLine(DateTime.UtcNow, LogSources.Core, LogLevel.Info, LogAreas.Main, LogCodes.CoreStart, Cid(cid),
                $"--- ядро запускается, план {hash} ---"));
            SaveCoreConfig(config);
        }
        catch (Exception)
        {
            // не важно
        }
    }

    public static void CoreStarted(int pid, CoreApi api, TimeSpan took, string? cid)
    {
        lock (Sync)
        {
            _api = api;
            _corePid = pid;
        }
        Info(LogAreas.Core, LogCodes.CoreReady, cid, $"Ядро готово: PID {pid}, за {took.TotalMilliseconds:0} мс");
        _core?.Append(new LogLine(DateTime.UtcNow, LogSources.Core, LogLevel.Info, LogAreas.Main, LogCodes.CoreReady, Cid(cid),
            $"--- ядро PID {pid} работает ---"));
        if (Detailed)
            StartDebugStream(cid);
    }

    /// <summary>Ядро остановлено или завершилось: подробный поток закрывается.</summary>
    public static void CoreGone()
    {
        lock (Sync)
        {
            _api = null;
            _corePid = null;
        }
        StopDebugStream(null);
    }

    private static void SetServers(CoreRequest request)
    {
        var servers = new List<KeyValuePair<string, string>>();
        foreach (var outbound in request.Outbounds)
        {
            var tag = outbound["tag"]?.GetValue<string>();
            if (string.IsNullOrEmpty(tag))
                continue;
            if (outbound["server"] is JsonValue server && server.TryGetValue<string>(out var host))
                servers.Add(new(host, tag));
            if (outbound["peers"] is JsonArray peers)
            {
                foreach (var peer in peers.OfType<JsonObject>())
                {
                    if (peer["address"] is JsonValue address && address.TryGetValue<string>(out var a))
                        servers.Add(new(a, tag));
                }
            }
        }
        Masker.SetServers(servers);
    }

    private static void SaveCoreConfig(string config)
    {
        if (_settings.Storage != LogStorage.Files)
            return;
        var path = Path.Combine(_directory, LogFiles.TimedName(ConfigPrefix, DateTime.UtcNow, ".json"));
        File.WriteAllText(path, ConfigSanitizer.Sanitize(config, Masker, Masker.HideAddresses), new UTF8Encoding(false));
    }

    public static string RequestHash(CoreRequest request)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(request, ControlProtocol.JsonOptions);
        return Convert.ToHexString(SHA256.HashData(json), 0, 4).ToLowerInvariant();
    }

    public static string DescribeRequest(CoreRequest request)
    {
        var types = request.Outbounds
            .Select(o => (o["type"]?.GetValue<string>() ?? "?") + " " + (o["tag"]?.GetValue<string>() ?? "?"))
            .ToList();
        var apps = request.Apps.Select(a => a.Tag + "→" + a.Outbound + Details(a));
        return $"подключений {request.Outbounds.Count} [{string.Join(", ", types)}], приложений {request.Apps.Count} [{string.Join(", ", apps)}], " +
               $"IPv6 {(request.Ipv6 ? "да" : "нет")}, скрывать DNS {(request.HideDns ? "да" : "нет")}" +
               (request.OutboundDns is { Count: > 0 } dns ? $", DNS в подключениях {dns.Count}" : string.Empty) +
               (request.HideDns && request.DnsOutbound is { } via ? $", редкие DNS-запросы через {via}" : string.Empty) +
               (request.Apps.Any(a => a.Programs is { Count: > 0 })
                   ? ", процессы программ без метки " + (request.Apps.Any(a => a.BlockOutside) ? "блокируются" : "через подключение приложения")
                   : string.Empty);

        static string Details(CoreAppRoute app)
        {
            var parts = new List<string>();
            if (app.DirectDomains.Count > 0)
                parts.Add($"исключений {app.DirectDomains.Count}");
            if (app.Programs is { Count: > 0 } programs)
                parts.Add($"программ {programs.Count}");
            return parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : string.Empty;
        }
    }

    // ---- сеансы (кто какие периоды может читать) ----

    /// <summary>Лаунчер пользователя начал работать со службой (первая команда, кроме журналов).</summary>
    public static void ClientActive(string? sid, int pid)
    {
        if (sid is not null)
            _sessions?.Open(sid, pid, "conn", DateTime.UtcNow);
    }

    public static void ClientGone(string? sid, int pid)
    {
        if (sid is not null)
            _sessions?.Close(sid, pid, "conn", DateTime.UtcNow);
    }

    public static void CoreOwner(string? sid, int pid, bool owns)
    {
        if (sid is null)
            return;
        if (owns)
            _sessions?.Open(sid, pid, "core", DateTime.UtcNow);
        else
            _sessions?.Close(sid, pid, "core", DateTime.UtcNow);
    }

    /// <summary>Последний лаунчер отключился: в режиме «только в памяти» журналы службы забываются.</summary>
    public static void AllClientsGone()
    {
        if (_settings.Storage != LogStorage.Memory)
            return;
        _service?.ClearMemory();
        _core?.ClearMemory();
    }

    // ---- команды канала ----

    public static ControlReply Handle(ControlMessage message, string? sid, int clientPid)
    {
        var request = message.Logs ?? new LogRequest();
        switch (message.Op)
        {
            case ControlProtocol.OpLogConfig:
                return Configure(message, request);
            case ControlProtocol.OpLogRead:
                return Read(message, request, sid);
            case ControlProtocol.OpLogMark:
            {
                var text = string.IsNullOrWhiteSpace(request.Text) ? "Отметка пользователя" : "Отметка пользователя: " + request.Text!.Trim();
                Info(LogAreas.Marker, LogCodes.Marker, message.Cid, text);
                _core?.Append(new LogLine(DateTime.UtcNow, LogSources.Core, LogLevel.Info, LogAreas.Marker, LogCodes.Marker, Cid(message.Cid),
                    Masker.Mask("--- " + text + " ---")));
                return new ControlReply { Id = message.Id, Ok = true };
            }
            case ControlProtocol.OpLogClear:
                return new ControlReply { Id = message.Id, Ok = true, Logs = new LogReply { Freed = Clear(message.Cid, clientPid) } };
            case ControlProtocol.OpLogFiles:
                return new ControlReply { Id = message.Id, Ok = true, Logs = new LogReply { Files = Files(request, sid) } };
            default:
                return new ControlReply { Id = message.Id, Ok = false, Error = $"Неизвестная команда «{message.Op}»." };
        }
    }

    private static ControlReply Configure(ControlMessage message, LogRequest request)
    {
        LogSettings before, after;
        lock (Sync)
        {
            before = _settings.Clone();
            var next = before.Clone();
            if (request.Storage is { } storage && Enum.TryParse<LogStorage>(storage, ignoreCase: true, out var s))
                next.Storage = s;
            if (request.RetentionDays is { } days)
                next.RetentionDays = days;
            if (request.HideAddresses is { } hide)
                next.HideAddresses = hide;
            next.DetailedUntilUtc = request.DetailedUntilUtc is { } until && until > DateTime.UtcNow
                ? DateTime.SpecifyKind(until, DateTimeKind.Utc)
                : null;
            _settings = next.Normalized();
            after = _settings.Clone();
            SaveSettings(after);
            Masker.HideAddresses = after.HideAddresses;
        }

        if (before.Storage != after.Storage || before.RetentionDays != after.RetentionDays)
            ApplyStorage();
        if (!before.HideAddresses && after.HideAddresses)
            Remask();

        var changed = DescribeChanges(before, after);
        if (changed.Length > 0)
            Info(LogAreas.Session, LogCodes.LogConfig, message.Cid, "Настройки журналов: " + changed);

        var wasDetailed = before.IsDetailed(DateTime.UtcNow);
        var isDetailed = after.IsDetailed(DateTime.UtcNow);
        if (!wasDetailed && isDetailed)
        {
            Info(LogAreas.Session, LogCodes.LogDetailed, message.Cid,
                $"Подробный журнал включен до {LogLine.FormatTime(after.DetailedUntilUtc!.Value)}");
            StartDebugStream(message.Cid);
        }
        else if (wasDetailed && !isDetailed)
        {
            Info(LogAreas.Session, LogCodes.LogDetailed, message.Cid, "Подробный журнал выключен");
            StopDebugStream(message.Cid);
        }

        return new ControlReply { Id = message.Id, Ok = true, Logs = State() };
    }

    /// <summary>Состояние журналов и ключ меток (только для logConfig: ключ нужен лаунчеру для одинаковых меток).</summary>
    private static LogReply State()
    {
        var settings = Settings;
        return new LogReply
        {
            MaskKey = Convert.ToBase64String(LoadKey(rotate: false)),
            ServiceBytes = (_service?.UsageBytes() ?? 0) + LogFiles.Size(_directory, CrashReport.FilePattern) + LogFiles.Size(_directory, "sessions.json"),
            CoreBytes = (_core?.UsageBytes() ?? 0) + LogFiles.Size(_directory, ConfigPattern),
            Dropped = (_service?.Dropped ?? 0) + (_core?.Dropped ?? 0) + Interlocked.Read(ref _debugDropped),
            Detailed = settings.IsDetailed(DateTime.UtcNow),
            Storage = settings.Storage == LogStorage.Memory ? "memory" : "files"
        };
    }

    private static ControlReply Read(ControlMessage message, LogRequest request, string? sid)
    {
        var from = request.FromUtc is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : DateTime.UtcNow.AddHours(-1);
        var to = request.ToUtc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : DateTime.UtcNow;
        var sources = request.Sources is { Count: > 0 } list ? list : new List<string> { LogSources.Service, LogSources.Core };
        var maxBytes = Math.Clamp(request.MaxBytes ?? MaxReadBytes, 16 * 1024, MaxReadBytes);
        var access = _sessions?.Intervals(sid) ?? new Intervals(Array.Empty<(DateTime, DateTime, string)>());

        // Курсор: «s:…» — читаем службу, «c:…» — ядро (служба первой); после двоеточия — курсор хранилища или пусто (с начала).
        var cursor = request.Cursor;
        var order = LogSources.All.Where(s => s != LogSources.Launcher && sources.Contains(s)).ToList();
        var index = 0;
        string? inner = null;
        if (cursor is { Length: >= 2 } && cursor[1] == ':')
        {
            var src = cursor[0] == 'c' ? LogSources.Core : LogSources.Service;
            index = Math.Max(0, order.IndexOf(src));
            inner = cursor.Length > 2 ? cursor[2..] : null;
        }

        var lines = new List<string>();
        var budget = maxBytes;
        for (; index < order.Count; index++)
        {
            var source = order[index];
            var store = source == LogSources.Core ? _core : _service;
            if (store is null)
                continue;

            Func<LogLine, bool> allowed = source == LogSources.Core
                ? l => l.Area == LogAreas.Marker || access.Allows(l.TimeUtc, "core")
                : l => l.Area == LogAreas.Session || access.Allows(l.TimeUtc, "conn");
            var chunk = store.ReadChunk(from, to, inner, budget, allowed);
            foreach (var line in chunk.Lines)
            {
                var text = line.Format();
                lines.Add(text);
                budget -= Encoding.UTF8.GetByteCount(text);
            }

            if (chunk.NextCursor is not null)
                return Reply(lines, (source == LogSources.Core ? "c:" : "s:") + chunk.NextCursor);
            inner = null;
            if (budget <= 0 && index + 1 < order.Count)
                return Reply(lines, (order[index + 1] == LogSources.Core ? "c:" : "s:"));
        }

        return Reply(lines, null);

        ControlReply Reply(List<string> result, string? next) => new()
        {
            Id = message.Id,
            Ok = true,
            Logs = new LogReply { Lines = result, Cursor = next }
        };
    }

    private static List<LogFileItem> Files(LogRequest request, string? sid)
    {
        var from = request.FromUtc is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : DateTime.UtcNow.AddHours(-1);
        var to = request.ToUtc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : DateTime.UtcNow;
        var access = _sessions?.Intervals(sid) ?? new Intervals(Array.Empty<(DateTime, DateTime, string)>());
        var result = new List<LogFileItem>();
        long total = 0;

        // Конфиг, действовавший в начале периода, запущен раньше: берем и последний до периода.
        var configs = LogFiles.InPeriod(_directory, ConfigPrefix, ConfigPattern, DateTime.MinValue, to).ToList();
        var firstInside = configs.FindIndex(c => c.Utc >= from);
        var start = firstInside < 0 ? Math.Max(0, configs.Count - 1) : Math.Max(0, firstInside - 1);
        foreach (var (path, utc) in configs.Skip(start).Concat(LogFiles.InPeriod(_directory, CrashReport.FilePrefix, CrashReport.FilePattern, from, to)))
        {
            if (Path.GetFileName(path).StartsWith(ConfigPrefix, StringComparison.OrdinalIgnoreCase) && !access.Allows(utc, "core"))
                continue;
            try
            {
                var content = File.ReadAllText(path, Encoding.UTF8);
                total += content.Length;
                if (total > 1024 * 1024)
                    break; // ответ канала ограничен; остальное — в следующей выгрузке за период короче
                result.Add(new LogFileItem { Name = Path.GetFileName(path), Utc = utc, Content = content });
            }
            catch (Exception)
            {
                // удален по сроку
            }
        }
        return result;
    }

    private static long Clear(string? cid, int clientPid)
    {
        long freed;
        lock (Sync)
        {
            freed = (_service?.Clear() ?? 0) + (_core?.Clear() ?? 0);
            freed += LogFiles.Delete(_directory, ConfigPattern) + LogFiles.Delete(_directory, CrashReport.FilePattern);
            Masker.SetKey(LoadKey(rotate: true));
        }
        Info(LogAreas.Session, LogCodes.LogClear, cid, $"Журналы службы и ядра удалены по команде лаунчера PID {clientPid}, ключ меток адресов заменен");
        return freed;
    }

    // ---- подробный журнал ядра: поток clash API /logs?level=debug ----

    private static void StartDebugStream(string? cid)
    {
        CoreApi? api;
        lock (Sync)
        {
            api = _api;
            if (api is null || _debug is not null)
                return;
            _debug = new CancellationTokenSource();
        }

        var token = _debug.Token;
        Info(LogAreas.Core, LogCodes.LogDetailed, cid, "Подробный журнал ядра: подписка на поток отладки");
        _ = Task.Run(() => ReadDebugStreamAsync(api, token));
    }

    private static void StopDebugStream(string? cid)
    {
        CancellationTokenSource? debug;
        lock (Sync)
        {
            debug = _debug;
            _debug = null;
        }
        if (debug is null)
            return;
        debug.Cancel();
        debug.Dispose();
        Info(LogAreas.Core, LogCodes.LogDetailed, cid, "Подробный журнал ядра: поток отладки закрыт");
    }

    private static async Task ReadDebugStreamAsync(CoreApi api, CancellationToken cancellation)
    {
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{api.Port}/logs?level=debug");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.Secret);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!cancellation.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
                if (line is null)
                    break;
                DebugLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // выключили
        }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
                Warn(LogAreas.Core, LogCodes.LogDetailed, null, "Подробный журнал ядра: поток прервался: " + ex.Message);
        }
    }

    private static void DebugLine(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "info" : "info";
            // info и выше уже приходят из вывода ядра, здесь только debug
            if (CoreLogParser.Level(type) != LogLevel.Debug)
                return;
            var payload = doc.RootElement.TryGetProperty("payload", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            var core = _core;
            if (core is null)
                return;
            var before = core.Dropped;
            core.Append(new LogLine(DateTime.UtcNow, LogSources.Core, LogLevel.Debug, LogAreas.Main, LogLine.None, LogLine.None, Masker.Mask(payload)));
            if (core.Dropped > before)
                Interlocked.Increment(ref _debugDropped);
        }
        catch (Exception)
        {
            // битая строка потока
        }
    }

    // ---- обслуживание ----

    private static void Tick()
    {
        try
        {
            var settings = Settings;
            if (settings.DetailedUntilUtc is { } until && until <= DateTime.UtcNow)
            {
                lock (Sync)
                {
                    _settings.DetailedUntilUtc = null;
                    SaveSettings(_settings);
                }
                Info(LogAreas.Session, LogCodes.LogDetailed, null, "Подробный журнал выключился сам: прошел час");
                StopDebugStream(null);
            }

            if (settings.Storage == LogStorage.Files)
            {
                LogFiles.Purge(_directory, ConfigPattern, DateTime.UtcNow, settings.RetentionDays, ExtraMaxBytes);
                LogFiles.Purge(_directory, CrashReport.FilePattern, DateTime.UtcNow, settings.RetentionDays, ExtraMaxBytes);
            }
            _sessions?.Prune(DateTime.UtcNow.AddDays(-Math.Max(31, settings.RetentionDays + 1)));
        }
        catch (Exception)
        {
            // попробуем позже
        }
    }

    private static void ApplyStorage()
    {
        var settings = Settings;
        _service?.Configure(settings.Storage, settings.RetentionDays);
        _core?.Configure(settings.Storage, settings.RetentionDays);
        if (settings.Storage == LogStorage.Memory)
        {
            LogFiles.Delete(_directory, ConfigPattern);
            // падения остаются: иначе о падении службы не узнать
        }
        else
        {
            LogFiles.Purge(_directory, ConfigPattern, DateTime.UtcNow, settings.RetentionDays, ExtraMaxBytes);
            LogFiles.Purge(_directory, CrashReport.FilePattern, DateTime.UtcNow, settings.RetentionDays, ExtraMaxBytes);
        }
    }

    /// <summary>Скрытие адресов включили: уже записанное перемаскируется в фоне.</summary>
    private static void Remask()
    {
        lock (Sync)
        {
            if (_remask is { IsCompleted: false })
                return;
            _remask = Task.Run(() =>
            {
                try
                {
                    _service?.Rewrite(l => l with { Text = Masker.Mask(l.Text) });
                    _core?.Rewrite(l => l with { Text = Masker.Mask(l.Text) });
                    foreach (var (path, _) in LogFiles.InPeriod(_directory, ConfigPrefix, ConfigPattern, DateTime.MinValue, DateTime.MaxValue))
                    {
                        try
                        {
                            File.WriteAllText(path, ConfigSanitizer.Sanitize(File.ReadAllText(path), Masker, true), new UTF8Encoding(false));
                        }
                        catch (Exception)
                        {
                            // файл занят
                        }
                    }
                    Info(LogAreas.Session, LogCodes.LogConfig, null, "Адреса в сохраненных журналах службы и ядра скрыты");
                }
                catch (Exception ex)
                {
                    Warn(LogAreas.Session, LogCodes.LogConfig, null, "Не удалось скрыть адреса в сохраненных журналах: " + ex.Message);
                }
            });
        }
    }

    /// <summary>Дождаться перемаскировки (выгрузка ждет ее окончания).</summary>
    public static void WaitRemask(TimeSpan timeout)
    {
        Task? task;
        lock (Sync)
            task = _remask;
        task?.Wait(timeout);
    }

    private static string DescribeSettings(LogSettings s) =>
        $"Журналы: {(s.Storage == LogStorage.Memory ? "только в памяти" : "в файлах, " + s.RetentionDays + " дн.")}, " +
        $"скрывать адреса {(s.HideAddresses ? "да" : "нет")}, подробный {(s.DetailedUntilUtc is { } u ? "до " + LogLine.FormatTime(u) : "нет")}";

    private static string DescribeChanges(LogSettings a, LogSettings b)
    {
        var parts = new List<string>();
        if (a.Storage != b.Storage)
            parts.Add($"хранение {a.Storage} → {b.Storage}");
        if (a.RetentionDays != b.RetentionDays)
            parts.Add($"срок {a.RetentionDays} → {b.RetentionDays} дн.");
        if (a.HideAddresses != b.HideAddresses)
            parts.Add($"скрывать адреса {(a.HideAddresses ? "да" : "нет")} → {(b.HideAddresses ? "да" : "нет")}");
        return string.Join(", ", parts);
    }

    private static string Cid(string? cid) =>
        cid is { Length: > 0 and <= 16 } && cid.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? cid : LogLine.None;

    // ---- файлы настроек и ключа ----

    private static void PrepareDirectory(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            return;

        // Только система и администраторы: в журналах адреса и имена программ всех пользователей компьютера.
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static byte[] LoadKey(bool rotate)
    {
        var path = Path.Combine(_directory, ".key");
        try
        {
            if (!rotate && File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length >= 16)
                    return existing;
            }
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(path, key);
            return key;
        }
        catch (Exception)
        {
            return RandomNumberGenerator.GetBytes(32);
        }
    }

    private static LogSettings LoadSettings()
    {
        try
        {
            var path = Path.Combine(_directory, "config.json");
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<LogSettings>(File.ReadAllText(path), SettingsJson) ?? new LogSettings()).Normalized();
        }
        catch (Exception)
        {
            // испорчен: по умолчанию
        }
        return new LogSettings();
    }

    private static void SaveSettings(LogSettings settings)
    {
        try
        {
            var path = Path.Combine(_directory, "config.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, SettingsJson));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception)
        {
            // не страшно: при следующем подключении лаунчер пришлет снова
        }
    }

    /// <summary>Интервалы (с, по, вид), когда служба работала на пользователя.</summary>
    internal sealed class Intervals
    {
        private readonly (DateTime From, DateTime To, string Kind)[] _items;

        public Intervals(IEnumerable<(DateTime From, DateTime To, string Kind)> items) => _items = items.ToArray();

        public bool Allows(DateTime utc, string kind)
        {
            foreach (var (from, to, k) in _items)
            {
                if ((k == kind || (kind == "conn" && k == "core")) && utc >= from && utc <= to)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Индекс сеансов <c>sessions.json</c>: какой пользователь (SID) когда работал со службой (conn) и владел ядром (core).
    /// По нему logRead отдает пользователю только его периоды (на общем компьютере журналы службы общие).
    /// </summary>
    internal sealed class SessionIndex
    {
        private readonly string _path;
        private readonly object _sync = new();
        private List<Entry> _entries;

        public SessionIndex(string path)
        {
            _path = path;
            _entries = Load(path);
        }

        public sealed class Entry
        {
            public string Sid { get; set; } = string.Empty;
            public int Pid { get; set; }
            public string Kind { get; set; } = "conn";
            public DateTime FromUtc { get; set; }
            public DateTime? ToUtc { get; set; }
        }

        public void Open(string sid, int pid, string kind, DateTime utc)
        {
            lock (_sync)
            {
                if (_entries.Any(e => e.Sid == sid && e.Pid == pid && e.Kind == kind && e.ToUtc is null))
                    return;
                _entries.Add(new Entry { Sid = sid, Pid = pid, Kind = kind, FromUtc = utc });
                Save();
            }
        }

        public void Close(string sid, int pid, string kind, DateTime utc)
        {
            lock (_sync)
            {
                var changed = false;
                foreach (var e in _entries.Where(e => e.Sid == sid && e.Pid == pid && e.Kind == kind && e.ToUtc is null))
                {
                    e.ToUtc = utc;
                    changed = true;
                }
                if (changed)
                    Save();
            }
        }

        /// <summary>Служба запустилась: открытые сеансы прошлого запуска закончились (служба упала или компьютер выключили).</summary>
        public void CloseOpen(DateTime utc)
        {
            lock (_sync)
            {
                foreach (var e in _entries.Where(e => e.ToUtc is null))
                    e.ToUtc = utc;
                Save();
            }
        }

        public void Prune(DateTime beforeUtc)
        {
            lock (_sync)
            {
                if (_entries.RemoveAll(e => e.ToUtc is { } to && to < beforeUtc) > 0)
                    Save();
            }
        }

        public Intervals Intervals(string? sid)
        {
            if (sid is null)
                return new Intervals(Array.Empty<(DateTime, DateTime, string)>());
            lock (_sync)
            {
                // запас 2 с: строки команды пишутся чуть раньше, чем отмечается сеанс
                return new Intervals(_entries.Where(e => e.Sid == sid)
                    .Select(e => (e.FromUtc.AddSeconds(-2), (e.ToUtc ?? DateTime.MaxValue.AddDays(-1)).AddSeconds(2), e.Kind)).ToList());
            }
        }

        private void Save()
        {
            try
            {
                File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_entries));
                File.Move(_path + ".tmp", _path, overwrite: true);
            }
            catch (Exception)
            {
                // в памяти индекс есть
            }
        }

        private static List<Entry> Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new List<Entry>();
                    foreach (var e in list)
                    {
                        e.FromUtc = DateTime.SpecifyKind(e.FromUtc, DateTimeKind.Utc);
                        if (e.ToUtc is { } to)
                            e.ToUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);
                    }
                    return list;
                }
            }
            catch (Exception)
            {
                // испорчен
            }
            return new List<Entry>();
        }
    }
}
