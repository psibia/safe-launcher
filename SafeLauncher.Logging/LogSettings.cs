using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SafeLauncher.Logging;

/// <summary>Настройки журналов (раздел «Журналы»). Общие для лаунчера, службы и ядра.</summary>
public sealed class LogSettings
{
    public const int DefaultRetentionDays = 7;

    /// <summary>Сколько длится «Подробный журнал», потом выключается сам.</summary>
    public static readonly TimeSpan DetailedDuration = TimeSpan.FromHours(1);

    public static IReadOnlyList<int> RetentionChoices { get; } = new[] { 1, 3, 7, 14, 30 };

    public LogStorage Storage { get; set; } = LogStorage.Files;

    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>Скрывать IP-адреса и серверы подключений при записи (по умолчанию нет, решение Павла 14.10).</summary>
    public bool HideAddresses { get; set; }

    /// <summary>До какого времени (UTC) пишется подробный журнал; null — выключен.</summary>
    public DateTime? DetailedUntilUtc { get; set; }

    public bool IsDetailed(DateTime nowUtc) => DetailedUntilUtc is { } until && until > nowUtc;

    public LogSettings Normalized()
    {
        var days = RetentionChoices.Contains(RetentionDays)
            ? RetentionDays
            : RetentionChoices.OrderBy(d => Math.Abs(d - RetentionDays)).First();
        return new LogSettings
        {
            Storage = Enum.IsDefined(Storage) ? Storage : LogStorage.Files,
            RetentionDays = days,
            HideAddresses = HideAddresses,
            DetailedUntilUtc = DetailedUntilUtc is { } u ? DateTime.SpecifyKind(u, DateTimeKind.Utc) : null
        };
    }

    public LogSettings Clone() => new()
    {
        Storage = Storage,
        RetentionDays = RetentionDays,
        HideAddresses = HideAddresses,
        DetailedUntilUtc = DetailedUntilUtc
    };
}

/// <summary>Служебные файлы рядом с журналами: падения (<c>crash-*.txt</c>), конфиги ядра (<c>core-config-*.json</c>).</summary>
public static class LogFiles
{
    /// <summary>Имя файла со временем UTC: <c>префикс-ГГГГММДД-ЧЧммсс-ммм.расширение</c>.</summary>
    public static string TimedName(string prefix, DateTime utc, string extension) =>
        prefix + "-" + utc.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + extension;

    /// <summary>Время из имени, сделанного <see cref="TimedName"/>.</summary>
    public static DateTime? TimeOf(string path, string prefix)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            return null;
        var stamp = name.Substring(prefix.Length + 1);
        return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;
    }

    /// <summary>Файлы с префиксом в периоде, по времени.</summary>
    public static IReadOnlyList<(string Path, DateTime Utc)> InPeriod(string directory, string prefix, string pattern, DateTime fromUtc, DateTime toUtc)
    {
        try
        {
            if (!Directory.Exists(directory))
                return Array.Empty<(string, DateTime)>();
            return Directory.EnumerateFiles(directory, pattern)
                .Select(p => (Path: p, Utc: TimeOf(p, prefix)))
                .Where(x => x.Utc is { } t && t >= fromUtc && t <= toUtc)
                .Select(x => (x.Path, x.Utc!.Value))
                .OrderBy(x => x.Item2)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<(string, DateTime)>();
        }
    }

    /// <summary>Удалить файлы по шаблону старше срока и сверх лимита (сначала старые). Возвращает освобожденные байты.</summary>
    public static long Purge(string directory, string pattern, DateTime nowUtc, int retentionDays, long maxBytes)
    {
        long freed = 0;
        try
        {
            if (!Directory.Exists(directory))
                return 0;
            var files = Directory.EnumerateFiles(directory, pattern).Select(p => new FileInfo(p)).OrderBy(f => f.LastWriteTimeUtc).ToList();
            var keepFrom = nowUtc.AddDays(-retentionDays);
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (f.LastWriteTimeUtc >= keepFrom && total <= maxBytes)
                    continue;
                try
                {
                    var len = f.Length;
                    f.Delete();
                    freed += len;
                    total -= len;
                }
                catch (Exception)
                {
                    // занят
                }
            }
        }
        catch (Exception)
        {
            // папки нет или нет доступа
        }
        return freed;
    }

    public static long Delete(string directory, string pattern)
    {
        long freed = 0;
        try
        {
            if (!Directory.Exists(directory))
                return 0;
            foreach (var f in Directory.EnumerateFiles(directory, pattern).Select(p => new FileInfo(p)))
            {
                try
                {
                    var len = f.Length;
                    f.Delete();
                    freed += len;
                }
                catch (Exception)
                {
                    // занят
                }
            }
        }
        catch (Exception)
        {
            // нет папки
        }
        return freed;
    }

    public static long Size(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern).Sum(p => new FileInfo(p).Length) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
