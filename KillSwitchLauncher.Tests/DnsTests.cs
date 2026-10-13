using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Домены: приведение к одному виду, показ национальных доменов, названия типов записей.</summary>
public class DnsNamesTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM. ", "example.com")]
    [InlineData("discord.com.", "discord.com")]
    [InlineData("https://user:pw@Sub.Example.com:8443/path?q=1#top", "sub.example.com")]
    [InlineData("http://example.com", "example.com")]
    [InlineData("example.com:443", "example.com")]
    [InlineData("example.com/path", "example.com")]
    [InlineData("example.com?x", "example.com")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("192.168.1.1:80", "192.168.1.1")]
    [InlineData("ЯНДЕКС.РФ", "xn--d1acpjx3f.xn--p1ai")]
    [InlineData("xn--d1acpjx3f.xn--p1ai", "xn--d1acpjx3f.xn--p1ai")]
    public void Normalize_GivesCleanDomain(string raw, string expected) =>
        Assert.Equal(expected, DnsNames.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("https://")]
    public void Normalize_NothingLeft_IsNull(string? raw) =>
        Assert.Null(DnsNames.Normalize(raw));

    [Fact]
    public void ToDisplay_ShowsNationalLetters()
    {
        Assert.Equal("яндекс.рф", DnsNames.ToDisplay("xn--d1acpjx3f.xn--p1ai"));
        Assert.Equal("example.com", DnsNames.ToDisplay("example.com"));
    }

    [Theory]
    [InlineData(1, "A")]
    [InlineData(28, "AAAA")]
    [InlineData(5, "CNAME")]
    [InlineData(65, "HTTPS")]
    [InlineData(64, "SVCB")]
    [InlineData(12, "PTR")]
    [InlineData(16, "TXT")]
    [InlineData(0, "—")]
    [InlineData(99, "TYPE99")]
    public void TypeName_IsReadable(ushort type, string expected) =>
        Assert.Equal(expected, DnsNames.TypeName(type));
}

/// <summary>Журнал одного приложения: кольцевой буфер, чтение новых записей, счётчики по доменам.</summary>
public class DnsJournalTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Empty()
    {
        var journal = new DnsJournal("Discord");

        Assert.True(journal.IsEmpty);
        Assert.Equal(0, journal.TotalQueries);
        Assert.Equal(0, journal.UniqueDomains);
        Assert.Equal(DnsJournal.DefaultCapacity, journal.Capacity);

        var read = journal.ReadSince(0);
        Assert.Empty(read.Entries);
        Assert.False(read.Reset);
        Assert.Equal(0, journal.Snapshot().LastSeq);
    }

    [Fact]
    public void Queries_AreCountedPerDomain_InOrderOfFirstAppearance()
    {
        var journal = new DnsJournal("Discord");
        journal.AddQuery(T0, "discord.com", 1, 10, "Discord.exe");
        journal.AddQuery(T0.AddSeconds(1), "cdn.discordapp.com", 1, 10, "Discord.exe");
        journal.AddQuery(T0.AddSeconds(2), "discord.com", 28, 11, "Discord.exe");

        Assert.False(journal.IsEmpty);
        Assert.Equal(3, journal.TotalQueries);
        Assert.Equal(2, journal.UniqueDomains);

        var domains = journal.Domains();
        Assert.Equal(new[] { "discord.com", "cdn.discordapp.com" }, domains.Select(d => d.Domain));
        Assert.Equal(2, domains[0].Count);
        Assert.Equal(T0.AddSeconds(2), domains[0].LastSeen);
        Assert.Equal(new ushort[] { 1, 28 }, domains[0].Types);
        Assert.Equal(1, domains[1].Count);
    }

    [Fact]
    public void Entries_KeepEverything()
    {
        var journal = new DnsJournal("Discord");
        journal.AddQuery(T0, "discord.com", 28, 42, "Discord.exe");

        var entry = Assert.Single(journal.ReadSince(0).Entries);
        Assert.Equal(1, entry.Seq);
        Assert.Equal(T0, entry.Time);
        Assert.Equal("discord.com", entry.Domain);
        Assert.Equal(28, entry.Type);
        Assert.Equal("AAAA", entry.TypeText);
        Assert.Equal(42, entry.ProcessId);
        Assert.Equal("Discord.exe", entry.ProcessName);
        Assert.False(entry.IsMarker);
    }

    [Fact]
    public void Markers_AreShownButNotCounted()
    {
        var journal = new DnsJournal("Discord");
        journal.AddMarker(T0, "Новый запуск");

        Assert.False(journal.IsEmpty);
        Assert.Equal(0, journal.TotalQueries);
        Assert.Equal(0, journal.UniqueDomains);

        var marker = Assert.Single(journal.ReadSince(0).Entries);
        Assert.True(marker.IsMarker);
        Assert.Equal("Новый запуск", marker.Domain);
        Assert.Equal(string.Empty, marker.TypeText);
    }

    [Fact]
    public void ReadSince_ReturnsOnlyNewEntries()
    {
        var journal = new DnsJournal("Discord");
        journal.AddQuery(T0, "a.com", 1, 1, "a.exe");
        journal.AddQuery(T0, "b.com", 1, 1, "a.exe");
        var seen = journal.Snapshot().LastSeq;

        journal.AddQuery(T0, "c.com", 1, 1, "a.exe");

        var read = journal.ReadSince(seen);
        Assert.False(read.Reset);
        Assert.Equal(new[] { "c.com" }, read.Entries.Select(e => e.Domain));
        Assert.Empty(journal.ReadSince(read.Entries[^1].Seq).Entries);
    }

    [Fact]
    public void OldEntries_AreDropped_ButCountersRemain()
    {
        var journal = new DnsJournal("Discord", capacity: 3);
        for (var i = 1; i <= 5; i++)
            journal.AddQuery(T0.AddSeconds(i), $"d{i}.com", 1, 1, "a.exe");

        var all = journal.ReadSince(0);
        Assert.False(all.Reset);
        Assert.Equal(new long[] { 3, 4, 5 }, all.Entries.Select(e => e.Seq));

        Assert.Equal(5, journal.TotalQueries);
        Assert.Equal(5, journal.UniqueDomains); // за всё время, не только то, что осталось в буфере

        // Окно видело записи до №2 включительно — продолжение (№3) ещё в буфере.
        var next = journal.ReadSince(2);
        Assert.False(next.Reset);
        Assert.Equal(new long[] { 3, 4, 5 }, next.Entries.Select(e => e.Seq));

        // Окно видело только №1 — №2 уже вытеснена: список надо перечитать целиком.
        var lagging = journal.ReadSince(1);
        Assert.True(lagging.Reset);
        Assert.Equal(new long[] { 3, 4, 5 }, lagging.Entries.Select(e => e.Seq));
    }

    [Fact]
    public void Snapshot_IsConsistent()
    {
        var journal = new DnsJournal("Discord");
        journal.AddQuery(T0, "a.com", 1, 1, "a.exe");
        journal.AddMarker(T0, "Новый запуск");
        journal.AddQuery(T0, "a.com", 28, 1, "a.exe");

        var snapshot = journal.Snapshot();

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Equal(3, snapshot.LastSeq);
        var domain = Assert.Single(snapshot.Domains);
        Assert.Equal(2, domain.Count);
    }
}

/// <summary>Монитор: какие запросы попадают в журналы и когда работает источник событий. Источник и «чьи процессы» — подставные.</summary>
public class DnsMonitorTests
{
    private static readonly Guid Discord = Guid.NewGuid();
    private static readonly Guid Telegram = Guid.NewGuid();

    private sealed class FakeSource : IDnsEventSource
    {
        public bool Disposed { get; private set; }
        public event EventHandler? Stopped;
        public void Stop() => Stopped?.Invoke(this, EventArgs.Empty);
        public void Dispose() => Disposed = true;
    }

    /// <summary>Подставной источник: запоминает, куда отдавать события, и сколько раз его запускали.</summary>
    private sealed class Harness
    {
        public readonly Dictionary<int, (Guid App, string Name)> Owners = new();
        public readonly List<int> Resolved = new();
        public readonly List<FakeSource> Sources = new();
        public Action<DnsQueryEvent>? Send;
        public Func<Task>? BeforeStart;
        public Exception? Fail;
        public readonly DnsMonitor Monitor;

        public Harness(int capacity = DnsJournal.DefaultCapacity)
        {
            Monitor = new DnsMonitor(Resolve, Start, capacity);
        }

        public FakeSource Source => Sources[^1];

        private bool Resolve(int pid, out Guid appId, out string name)
        {
            Resolved.Add(pid);
            if (Owners.TryGetValue(pid, out var owner))
            {
                (appId, name) = owner;
                return true;
            }

            appId = Guid.Empty;
            name = string.Empty;
            return false;
        }

        private async Task<IDnsEventSource> Start(Action<DnsQueryEvent> onEvent)
        {
            if (BeforeStart is not null)
                await BeforeStart();
            if (Fail is not null)
                throw Fail;
            Send = onEvent;
            var source = new FakeSource();
            Sources.Add(source);
            return source;
        }

        public void Query(int pid, string name, ushort type = 1, DateTime? time = null) =>
            Send!(new DnsQueryEvent(time ?? DateTime.UtcNow, pid, name, type));
    }

    private static async Task<Harness> Running()
    {
        var h = new Harness();
        h.Owners[100] = (Discord, "Discord.exe");
        h.Owners[200] = (Telegram, "Telegram.exe");
        h.Monitor.SetTracking(Discord, "Discord", true);
        await h.Monitor.StartAsync();
        return h;
    }

    [Fact]
    public async Task OnlyTrackedApps_GetTheirQueries()
    {
        var h = await Running();

        h.Query(100, "Discord.COM.");
        h.Query(200, "telegram.org"); // журнал Telegram не включён
        h.Query(300, "windowsupdate.com"); // не приложение лаунчера

        var journal = h.Monitor.FindJournal(Discord)!;
        var entry = Assert.Single(journal.ReadSince(0).Entries);
        Assert.Equal("discord.com", entry.Domain);
        Assert.Equal(100, entry.ProcessId);
        Assert.Equal("Discord.exe", entry.ProcessName);
        Assert.Null(h.Monitor.FindJournal(Telegram));
    }

    [Fact]
    public async Task Strangers_AreRememberedForAWhile()
    {
        var h = await Running();

        h.Query(300, "a.com");
        h.Query(300, "b.com");
        h.Query(300, "c.com");

        Assert.Equal(1, h.Resolved.Count(pid => pid == 300));
    }

    [Fact]
    public async Task NewLaunch_ForgetsStrangers()
    {
        // Новый процесс приложения мог получить PID, который только что был «чужим».
        var h = await Running();
        h.Query(300, "a.com");

        h.Owners[300] = (Discord, "Update.exe");
        h.Monitor.NoteLaunch(Discord);
        h.Query(300, "b.com");

        Assert.Equal(new[] { "b.com" }, h.Monitor.FindJournal(Discord)!.ReadSince(0).Entries.Where(e => !e.IsMarker).Select(e => e.Domain));
    }

    [Fact]
    public async Task SameQueryWithinASecond_IsWrittenOnce()
    {
        var h = await Running();
        var t = DateTime.UtcNow;

        h.Query(100, "discord.com", 1, t);
        h.Query(100, "discord.com", 1, t.AddMilliseconds(300)); // Windows пишет начало и конец запроса
        h.Query(100, "discord.com", 28, t.AddMilliseconds(400)); // другой тип — отдельный запрос
        h.Query(100, "discord.com", 1, t.AddSeconds(2)); // повтор позже — новый запрос

        var types = h.Monitor.FindJournal(Discord)!.ReadSince(0).Entries.Select(e => e.Type);
        Assert.Equal(new ushort[] { 1, 28, 1 }, types);
    }

    [Fact]
    public async Task EmptyNames_AreSkipped()
    {
        var h = await Running();

        h.Query(100, "");
        h.Query(100, " . ");

        Assert.True(h.Monitor.FindJournal(Discord)!.IsEmpty);
    }

    [Fact]
    public async Task Markers_OnlyInNonEmptyJournal()
    {
        var h = await Running();
        var journal = h.Monitor.FindJournal(Discord)!;

        h.Monitor.NoteLaunch(Discord);
        Assert.True(journal.IsEmpty); // в пустом журнале разделитель не нужен

        h.Query(100, "discord.com");
        h.Monitor.NoteLaunch(Discord);
        h.Monitor.SetTracking(Discord, "Discord", false);
        h.Monitor.SetTracking(Discord, "Discord", true);

        var markers = journal.ReadSince(0).Entries.Where(e => e.IsMarker).Select(e => e.Domain);
        Assert.Equal(new[] { "Новый запуск", "Журнал выключен", "Журнал снова включён" }, markers);
    }

    [Fact]
    public async Task TurnedOff_JournalIsKept_ButNotWritten()
    {
        var h = await Running();
        h.Owners[200] = (Telegram, "Telegram.exe");
        h.Monitor.SetTracking(Telegram, "Telegram", true);

        h.Query(100, "discord.com");
        h.Monitor.SetTracking(Discord, "Discord", false);
        h.Query(100, "later.com");

        var journal = h.Monitor.FindJournal(Discord)!;
        Assert.Equal(1, journal.TotalQueries);
        Assert.False(h.Monitor.IsTracking(Discord));
        Assert.True(h.Monitor.IsActive); // Telegram ещё записывается
    }

    [Fact]
    public async Task Start_IsLazy_AndHappensOnce()
    {
        var h = new Harness();
        h.Monitor.SetTracking(Discord, "Discord", true);

        Assert.False(h.Monitor.IsActive); // включение журнала само источник не запускает
        Assert.Empty(h.Sources);

        var gate = new TaskCompletionSource();
        h.BeforeStart = () => gate.Task;
        var first = h.Monitor.StartAsync();
        var second = h.Monitor.StartAsync(); // второй клик, пока идёт запуск
        gate.SetResult();
        await Task.WhenAll(first, second);
        await h.Monitor.StartAsync(); // уже работает

        Assert.Single(h.Sources);
        Assert.True(h.Monitor.IsActive);
    }

    [Fact]
    public async Task Start_RaisesStateChanged()
    {
        var h = new Harness();
        var changed = 0;
        h.Monitor.StateChanged += (_, _) => changed++;
        h.Monitor.SetTracking(Discord, "Discord", true);

        await h.Monitor.StartAsync();

        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task TurnedOffWhileStarting_SourceIsClosed()
    {
        var h = new Harness();
        h.Monitor.SetTracking(Discord, "Discord", true);
        var gate = new TaskCompletionSource();
        h.BeforeStart = () => gate.Task;

        var start = h.Monitor.StartAsync();
        h.Monitor.SetTracking(Discord, "Discord", false);
        gate.SetResult();
        await start;

        Assert.False(h.Monitor.IsActive);
        Assert.True(h.Source.Disposed);
    }

    [Fact]
    public async Task StartFailure_IsReported_AndCanBeRetried()
    {
        var h = new Harness();
        h.Monitor.SetTracking(Discord, "Discord", true);
        h.Fail = new Win32Exception(1223);

        var error = await Assert.ThrowsAsync<Win32Exception>(h.Monitor.StartAsync);
        Assert.Equal(1223, error.NativeErrorCode);
        Assert.False(h.Monitor.IsActive);

        h.Fail = null;
        await h.Monitor.StartAsync();
        Assert.True(h.Monitor.IsActive);
    }

    [Fact]
    public async Task StopIfIdle_StopsOnlyWhenNothingIsTracked()
    {
        var h = await Running();
        var changed = 0;
        h.Monitor.StateChanged += (_, _) => changed++;

        h.Monitor.StopIfIdle();
        Assert.True(h.Monitor.IsActive);
        Assert.False(h.Source.Disposed);

        h.Monitor.SetTracking(Discord, "Discord", false);
        h.Monitor.StopIfIdle();

        Assert.False(h.Monitor.IsActive);
        Assert.True(h.Source.Disposed);
        Assert.Equal(1, changed);
        Assert.NotNull(h.Monitor.FindJournal(Discord)); // журнал остаётся до закрытия лаунчера
    }

    [Fact]
    public async Task SourceStoppedByItself_IsReported()
    {
        var h = await Running();
        var lost = 0;
        h.Monitor.SourceLost += (_, _) => lost++;

        h.Source.Stop(); // например, помощник закрыли

        Assert.Equal(1, lost);
        Assert.False(h.Monitor.IsActive);
        Assert.True(h.Source.Disposed);
        Assert.True(h.Monitor.IsTracking(Discord)); // журнал включён: запись продолжится при следующем запуске

        await h.Monitor.StartAsync();
        Assert.Equal(2, h.Sources.Count);
    }

    [Fact]
    public async Task Forget_DropsJournal_AndStopsWhenIdle()
    {
        var h = await Running();
        h.Query(100, "discord.com");

        h.Monitor.Forget(Discord);

        Assert.Null(h.Monitor.FindJournal(Discord));
        Assert.False(h.Monitor.HasTracked);
        Assert.False(h.Monitor.IsActive);
        Assert.True(h.Source.Disposed);
    }

    [Fact]
    public async Task Capacity_IsPassedToJournals()
    {
        var h = new Harness(capacity: 2);
        h.Owners[100] = (Discord, "Discord.exe");
        h.Monitor.SetTracking(Discord, "Discord", true);
        await h.Monitor.StartAsync();

        h.Query(100, "a.com");
        h.Query(100, "b.com");
        h.Query(100, "c.com");

        var journal = h.Monitor.Journal(Discord, "Discord (переименован)");
        Assert.Equal(2, journal.Capacity);
        Assert.Equal(new[] { "b.com", "c.com" }, journal.ReadSince(0).Entries.Select(e => e.Domain));
        Assert.Equal("Discord (переименован)", journal.AppName);
    }

    [Fact]
    public async Task Dispose_ClosesSource_AndPreventsStart()
    {
        var h = await Running();

        h.Monitor.Dispose();

        Assert.True(h.Source.Disposed);
        Assert.False(h.Monitor.IsActive);
        await h.Monitor.StartAsync();
        Assert.Single(h.Sources);
    }
}

/// <summary>Обмен с помощником и раскладка структур ETW (ошибка в размере — падение лаунчера, поэтому проверяется заранее).</summary>
public class DnsPlumbingTests
{
    [Fact]
    public void PipeProtocol_RoundTrip()
    {
        var events = new[]
        {
            new DnsQueryEvent(new DateTime(2026, 10, 12, 9, 30, 15, 123, DateTimeKind.Utc).AddTicks(4567), 1234, "discord.com", 1),
            new DnsQueryEvent(DateTime.UtcNow, int.MaxValue, "яндекс.рф", 65),
            new DnsQueryEvent(DateTime.UtcNow, 1, "", 0)
        };

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var e in events)
                DnsPipeProtocol.WriteQuery(writer, e);
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        foreach (var expected in events)
        {
            Assert.Equal(DnsPipeProtocol.Query, reader.ReadByte());
            var actual = DnsPipeProtocol.ReadQuery(reader);
            Assert.Equal(expected, actual);
            Assert.Equal(DateTimeKind.Utc, actual.TimeUtc.Kind);
        }

        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void EtwStructs_MatchWindowsLayout()
    {
        if (IntPtr.Size != 8)
            return; // размеры ниже — для 64-битного Windows; лаунчер собирается под x64

        Assert.Equal(48, Marshal.SizeOf<EtwNative.WnodeHeader>());
        Assert.Equal(120, Marshal.SizeOf<EtwNative.EventTraceProperties>());
        Assert.Equal(116, (int)Marshal.OffsetOf<EtwNative.EventTraceProperties>(nameof(EtwNative.EventTraceProperties.LoggerNameOffset)));
        Assert.Equal(48, Marshal.SizeOf<EtwNative.EventTraceHeader>());
        Assert.Equal(88, Marshal.SizeOf<EtwNative.EventTrace>());
        Assert.Equal(172, Marshal.SizeOf<EtwNative.TimeZoneBlob>());
        Assert.Equal(280, Marshal.SizeOf<EtwNative.TraceLogfileHeader>());
        Assert.Equal(448, Marshal.SizeOf<EtwNative.EventTraceLogfile>());
        Assert.Equal(424, (int)Marshal.OffsetOf<EtwNative.EventTraceLogfile>(nameof(EtwNative.EventTraceLogfile.EventRecordCallback)));
        Assert.Equal(440, (int)Marshal.OffsetOf<EtwNative.EventTraceLogfile>(nameof(EtwNative.EventTraceLogfile.Context)));
        Assert.Equal(80, Marshal.SizeOf<EtwNative.EventHeader>());
        Assert.Equal(112, Marshal.SizeOf<EtwNative.EventRecord>());
        Assert.Equal(96, (int)Marshal.OffsetOf<EtwNative.EventRecord>(nameof(EtwNative.EventRecord.UserData)));
        Assert.Equal(16, Marshal.SizeOf<EtwNative.EventFilterDescriptor>());
        Assert.Equal(48, Marshal.SizeOf<EtwNative.EnableTraceParameters>());
        Assert.Equal(16, Marshal.SizeOf<EtwNative.PropertyDataDescriptor>());
    }
}
