using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KillSwitchLauncher.Services;

/// <summary>Строка журнала DNS: запрос или отметка («Новый запуск», «Журнал выключен»).</summary>
public sealed class DnsEntry
{
    /// <summary>Порядковый номер в журнале, растёт с каждой записью.</summary>
    public long Seq { get; internal set; }

    /// <summary>Местное время.</summary>
    public DateTime Time { get; init; }

    /// <summary>Нормализованный домен; у отметки — её текст.</summary>
    public string Domain { get; init; } = string.Empty;

    public ushort Type { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public bool IsMarker { get; init; }

    public string TypeText => IsMarker ? string.Empty : DnsNames.TypeName(Type);
}

/// <summary>Сводка по одному домену для режима «Только уникальные».</summary>
public sealed record DnsDomainSummary(string Domain, int Count, DateTime LastSeen, IReadOnlyList<ushort> Types);

/// <summary>Что появилось в журнале после указанной записи.</summary>
/// <param name="Entries">Новые записи по порядку.</param>
/// <param name="Reset">Часть записей уже вытеснена из журнала: список надо перечитать целиком (он в Entries).</param>
public sealed record DnsJournalRead(IReadOnlyList<DnsEntry> Entries, bool Reset);

/// <summary>Журнал целиком на один момент.</summary>
public sealed record DnsJournalSnapshot(IReadOnlyList<DnsEntry> Entries, IReadOnlyList<DnsDomainSummary> Domains, long LastSeq);

/// <summary>
/// Журнал DNS одного приложения. Хранит последние Capacity записей (кольцевой буфер: старые вытесняются)
/// и счётчики по каждому домену за всё время. Потокобезопасен: записи приходят из фонового потока,
/// а окно журнала забирает новые пачкой по таймеру.
/// </summary>
public sealed class DnsJournal
{
    public const int DefaultCapacity = 5000;

    private readonly object _gate = new();
    private readonly Queue<DnsEntry> _entries = new();
    private readonly Dictionary<string, DomainCounter> _domains = new(StringComparer.Ordinal);
    private long _lastSeq;
    private long _total;

    public DnsJournal(string appName, int capacity = DefaultCapacity)
    {
        AppName = appName;
        Capacity = Math.Max(1, capacity);
    }

    public string AppName { get; set; }

    public int Capacity { get; }

    /// <summary>Сколько запросов записано за всё время (вытесненные тоже).</summary>
    public long TotalQueries
    {
        get
        {
            lock (_gate)
                return _total;
        }
    }

    public int UniqueDomains
    {
        get
        {
            lock (_gate)
                return _domains.Count;
        }
    }

    /// <summary>Есть ли что показать.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
                return _entries.Count == 0;
        }
    }

    public void AddQuery(DateTime time, string domain, ushort type, int processId, string processName)
    {
        lock (_gate)
        {
            var entry = Append(new DnsEntry { Time = time, Domain = domain, Type = type, ProcessId = processId, ProcessName = processName });
            _total++;

            if (!_domains.TryGetValue(domain, out var counter))
                _domains[domain] = counter = new DomainCounter { FirstSeq = entry.Seq };
            counter.Count++;
            counter.LastSeen = time;
            if (!counter.Types.Contains(type))
                counter.Types.Add(type);
        }
    }

    public void AddMarker(DateTime time, string text)
    {
        lock (_gate)
            Append(new DnsEntry { Time = time, Domain = text, IsMarker = true });
    }

    /// <summary>Записи с номером больше afterSeq. 0 — все, что есть.</summary>
    public DnsJournalRead ReadSince(long afterSeq)
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
                return new DnsJournalRead(Array.Empty<DnsEntry>(), Reset: afterSeq > _lastSeq);

            var first = _entries.Peek().Seq;
            if (afterSeq > 0 && afterSeq < first - 1)
                return new DnsJournalRead(_entries.ToList(), Reset: true); // окно отстало больше, чем на весь буфер

            return new DnsJournalRead(_entries.Where(e => e.Seq > afterSeq).ToList(), Reset: false);
        }
    }

    /// <summary>Домены по порядку первого появления, со счётчиками за всё время.</summary>
    public IReadOnlyList<DnsDomainSummary> Domains()
    {
        lock (_gate)
            return DomainsLocked();
    }

    /// <summary>Записи и домены одним снимком: окно журнала берёт его при открытии, а дальше читает ReadSince(LastSeq).</summary>
    public DnsJournalSnapshot Snapshot()
    {
        lock (_gate)
            return new DnsJournalSnapshot(_entries.ToList(), DomainsLocked(), _lastSeq);
    }

    private List<DnsDomainSummary> DomainsLocked() =>
        _domains
            .OrderBy(d => d.Value.FirstSeq)
            .Select(d => new DnsDomainSummary(d.Key, d.Value.Count, d.Value.LastSeen, d.Value.Types.ToList()))
            .ToList();

    private DnsEntry Append(DnsEntry entry)
    {
        entry.Seq = ++_lastSeq;
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity)
            _entries.Dequeue();
        return entry;
    }

    private sealed class DomainCounter
    {
        public long FirstSeq;
        public int Count;
        public DateTime LastSeen;
        public readonly List<ushort> Types = new();
    }
}

/// <summary>Приведение доменов к одному виду и названия типов DNS-записей.</summary>
public static class DnsNames
{
    private static readonly IdnMapping Idn = new();

    /// <summary>
    /// Чистый домен: без пробелов, схемы, логина, порта, пути и точки на конце, в нижнем регистре.
    /// «HTTPS://User@Example.COM:443/path» → «example.com». null, если домена не осталось.
    /// Национальные домены остаются в виде punycode (xn--…), как их видит DNS.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim().Trim('\0');

        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            s = s[(scheme + 3)..];

        var cut = s.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0)
            s = s[..cut];

        var at = s.LastIndexOf('@');
        if (at >= 0)
            s = s[(at + 1)..];

        // Порт: «host:443». Адрес IPv6 в квадратных скобках оставляем без скобок, двоеточия в нём — часть адреса.
        if (s.StartsWith('[') && s.IndexOf(']') is var close and > 0)
            s = s[1..close];
        else if (s.Count(c => c == ':') == 1)
            s = s[..s.IndexOf(':')];

        s = s.Trim().TrimEnd('.').ToLowerInvariant();
        if (s.Length == 0)
            return null;

        // Если приложение передало имя в юникоде, приводим к punycode, чтобы одинаковые домены совпадали.
        if (s.Any(c => c > 127))
        {
            try
            {
                s = Idn.GetAscii(s);
            }
            catch (ArgumentException)
            {
                // не домен по правилам IDN: оставляем как есть
            }
        }

        return s;
    }

    /// <summary>Как показать домен человеку: punycode переводится обратно в национальные буквы.</summary>
    public static string ToDisplay(string domain)
    {
        if (!domain.Contains("xn--", StringComparison.Ordinal))
            return domain;

        try
        {
            return Idn.GetUnicode(domain);
        }
        catch (ArgumentException)
        {
            return domain;
        }
    }

    public static string TypeName(ushort type) => type switch
    {
        1 => "A",
        2 => "NS",
        5 => "CNAME",
        6 => "SOA",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        28 => "AAAA",
        33 => "SRV",
        64 => "SVCB",
        65 => "HTTPS",
        255 => "ANY",
        0 => "—",
        _ => $"TYPE{type}"
    };
}
