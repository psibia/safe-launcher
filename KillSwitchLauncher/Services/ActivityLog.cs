using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KillSwitchLauncher.Services;

public enum LogKind
{
    Info,
    VpnUp,
    VpnDown,
    Launch,
    Closed,
    Failed,
    Blocked,
    Update
}

public sealed record ActivityEntry(DateTime Time, LogKind Kind, string Text);

/// <summary>
/// Журнал событий: обрывы и подключения VPN, запуски и закрытия приложений, обновления.
/// Хранится в %LOCALAPPDATA%\KillSwitchLauncher\events.log (строка = время, тип, текст через табуляцию),
/// последние <see cref="Keep"/> записей. Методы можно вызывать из любого потока.
/// </summary>
public sealed class ActivityLog
{
    public const int Keep = 500;
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly object _gate = new();
    private readonly List<ActivityEntry> _entries;
    private int _linesInFile;

    public ActivityLog()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillSwitchLauncher", "events.log"))
    {
    }

    /// <summary>Для тестов: журнал в указанном файле.</summary>
    internal ActivityLog(string filePath)
    {
        FilePath = filePath;
        _entries = Load(FilePath, out _linesInFile);
    }

    public string FilePath { get; }

    /// <summary>Новая запись. Вызывается в том потоке, где запись добавлена.</summary>
    public event EventHandler<ActivityEntry>? Added;

    /// <summary>Журнал очищен.</summary>
    public event EventHandler? Cleared;

    /// <summary>Записи от старых к новым.</summary>
    public IReadOnlyList<ActivityEntry> Snapshot()
    {
        lock (_gate)
            return _entries.ToArray();
    }

    public void Add(LogKind kind, string text)
    {
        var entry = new ActivityEntry(DateTime.Now, kind, Clean(text));

        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Keep)
                _entries.RemoveRange(0, _entries.Count - Keep);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                // Файл дописывается, а раз в сотню записей переписывается целиком, чтобы не рос бесконечно.
                if (_linesInFile >= Keep + 100)
                {
                    WriteAll();
                }
                else
                {
                    File.AppendAllText(FilePath, Format(entry) + Environment.NewLine, Encoding.UTF8);
                    _linesInFile++;
                }
            }
            catch (Exception)
            {
                // журнал не должен мешать работе: в памяти запись всё равно есть
            }
        }

        Added?.Invoke(this, entry);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            try
            {
                WriteAll();
            }
            catch (Exception)
            {
                // ничего страшного
            }
        }

        Cleared?.Invoke(this, EventArgs.Empty);
    }

    private void WriteAll()
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllLines(tmp, _entries.Select(Format), Encoding.UTF8);
        File.Move(tmp, FilePath, overwrite: true);
        _linesInFile = _entries.Count;
    }

    private static List<ActivityEntry> Load(string path, out int lines)
    {
        var result = new List<ActivityEntry>();
        lines = 0;
        try
        {
            if (!File.Exists(path))
                return result;

            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                lines++;
                var parts = line.Split('\t', 3);
                if (parts.Length == 3 &&
                    DateTime.TryParseExact(parts[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) &&
                    Enum.TryParse<LogKind>(parts[1], out var kind))
                {
                    result.Add(new ActivityEntry(time, kind, parts[2]));
                }
            }
        }
        catch (Exception)
        {
            // повреждённый или занятый файл: начинаем с пустого журнала
        }

        if (result.Count > Keep)
            result.RemoveRange(0, result.Count - Keep);
        return result;
    }

    private static string Format(ActivityEntry e) =>
        $"{e.Time.ToString(TimeFormat, CultureInfo.InvariantCulture)}\t{e.Kind}\t{e.Text}";

    private static string Clean(string text) => text.Replace('\t', ' ').Replace("\r", string.Empty).Replace('\n', ' ').Trim();
}
