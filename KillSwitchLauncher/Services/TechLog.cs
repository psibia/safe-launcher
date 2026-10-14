using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.Services;

/// <summary>Прошлый сеанс лаунчера закончился без записи о завершении: когда начался и что упало (если записано).</summary>
public sealed record PreviousSession(DateTime StartUtc, DateTime? CrashUtc, string? CrashType, string? CrashFile);

/// <summary>
/// Технический журнал лаунчера (notes/logging/01-system-concept.md): <c>%LOCALAPPDATA%\KillSwitchLauncher\logs\launcher-*.log</c>
/// или только в памяти. Пишут все части лаунчера: решения сети и ядра, фильтры, запуски, проверки, зеркало Журнала событий.
/// Строки проходят через <see cref="LogMasker"/> (секреты всегда, адреса по настройке). До <see cref="Initialize"/> записи
/// молча пропускаются (тесты, помощник журнала DNS). Методы можно вызывать из любого потока; журнал никогда не бросает исключений.
/// </summary>
public static class TechLog
{
    public const long MaxBytes = 50L * 1024 * 1024;
    public const int MemoryLines = 20_000;
    public const string FilePrefix = "launcher";
    private const string LockName = "session.lock";

    private static readonly LogMasker MaskerInstance = new();
    private static readonly AsyncLocal<string?> Operation = new();
    private static readonly object Sync = new();

    private static LogStore? _store;
    private static LogSettings _settings = new();
    private static int _cidSeq = Random.Shared.Next(0, 0x1000);
    private static Task? _remask;

    public static string Directory { get; private set; } = DefaultDirectory();

    public static DateTime SessionStartUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Прошлый сеанс завершился аварийно (нет записи о закрытии), иначе null.</summary>
    public static PreviousSession? Previous { get; private set; }

    /// <summary>Ключ меток адресов получен от службы: метки лаунчера и службы совпадают.</summary>
    public static bool MaskKeyShared { get; private set; }

    public static LogMasker Masker => MaskerInstance;

    public static LogStore? Store => _store;

    public static bool IsInitialized => _store is not null;

    public static LogSettings Settings
    {
        get
        {
            lock (Sync)
                return _settings.Clone();
        }
    }

    /// <summary>Пишется ли сейчас подробный журнал (DEBUG).</summary>
    public static bool Detailed => Settings.IsDetailed(DateTime.UtcNow);

    /// <summary>Новая строка журнала (в потоке записи). Для живого просмотра.</summary>
    public static event Action<LogLine>? Added;

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillSwitchLauncher", "logs");

    /// <summary>Открыть журнал в начале работы лаунчера (до окон). Узнает, как закончился прошлый сеанс.</summary>
    public static void Initialize(LogSettings settings, string? directory = null, bool background = true)
    {
        lock (Sync)
        {
            if (_store is not null)
                return;
            Directory = directory ?? DefaultDirectory();
            SessionStartUtc = DateTime.UtcNow;
            _settings = settings.Normalized();
            MaskerInstance.HideAddresses = _settings.HideAddresses;
            MaskerInstance.SetKey(LocalKey());
            _store = new LogStore(Directory, FilePrefix, LogSources.Launcher, MaxBytes, MemoryLines, null, background);
            _store.Added += line => Added?.Invoke(line);
            _store.Configure(_settings.Storage, _settings.RetentionDays);
            Previous = ReadPreviousSession();
            WriteLock();
        }

        Info(LogAreas.Session, LogCodes.SessionStart,
            $"Лаунчер {UpdateService.Format(UpdateService.CurrentVersion)} запущен: PID {Environment.ProcessId}, Windows {Environment.OSVersion.Version}, " +
            $".NET {Environment.Version}, {DescribeSettings(_settings)}");
        if (Previous is { } previous)
        {
            Warn(LogAreas.Session, LogCodes.SessionCrashed,
                $"Прошлый сеанс (начат {LogLine.FormatTime(previous.StartUtc)}) завершился аварийно: " +
                (previous.CrashType is null ? "записи о падении нет (процесс закрыли принудительно или выключилось питание)" : previous.CrashType));
        }
    }

    /// <summary>Нормальное завершение лаунчера: строка конца сеанса, файл сеанса удаляется.</summary>
    public static void EndSession()
    {
        if (_store is null)
            return;
        Info(LogAreas.Session, LogCodes.SessionEnd, $"Лаунчер закрыт, сеанс длился {(DateTime.UtcNow - SessionStartUtc):d\\.hh\\:mm\\:ss}");
        try
        {
            _store.Flush();
            File.Delete(Path.Combine(Directory, LockName));
            // В режиме «только в памяти» описание падения хранится лишь до следующего нормального закрытия.
            if (Settings.Storage == LogStorage.Memory && Previous is not null)
                LogFiles.Delete(Directory, CrashReport.FilePattern);
        }
        catch (Exception)
        {
            // не важно
        }
    }

    /// <summary>
    /// Новые настройки журналов: хранение, срок, скрытие адресов, подробный режим. Включение скрытия перемаскирует
    /// уже записанное в фоне (<see cref="WaitRemaskAsync"/>).
    /// </summary>
    public static void Configure(LogSettings settings)
    {
        var next = settings.Normalized();
        LogSettings before;
        lock (Sync)
        {
            before = _settings;
            _settings = next;
            MaskerInstance.HideAddresses = next.HideAddresses;
        }

        if (_store is null)
            return;
        if (before.Storage != next.Storage || before.RetentionDays != next.RetentionDays)
        {
            _store.Configure(next.Storage, next.RetentionDays);
            if (next.Storage == LogStorage.Files)
                LogFiles.Purge(Directory, CrashReport.FilePattern, DateTime.UtcNow, next.RetentionDays, 20L * 1024 * 1024);
        }

        var changes = new List<string>();
        if (before.Storage != next.Storage)
            changes.Add($"хранение {Name(before.Storage)} → {Name(next.Storage)}");
        if (before.RetentionDays != next.RetentionDays)
            changes.Add($"срок {before.RetentionDays} → {next.RetentionDays} дн.");
        if (before.HideAddresses != next.HideAddresses)
            changes.Add($"скрывать IP-адреса {(next.HideAddresses ? "да" : "нет")}");
        var wasDetailed = before.IsDetailed(DateTime.UtcNow);
        var isDetailed = next.IsDetailed(DateTime.UtcNow);
        if (wasDetailed != isDetailed)
            changes.Add(isDetailed ? $"подробный журнал до {LogLine.FormatTime(next.DetailedUntilUtc!.Value)}" : "подробный журнал выключен");
        if (changes.Count > 0)
            Info(isDetailed != wasDetailed ? LogAreas.Session : LogAreas.Settings, isDetailed != wasDetailed ? LogCodes.LogDetailed : LogCodes.LogConfig,
                "Настройки журналов: " + string.Join(", ", changes));

        if (!before.HideAddresses && next.HideAddresses)
        {
            lock (Sync)
            {
                if (_remask is not { IsCompleted: false })
                    _remask = Task.Run(() => _store?.Rewrite(l => l with { Text = MaskerInstance.Mask(l.Text) }));
            }
        }
    }

    /// <summary>Дождаться перемаскировки после включения скрытия адресов (выгрузка ждет ее).</summary>
    public static async Task WaitRemaskAsync()
    {
        Task? task;
        lock (Sync)
            task = _remask;
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // перемаскировка не удалась: выгрузка все равно маскирует, если выбрано
            }
        }
    }

    /// <summary>Подробный журнал истек: выключить и записать об этом. true — выключили сейчас.</summary>
    public static bool ExpireDetailed()
    {
        lock (Sync)
        {
            if (_settings.DetailedUntilUtc is not { } until || until > DateTime.UtcNow)
                return false;
            _settings.DetailedUntilUtc = null;
        }
        Info(LogAreas.Session, LogCodes.LogDetailed, "Подробный журнал выключился сам: прошел час");
        return true;
    }

    // ---- корреляция ----

    /// <summary>Новый id операции: L + 4 hex.</summary>
    public static string NewCid() => "L" + (Interlocked.Increment(ref _cidSeq) & 0xFFFF).ToString("x4", CultureInfo.InvariantCulture);

    /// <summary>Id текущей операции (передается и в команды службы).</summary>
    public static string? CurrentCid => Operation.Value;

    /// <summary>Начать операцию: строки до конца блока (и в продолжениях await) получат ее id.</summary>
    public static IDisposable Begin(out string cid)
    {
        cid = NewCid();
        var previous = Operation.Value;
        Operation.Value = cid;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;

        public Scope(string? previous) => _previous = previous;

        public void Dispose() => Operation.Value = _previous;
    }

    // ---- запись ----

    public static void Debug(string area, string code, string text, string? cid = null) => Write(LogLevel.Debug, area, code, text, cid);

    public static void Info(string area, string code, string text, string? cid = null) => Write(LogLevel.Info, area, code, text, cid);

    public static void Warn(string area, string code, string text, string? cid = null) => Write(LogLevel.Warn, area, code, text, cid);

    public static void Error(string area, string code, string text, string? cid = null) => Write(LogLevel.Error, area, code, text, cid);

    public static void Write(LogLevel level, string area, string code, string text, string? cid = null)
    {
        var store = _store;
        if (store is null)
            return;
        try
        {
            if (level == LogLevel.Debug && !Detailed)
                return;
            store.Append(new LogLine(DateTime.UtcNow, LogSources.Launcher, level, area, code, cid ?? Operation.Value ?? LogLine.None,
                MaskerInstance.Mask(text)));
        }
        catch (Exception)
        {
            // журнал не должен мешать работе
        }
    }

    /// <summary>Строка ядра проверки подключения (источник «Ядро», область «проверка»).</summary>
    public static void CheckCoreLine(CoreLogParser parser, string raw, string? cid = null)
    {
        var store = _store;
        if (store is null)
            return;
        try
        {
            if (parser.Parse(raw) is not { } parsed || (parsed.Level == LogLevel.Debug && !Detailed))
                return;
            store.Append(new LogLine(DateTime.UtcNow, LogSources.Core, parsed.Level, LogAreas.Check, LogLine.None, cid ?? Operation.Value ?? LogLine.None,
                MaskerInstance.Mask(parsed.Text)));
        }
        catch (Exception)
        {
            // не важно
        }
    }

    /// <summary>Зеркало записи Журнала событий.</summary>
    public static void Event(LogKind kind, string text)
    {
        var level = kind switch
        {
            LogKind.Failed or LogKind.Blocked or LogKind.VpnDown => LogLevel.Warn,
            _ => LogLevel.Info
        };
        Write(level, LogAreas.Event, LogCodes.Event, "[" + kind + "] " + text);
    }

    /// <summary>Отметка пользователя «здесь проблема».</summary>
    public static string Mark(string? text)
    {
        var cid = NewCid();
        Info(LogAreas.Marker, LogCodes.Marker, string.IsNullOrWhiteSpace(text) ? "Отметка пользователя" : "Отметка пользователя: " + text!.Trim(), cid);
        _store?.Flush();
        return cid;
    }

    /// <summary>Падение лаунчера: строка и файл синхронно.</summary>
    public static void Crash(Exception? ex, string kind, bool terminating)
    {
        try
        {
            CrashReport.Write(_store, MaskerInstance, Directory, LogSources.Launcher, kind, ex,
                UpdateService.Format(UpdateService.CurrentVersion), SessionStartUtc, terminating);
        }
        catch (Exception)
        {
            // ничего не поделать
        }
    }

    // ---- маскировка ----

    /// <summary>Ключ меток от службы (logConfig): метки лаунчера и службы совпадут.</summary>
    public static void UseSharedKey(byte[] key)
    {
        if (key.Length < 16)
            return;
        MaskerInstance.SetKey(key);
        MaskKeyShared = true;
    }

    public static void SetServers(IEnumerable<KeyValuePair<string, string>> servers) => MaskerInstance.SetServers(servers);

    public static void SetSecrets(IEnumerable<string> secrets) => MaskerInstance.SetSecrets(secrets);

    /// <summary>Удалить журнал лаунчера и файлы падений (кнопка «Удалить все журналы»). Возвращает освобожденные байты.</summary>
    public static long Clear()
    {
        var freed = (_store?.Clear() ?? 0) + LogFiles.Delete(Directory, CrashReport.FilePattern);
        if (!MaskKeyShared)
        {
            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            TryWriteKey(key);
            MaskerInstance.SetKey(key);
        }
        Info(LogAreas.Session, LogCodes.LogClear, "Журналы лаунчера удалены пользователем");
        return freed;
    }

    public static long UsageBytes() =>
        (_store?.UsageBytes() ?? 0) + LogFiles.Size(Directory, CrashReport.FilePattern);

    public static IReadOnlyList<LogLine> Read(DateTime fromUtc, DateTime toUtc, Func<LogLine, bool>? filter = null, int maxLines = int.MaxValue) =>
        _store?.Read(fromUtc, toUtc, filter, maxLines) ?? Array.Empty<LogLine>();

    public static string DescribeSettings(LogSettings s) =>
        $"журналы {(s.Storage == LogStorage.Memory ? "только в памяти" : "в файлах, " + s.RetentionDays + " дн.")}, " +
        $"скрывать IP-адреса {(s.HideAddresses ? "да" : "нет")}" + (s.DetailedUntilUtc is { } u ? ", подробный до " + LogLine.FormatTime(u) : string.Empty);

    private static string Name(LogStorage storage) => storage == LogStorage.Memory ? "только в памяти" : "в файлах";

    // ---- сеанс ----

    private static void WriteLock()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, LockName),
                LogLine.FormatTime(SessionStartUtc) + "\t" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // без файла сеанса не узнаем о падении — не страшно
        }
    }

    private static PreviousSession? ReadPreviousSession()
    {
        try
        {
            var path = Path.Combine(Directory, LockName);
            if (!File.Exists(path))
                return null;
            var parts = File.ReadAllText(path).Trim().Split('\t');
            if (parts.Length < 1 || !LogLine.TryParseTime(parts[0], out var start))
                return null;

            var crash = LogFiles.InPeriod(Directory, CrashReport.FilePrefix, CrashReport.FilePattern, start, DateTime.UtcNow).LastOrDefault();
            string? type = null;
            if (crash.Path is not null)
            {
                type = File.ReadLines(crash.Path).FirstOrDefault(l => l.StartsWith("Тип: ", StringComparison.Ordinal))?["Тип: ".Length..];
                type = type?.Split('.').LastOrDefault() ?? "исключение";
            }
            return new PreviousSession(start, crash.Path is null ? null : crash.Utc, type, crash.Path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] LocalKey()
    {
        var path = Path.Combine(Directory, ".key");
        try
        {
            if (File.Exists(path))
            {
                var key = File.ReadAllBytes(path);
                if (key.Length >= 16)
                    return key;
            }
        }
        catch (Exception)
        {
            // создадим новый
        }

        var created = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        TryWriteKey(created);
        return created;
    }

    private static void TryWriteKey(byte[] key)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllBytes(Path.Combine(Directory, ".key"), key);
        }
        catch (Exception)
        {
            // ключ в памяти
        }
    }

    /// <summary>Настройки журналов из settings.json до загрузки остальных настроек (журнал нужен раньше окон).</summary>
    public static LogSettings ReadSettings(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
                return new LogSettings();
            var node = JsonNode.Parse(File.ReadAllText(settingsPath))?["Logs"];
            if (node is null)
                return new LogSettings();
            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            return (node.Deserialize<LogSettings>(options) ?? new LogSettings()).Normalized();
        }
        catch (Exception)
        {
            return new LogSettings();
        }
    }

    /// <summary>Для тестов: закрыть журнал.</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _store?.Dispose();
            _store = null;
            _settings = new LogSettings();
            Previous = null;
            MaskKeyShared = false;
            MaskerInstance.HideAddresses = false;
            MaskerInstance.SetServers(Array.Empty<KeyValuePair<string, string>>());
            MaskerInstance.SetSecrets(Array.Empty<string>());
        }
    }
}
