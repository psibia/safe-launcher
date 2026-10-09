using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Журнал событий (events.log): запись, хранение последних 500, очистка, повреждённый файл.</summary>
public class ActivityLogTests
{
    [Fact]
    public void Add_KeepsEntriesInOrder_AndWritesTabSeparatedLines()
    {
        using var dir = new TempDir();
        var log = new ActivityLog(dir.File("events.log"));

        log.Add(LogKind.VpnDown, "VPN отключён");
        log.Add(LogKind.Closed, "Kill-Switch закрыл: Discord");

        var entries = log.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal(LogKind.VpnDown, entries[0].Kind);
        Assert.Equal("Kill-Switch закрыл: Discord", entries[1].Text);

        var lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(2, lines.Length);
        var parts = lines[0].Split('\t');
        Assert.Equal(3, parts.Length);
        Assert.Equal("VpnDown", parts[1]);
        Assert.Equal("VPN отключён", parts[2]);
    }

    [Fact]
    public void Add_CreatesMissingFolder()
    {
        using var dir = new TempDir();
        var log = new ActivityLog(dir.File("нет", "такой", "папки", "events.log"));

        log.Add(LogKind.Info, "запись");

        Assert.True(File.Exists(log.FilePath));
    }

    [Fact]
    public void Reload_RestoresEntries_WithTimeToTheSecond()
    {
        using var dir = new TempDir();
        var path = dir.File("events.log");
        var log = new ActivityLog(path);
        log.Add(LogKind.Launch, "Запущено: Telegram");
        var original = log.Snapshot().Single();

        var restored = new ActivityLog(path).Snapshot().Single();

        Assert.Equal(original.Kind, restored.Kind);
        Assert.Equal(original.Text, restored.Text);
        Assert.Equal(original.Time.ToString("yyyy-MM-dd HH:mm:ss"), restored.Time.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    [Fact]
    public void KeepsOnlyLast500_InMemoryAndAfterReload()
    {
        using var dir = new TempDir();
        var path = dir.File("events.log");
        var log = new ActivityLog(path);

        for (var i = 0; i < 1234; i++)
            log.Add(LogKind.Info, $"#{i}");

        var entries = log.Snapshot();
        Assert.Equal(ActivityLog.Keep, entries.Count);
        Assert.Equal("#734", entries[0].Text);
        Assert.Equal("#1233", entries[^1].Text);

        // Файл не растёт бесконечно: переписывается, когда в нём набирается на сотню строк больше.
        Assert.True(File.ReadAllLines(path).Length <= ActivityLog.Keep + 100);

        var reloaded = new ActivityLog(path).Snapshot();
        Assert.Equal(ActivityLog.Keep, reloaded.Count);
        Assert.Equal("#734", reloaded[0].Text);
        Assert.Equal("#1233", reloaded[^1].Text);
    }

    [Fact]
    public void Add_ReplacesTabsAndLineBreaks_SoOneEntryIsOneLine()
    {
        using var dir = new TempDir();
        var path = dir.File("events.log");
        var log = new ActivityLog(path);

        log.Add(LogKind.Failed, "  ошибка\tв строке\r\nи дальше  ");

        Assert.Equal("ошибка в строке и дальше", log.Snapshot().Single().Text);
        Assert.Single(File.ReadAllLines(path));
        Assert.Equal("ошибка в строке и дальше", new ActivityLog(path).Snapshot().Single().Text);
    }

    [Fact]
    public void Load_SkipsDamagedLines()
    {
        using var dir = new TempDir();
        var path = dir.File("events.log");
        File.WriteAllLines(path, new[]
        {
            "мусор",
            "2026-10-01 10:00:00\tНеизвестныйТип\tтекст",
            "не дата\tInfo\tтекст",
            "2026-10-01 10:00:01\tVpnUp\tVPN подключён: WireGuard",
            ""
        });

        var entry = new ActivityLog(path).Snapshot().Single();

        Assert.Equal(LogKind.VpnUp, entry.Kind);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 1), entry.Time);
        Assert.Equal("VPN подключён: WireGuard", entry.Text);
    }

    [Fact]
    public void Clear_RemovesEverything_AndRaisesCleared()
    {
        using var dir = new TempDir();
        var path = dir.File("events.log");
        var log = new ActivityLog(path);
        log.Add(LogKind.Info, "один");
        log.Add(LogKind.Info, "два");
        var cleared = 0;
        log.Cleared += (_, _) => cleared++;

        log.Clear();

        Assert.Empty(log.Snapshot());
        Assert.Equal(1, cleared);
        Assert.Empty(new ActivityLog(path).Snapshot());

        // После очистки журнал продолжает писаться.
        log.Add(LogKind.Info, "три");
        Assert.Equal("три", new ActivityLog(path).Snapshot().Single().Text);
    }

    [Fact]
    public void Added_IsRaisedWithTheNewEntry()
    {
        using var dir = new TempDir();
        var log = new ActivityLog(dir.File("events.log"));
        var received = new List<ActivityEntry>();
        log.Added += (_, e) => received.Add(e);

        log.Add(LogKind.Blocked, "«Discord» запустилось без VPN и сразу закрыто");

        var entry = Assert.Single(received);
        Assert.Equal(LogKind.Blocked, entry.Kind);
        Assert.Same(log.Snapshot().Single(), entry);
    }

    [Fact]
    public void Add_FromManyThreads_LosesNothing()
    {
        using var dir = new TempDir();
        var path = dir.File("events.log");
        var log = new ActivityLog(path);

        Parallel.For(0, 400, i => log.Add(LogKind.Info, $"#{i}"));

        Assert.Equal(400, log.Snapshot().Count);
        Assert.Equal(400, log.Snapshot().Select(e => e.Text).Distinct().Count());
        Assert.Equal(400, new ActivityLog(path).Snapshot().Count);
    }
}
