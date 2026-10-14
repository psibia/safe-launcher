using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SafeLauncher.Logging;

public enum LogStorage
{
    /// <summary>В файлах по дням, со сроком хранения.</summary>
    Files,

    /// <summary>Только в памяти, пока работает программа.</summary>
    Memory
}

/// <summary>Порция строк при чтении журнала и где продолжить (null — дочитано).</summary>
public sealed record LogChunk(IReadOnlyList<LogLine> Lines, string? NextCursor);

/// <summary>
/// Хранилище одного писателя: файлы <c>префикс-ГГГГММДД[-N].log</c> по суткам UTC или кольцевой буфер в памяти.
/// Запись через очередь и фоновый поток (сброс раз в секунду и сразу для WARN/ERROR), поэтому вызывающий не ждет диска.
/// Ошибки записи не выходят наружу: строки считаются потерянными, и следующая удачная запись сообщает, сколько.
/// Удаление по сроку и размеру: при настройке, при старте и раз в час.
/// </summary>
public sealed class LogStore : IDisposable
{
    public const int QueueLimit = 10_000;

    /// <summary>Допуск на неупорядоченность строк внутри файла (строки разных потоков приходят в очередь не строго по времени).</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(10);

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly ConcurrentQueue<LogLine> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly object _writeLock = new();
    private readonly Func<DateTime> _clock;
    private readonly Thread? _thread;
    private readonly LinkedList<(long Seq, LogLine Line)> _memory = new();

    private int _queued;
    private long _dropped;
    private long _droppedReported;
    private long _seq;
    private long _writeFailures;
    private DateTime _retryAfterUtc;
    private DateTime _nextPurgeUtc;
    private volatile bool _disposed;

    private FileStream? _file;
    private string? _filePath;
    private DateTime _fileDay;
    private int _fileIndex;

    public LogStore(string directory, string prefix, string source, long maxTotalBytes, int memoryLines,
        Func<DateTime>? clock = null, bool background = true)
    {
        Directory = directory;
        Prefix = prefix;
        Source = source;
        MaxTotalBytes = Math.Max(1024, maxTotalBytes);
        MemoryLines = Math.Max(100, memoryLines);
        _clock = clock ?? (() => DateTime.UtcNow);
        _nextPurgeUtc = _clock();

        if (background)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "log-" + prefix, Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    public string Directory { get; }

    public string Prefix { get; }

    /// <summary>Источник служебных строк самого хранилища (о потерях).</summary>
    public string Source { get; }

    public long MaxTotalBytes { get; }

    /// <summary>Суточный файл не больше трети общего лимита: дальше открывается следующий файл того же дня.</summary>
    public long MaxFileBytes => MaxTotalBytes / 3;

    public int MemoryLines { get; }

    public LogStorage Storage { get; private set; } = LogStorage.Files;

    public int RetentionDays { get; private set; } = 7;

    /// <summary>Сколько строк потеряно (переполнение очереди, ошибки записи) с начала работы.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Новая строка принята (в потоке вызывающего). Для живого просмотра.</summary>
    public event Action<LogLine>? Added;

    /// <summary>
    /// Режим и срок хранения. Переход в память удаляет файлы (пользователь подтвердил это в настройках);
    /// переход в файлы сбрасывает накопленное в памяти на диск, чтобы текущий случай не потерялся.
    /// </summary>
    public void Configure(LogStorage storage, int retentionDays)
    {
        lock (_writeLock)
        {
            DrainLocked();
            RetentionDays = Math.Clamp(retentionDays, 1, 365);
            if (storage != Storage)
            {
                if (storage == LogStorage.Memory)
                {
                    CloseFile();
                    DeleteFilesLocked();
                    Storage = LogStorage.Memory;
                }
                else
                {
                    Storage = LogStorage.Files;
                    var saved = _memory.Select(m => m.Line).ToList();
                    _memory.Clear();
                    WriteLocked(saved);
                    FlushFile();
                }
            }

            if (Storage == LogStorage.Files)
                PurgeLocked();
        }
    }

    public void Append(LogLine line)
    {
        if (_disposed)
            return;

        var queued = Interlocked.Increment(ref _queued);
        if ((queued > QueueLimit && line.Level < LogLevel.Warn) || queued > QueueLimit * 2)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }

        _queue.Enqueue(line);
        if (line.Level >= LogLevel.Warn || _thread is null)
            _signal.Set();

        try
        {
            Added?.Invoke(line);
        }
        catch (Exception)
        {
            // подписчик не должен ломать запись
        }

        if (_thread is null)
            Flush();
    }

    /// <summary>Записать сразу и дождаться диска (падение программы: фоновый поток может не успеть).</summary>
    public void AppendNow(LogLine line)
    {
        var taken = false;
        try
        {
            Monitor.TryEnter(_writeLock, TimeSpan.FromSeconds(2), ref taken);
            if (taken)
            {
                DrainLocked();
                WriteLocked(new[] { line });
                FlushFile();
            }
            else if (Storage == LogStorage.Files)
            {
                // хранилище занято другим потоком (возможно, он и упал): пишем мимо него
                System.IO.Directory.CreateDirectory(Directory);
                using var fs = new FileStream(Path.Combine(Directory, FileName(line.TimeUtc.Date, 0)), FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                var bytes = Utf8.GetBytes(line.Format() + "\n");
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception)
        {
            // при падении больше ничего сделать нельзя
        }
        finally
        {
            if (taken)
                Monitor.Exit(_writeLock);
        }

        try
        {
            Added?.Invoke(line);
        }
        catch (Exception)
        {
            // подписчик не должен ломать запись
        }
    }

    /// <summary>Дописать очередь на диск (или в память).</summary>
    public void Flush()
    {
        lock (_writeLock)
        {
            DrainLocked();
            FlushFile();
        }
    }

    /// <summary>Все строки периода, по времени.</summary>
    public IReadOnlyList<LogLine> Read(DateTime fromUtc, DateTime toUtc, Func<LogLine, bool>? filter = null, int maxLines = int.MaxValue)
    {
        var result = new List<LogLine>();
        string? cursor = null;
        do
        {
            var chunk = ReadChunk(fromUtc, toUtc, cursor, 4 * 1024 * 1024, filter);
            result.AddRange(chunk.Lines);
            cursor = chunk.NextCursor;
        }
        while (cursor is not null && result.Count < maxLines);

        Sort(result);
        if (result.Count > maxLines)
            result.RemoveRange(0, result.Count - maxLines);
        return result;
    }

    /// <summary>
    /// Порция строк периода не больше <paramref name="maxBytes"/> (по длине текста). Продолжение — по <see cref="LogChunk.NextCursor"/>.
    /// Внутри порции и между порциями строки почти по времени (как писались); для точного порядка вызывающий сортирует.
    /// </summary>
    public LogChunk ReadChunk(DateTime fromUtc, DateTime toUtc, string? cursor, int maxBytes, Func<LogLine, bool>? filter = null)
    {
        maxBytes = Math.Max(4096, maxBytes);
        lock (_writeLock)
        {
            DrainLocked();
            FlushFile();

            if (Storage == LogStorage.Memory || (cursor?.StartsWith('m') ?? false))
                return ReadMemory(fromUtc, toUtc, cursor, maxBytes, filter);
        }

        return ReadFiles(fromUtc, toUtc, cursor, maxBytes, filter);
    }

    /// <summary>Сколько байт занимают файлы журнала.</summary>
    public long UsageBytes()
    {
        try
        {
            return ListFiles().Sum(f => SafeLength(f.Path));
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Удалить файлы старше срока и сверх лимита размера. Возвращает освобожденные байты.</summary>
    public long Purge()
    {
        lock (_writeLock)
            return PurgeLocked();
    }

    /// <summary>Удалить все файлы и строки в памяти. Возвращает освобожденные байты.</summary>
    public long Clear()
    {
        lock (_writeLock)
        {
            DrainLocked();
            _memory.Clear();
            CloseFile();
            return DeleteFilesLocked();
        }
    }

    /// <summary>Очистить только память (служба: лаунчер-владелец отключился в режиме «только в памяти»).</summary>
    public void ClearMemory()
    {
        lock (_writeLock)
        {
            if (Storage == LogStorage.Memory)
                DrainLocked();
            _memory.Clear();
        }
    }

    /// <summary>Переписать все сохраненные строки (перемаскировка после включения «Скрывать IP-адреса»).</summary>
    public void Rewrite(Func<LogLine, LogLine> transform)
    {
        lock (_writeLock)
        {
            DrainLocked();
            for (var node = _memory.First; node is not null; node = node.Next)
                node.Value = (node.Value.Seq, transform(node.Value.Line));

            CloseFile();
            foreach (var (path, _, _) in ListFiles())
            {
                try
                {
                    var tmp = path + ".tmp";
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(input, Utf8))
                    using (var output = new StreamWriter(new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read), Utf8))
                    {
                        output.NewLine = "\n";
                        string? raw;
                        while ((raw = reader.ReadLine()) is not null)
                        {
                            if (LogLine.TryParse(raw, out var parsed))
                                output.WriteLine(transform(parsed).Format());
                        }
                    }
                    File.Move(tmp, path, overwrite: true);
                }
                catch (Exception)
                {
                    // файл занят или испорчен: остается как был
                }
            }
        }
    }

    /// <summary>Файлы журнала от старых к новым.</summary>
    public IReadOnlyList<string> Files() => ListFiles().Select(f => f.Path).ToArray();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _signal.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        lock (_writeLock)
        {
            DrainLocked();
            CloseFile();
        }
    }

    /// <summary>По времени; строки с одинаковым временем остаются в порядке записи.</summary>
    public static void Sort(List<LogLine> lines)
    {
        // устойчивая сортировка: строки с одинаковым временем остаются в порядке записи
        var indexed = lines.Select((l, i) => (l, i)).OrderBy(x => x.l.TimeUtc).ThenBy(x => x.i).Select(x => x.l).ToList();
        lines.Clear();
        lines.AddRange(indexed);
    }

    private void Loop()
    {
        while (!_disposed)
        {
            _signal.WaitOne(1000);
            try
            {
                lock (_writeLock)
                {
                    DrainLocked();
                    FlushFile();
                    if (Storage == LogStorage.Files && _clock() >= _nextPurgeUtc)
                        PurgeLocked();
                }
            }
            catch (Exception)
            {
                // фоновая запись не должна падать
            }
        }
    }

    private void DrainLocked()
    {
        if (_queue.IsEmpty)
            return;

        var batch = new List<LogLine>(Math.Min(_queued + 1, 4096));
        while (_queue.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _queued);
            batch.Add(line);
        }

        var dropped = Interlocked.Read(ref _dropped);
        if (dropped > _droppedReported && batch.Count > 0)
        {
            var lost = dropped - _droppedReported;
            _droppedReported = dropped;
            batch.Add(new LogLine(_clock(), Source, LogLevel.Warn, LogAreas.Session, LogCodes.LogDropped, LogLine.None,
                $"Журнал: пропущено строк: {lost} (переполнение очереди или ошибка записи), всего с начала работы {dropped}"));
        }

        WriteLocked(batch);
    }

    private void WriteLocked(IReadOnlyList<LogLine> lines)
    {
        if (lines.Count == 0)
            return;

        if (Storage == LogStorage.Memory)
        {
            foreach (var line in lines)
            {
                _memory.AddLast((++_seq, line));
                if (_memory.Count > MemoryLines)
                    _memory.RemoveFirst();
            }
            return;
        }

        var now = _clock();
        if (now < _retryAfterUtc)
        {
            Interlocked.Add(ref _dropped, lines.Count);
            return;
        }

        try
        {
            // файл могли удалить снаружи (блокнот, очистка вручную): тогда откроем заново
            if (_filePath is not null && !File.Exists(_filePath))
                CloseFile();

            foreach (var line in lines)
            {
                var bytes = Utf8.GetBytes(line.Format() + "\n");
                EnsureFile(line.TimeUtc, bytes.Length);
                _file!.Write(bytes, 0, bytes.Length);
            }
            _writeFailures = 0;
        }
        catch (Exception)
        {
            // диск полон, нет доступа: не блокируем работу, пробуем снова через минуту
            CloseFile();
            _writeFailures++;
            _retryAfterUtc = now.AddMinutes(1);
            Interlocked.Add(ref _dropped, lines.Count);
        }
    }

    private void EnsureFile(DateTime timeUtc, int nextBytes)
    {
        var day = timeUtc.Date;
        if (_file is not null && _fileDay == day)
        {
            if (_file.Length + nextBytes <= MaxFileBytes || _file.Length == 0)
                return;
            CloseFile();
            OpenFile(day, _fileIndex + 1);
            return;
        }

        CloseFile();
        System.IO.Directory.CreateDirectory(Directory);
        // продолжаем последний файл этого дня
        var last = ListFiles().Where(f => f.Day == day).Select(f => f.Index).DefaultIfEmpty(0).Max();
        OpenFile(day, last);
        if (_file!.Length + nextBytes > MaxFileBytes && _file.Length > 0)
        {
            CloseFile();
            OpenFile(day, last + 1);
        }
    }

    private void OpenFile(DateTime day, int index)
    {
        var path = Path.Combine(Directory, FileName(day, index));
        _file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        _filePath = path;
        _fileDay = day;
        _fileIndex = index;
    }

    private void FlushFile()
    {
        try
        {
            _file?.Flush(true);
        }
        catch (Exception)
        {
            CloseFile();
        }
    }

    private void CloseFile()
    {
        try
        {
            _file?.Dispose();
        }
        catch (Exception)
        {
            // уже не важно
        }
        _file = null;
        _filePath = null;
    }

    private long PurgeLocked()
    {
        _nextPurgeUtc = _clock().AddHours(1);
        long freed = 0;
        try
        {
            var files = ListFiles().ToList();
            var keepFrom = _clock().AddDays(-RetentionDays);
            foreach (var f in files.ToList())
            {
                if (f.Day.AddDays(1) <= keepFrom)
                {
                    if (f.Path == _filePath)
                        CloseFile();
                    freed += Delete(f.Path);
                    files.Remove(f);
                }
            }

            var total = files.Sum(f => SafeLength(f.Path));
            foreach (var f in files)
            {
                if (total <= MaxTotalBytes)
                    break;
                if (f.Path == _filePath)
                    CloseFile();
                var len = Delete(f.Path);
                freed += len;
                total -= len;
            }
        }
        catch (Exception)
        {
            // попробуем через час
        }
        return freed;
    }

    private long DeleteFilesLocked()
    {
        long freed = 0;
        try
        {
            foreach (var f in ListFiles())
                freed += Delete(f.Path);
        }
        catch (Exception)
        {
            // папки нет
        }
        return freed;
    }

    private static long Delete(string path)
    {
        try
        {
            var len = SafeLength(path);
            File.Delete(path);
            return len;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private string FileName(DateTime day, int index) =>
        Prefix + "-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + (index > 1 ? "-" + index.ToString(CultureInfo.InvariantCulture) : string.Empty) + ".log";

    private List<(string Path, DateTime Day, int Index)> ListFiles()
    {
        var result = new List<(string, DateTime, int)>();
        if (!System.IO.Directory.Exists(Directory))
            return result;

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, Prefix + "-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path).Substring(Prefix.Length + 1);
            var parts = name.Split('-');
            if (parts.Length is < 1 or > 2 ||
                !DateTime.TryParseExact(parts[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day))
                continue;
            var index = 1;
            if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 2))
                continue;
            result.Add((path, DateTime.SpecifyKind(day.Date, DateTimeKind.Utc), index));
        }

        return result.OrderBy(f => f.Item2).ThenBy(f => f.Item3).ToList();
    }

    private LogChunk ReadMemory(DateTime fromUtc, DateTime toUtc, string? cursor, int maxBytes, Func<LogLine, bool>? filter)
    {
        long after = 0;
        if (cursor is not null && cursor.StartsWith('m'))
            long.TryParse(cursor.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out after);

        var lines = new List<LogLine>();
        var bytes = 0;
        foreach (var (seq, line) in _memory)
        {
            if (seq <= after || line.TimeUtc < fromUtc || line.TimeUtc > toUtc || (filter is not null && !filter(line)))
                continue;
            lines.Add(line);
            bytes += line.Text.Length + 64;
            if (bytes >= maxBytes)
                return new LogChunk(lines, "m" + seq.ToString(CultureInfo.InvariantCulture));
        }
        return new LogChunk(lines, null);
    }

    private LogChunk ReadFiles(DateTime fromUtc, DateTime toUtc, string? cursor, int maxBytes, Func<LogLine, bool>? filter)
    {
        var files = ListFiles()
            .Where(f => f.Day >= (fromUtc - Slack).Date && f.Day <= (toUtc + Slack).Date)
            .ToList();

        string? startFile = null;
        long startOffset = -1;
        if (cursor is not null && cursor.StartsWith('f'))
        {
            var sep = cursor.LastIndexOf('|');
            if (sep > 1 && long.TryParse(cursor.AsSpan(sep + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var off))
            {
                startFile = cursor.Substring(1, sep - 1);
                startOffset = off;
            }
        }

        var lines = new List<LogLine>();
        var bytes = 0;
        var started = startFile is null;
        foreach (var f in files)
        {
            var name = Path.GetFileName(f.Path);
            long offset = 0;
            if (!started)
            {
                if (!string.Equals(name, startFile, StringComparison.OrdinalIgnoreCase))
                    continue;
                started = true;
                offset = startOffset;
            }
            else
            {
                offset = -1;
            }

            try
            {
                using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
                if (offset < 0)
                    offset = FindOffset(fs, fromUtc - Slack);
                fs.Seek(offset, SeekOrigin.Begin);

                var reader = new LineReader(fs);
                while (reader.Next(out var raw, out var end))
                {
                    if (!LogLine.TryParse(raw, out var line))
                        continue;
                    if (line.TimeUtc > toUtc + Slack)
                        break;
                    if (line.TimeUtc < fromUtc || line.TimeUtc > toUtc || (filter is not null && !filter(line)))
                        continue;
                    lines.Add(line);
                    bytes += raw!.Length;
                    if (bytes >= maxBytes)
                        return new LogChunk(lines, "f" + name + "|" + end.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception)
            {
                // файл удален или недоступен: дальше
            }
        }

        return new LogChunk(lines, null);
    }

    /// <summary>Позиция первой строки не раньше <paramref name="fromUtc"/> (двоичный поиск по началам строк).</summary>
    private static long FindOffset(FileStream fs, DateTime fromUtc)
    {
        long lo = 0, hi = fs.Length;
        var steps = 0;
        while (hi - lo > 64 * 1024 && steps++ < 40)
        {
            var mid = lo + (hi - lo) / 2;
            var lineStart = NextLineStart(fs, mid);
            if (lineStart < 0 || lineStart >= hi)
            {
                hi = mid;
                continue;
            }
            fs.Seek(lineStart, SeekOrigin.Begin);
            var reader = new LineReader(fs);
            if (!reader.Next(out var raw, out _) || !TryTime(raw, out var time))
            {
                hi = mid;
                continue;
            }
            if (time < fromUtc)
                lo = lineStart;
            else
                hi = mid;
        }
        return lo;
    }

    private static bool TryTime(string? raw, out DateTime time)
    {
        time = default;
        if (raw is null)
            return false;
        var tab = raw.IndexOf('\t');
        return tab > 0 && LogLine.TryParseTime(raw[..tab], out time);
    }

    private static long NextLineStart(FileStream fs, long position)
    {
        fs.Seek(position, SeekOrigin.Begin);
        int b;
        var pos = position;
        while ((b = fs.ReadByte()) >= 0)
        {
            pos++;
            if (b == '\n')
                return pos;
        }
        return -1;
    }

    /// <summary>Чтение строк из потока с позицией конца каждой строки. Недописанная последняя строка не отдается.</summary>
    private sealed class LineReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private readonly MemoryStream _line = new();
        private int _len;
        private int _pos;
        private long _position;

        public LineReader(Stream stream)
        {
            _stream = stream;
            _position = stream.Position;
        }

        public bool Next(out string? line, out long endPosition)
        {
            _line.SetLength(0);
            while (true)
            {
                if (_pos >= _len)
                {
                    _len = _stream.Read(_buffer, 0, _buffer.Length);
                    _pos = 0;
                    if (_len <= 0)
                    {
                        line = null;
                        endPosition = _position;
                        return false;
                    }
                }

                var idx = Array.IndexOf(_buffer, (byte)'\n', _pos, _len - _pos);
                if (idx < 0)
                {
                    _line.Write(_buffer, _pos, _len - _pos);
                    _position += _len - _pos;
                    _pos = _len;
                    continue;
                }

                _line.Write(_buffer, _pos, idx - _pos);
                _position += idx - _pos + 1;
                _pos = idx + 1;
                line = Utf8.GetString(_line.GetBuffer(), 0, (int)_line.Length);
                endPosition = _position;
                return true;
            }
        }
    }
}
