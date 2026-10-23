using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SafeLauncher.Logging;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Технический журнал (SafeLauncher.Logging): формат строки, маскировка, хранение, разбор строк ядра, падения.</summary>
public class LoggingTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 8, 41, 2, 123, DateTimeKind.Utc);

    private static LogLine Line(DateTime t, string text, LogLevel level = LogLevel.Info, string source = LogSources.Launcher) =>
        new(t, source, level, LogAreas.Network, LogCodes.CoreRestart, "L0001", text);

    // ---- формат строки ----

    [Fact]
    public void Format_And_Parse_RoundTrip()
    {
        var line = new LogLine(T0, LogSources.Service, LogLevel.Warn, LogAreas.Control, LogCodes.CmdStart, "L7f2a", "start: остановка ядра");
        var text = line.Format();

        Assert.Equal("2026-10-14T08:41:02.123Z\tservice\tWARN\tcontrol\tcmd.start\tL7f2a\tstart: остановка ядра", text);
        Assert.True(LogLine.TryParse(text, out var parsed));
        Assert.Equal(line, parsed);
        Assert.Equal(DateTimeKind.Utc, parsed.TimeUtc.Kind);
    }

    [Fact]
    public void Format_EscapesTabsAndNewLines_EmptyFieldsBecomeDash()
    {
        var line = new LogLine(T0, LogSources.Launcher, LogLevel.Error, LogAreas.Session, "", "", "строка1\r\nстрока2\tконец\n");
        var text = line.Format();

        Assert.Equal(7, text.Split('\t').Length);
        Assert.Contains("строка1 ⏎ строка2 конец", text);
        Assert.Contains("\t-\t-\t", text);
        Assert.DoesNotContain('\n', text);
    }

    [Fact]
    public void TryParse_RejectsBrokenLines()
    {
        Assert.False(LogLine.TryParse("", out _));
        Assert.False(LogLine.TryParse("мусор\tбез\tполей", out _));
        Assert.False(LogLine.TryParse("2026-10-14T08:41:02Z\tlauncher\tINFO\ta\tb\tc\tтекст", out _)); // без мс
        Assert.False(LogLine.TryParse("2026-10-14T08:41:02.123Z\tlauncher\tLOUD\ta\tb\tc\tтекст", out _));
    }

    [Fact]
    public void Codes_AndAreas_AreUnique()
    {
        Assert.Equal(LogCodes.All.Count, LogCodes.All.Distinct().Count());
        Assert.Equal(LogAreas.All.Count, LogAreas.All.Distinct().Count());
        Assert.All(LogCodes.All, c => Assert.Matches("^[a-z0-9]+(\\.[a-z0-9]+)?$", c));
        // каждая область входит в группу фильтра «Типы событий», кроме строк ядра (main): их показывает вкладка «Ядро»
        Assert.All(LogAreas.All.Where(a => a != LogAreas.Main), a => Assert.NotNull(LogAreaGroups.Of(a)));
        Assert.Null(LogAreaGroups.Of(LogAreas.Main));
    }

    // ---- маскировка ----

    private static LogMasker Masker(bool hide = true)
    {
        var m = new LogMasker();
        m.SetKey(new byte[32]);
        m.HideAddresses = hide;
        return m;
    }

    [Fact]
    public void Mask_ByDefault_KeepsAddresses_ButAlwaysHidesSecrets()
    {
        var m = Masker(hide: false);
        var text = m.Mask("dial 203.0.113.7:443 uuid=1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed \"private_key\": \"yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=\"");

        Assert.Contains("203.0.113.7:443", text);
        Assert.DoesNotContain("1b9d6bcd", text);
        Assert.DoesNotContain("yAnz5TF", text);
    }

    [Theory]
    [InlineData("vless://1b9d6bcd-bbfd-4b2d@host.example:443?security=reality#Работа", "[ссылка vless]")]
    [InlineData("ошибка загрузки https://sub.example.com/api/sub?token=abcdef123456", "https://sub.example.com/api/sub?[…]")]
    [InlineData("Authorization: Bearer abcdef0123456789", "Bearer ***")]
    [InlineData("password=hunter2hunter", "password=***")]
    [InlineData("{\"password\":\"p@ss w\"}", "\"password\":\"***")]
    public void Mask_HidesSecretForms(string input, string expected)
    {
        var text = Masker(hide: false).Mask(input);
        Assert.Contains(expected, text);
    }

    [Fact]
    public void Mask_KnownSecrets_AreReplaced()
    {
        var m = Masker(hide: false);
        m.SetSecrets(new[] { "https://my.sub/abcdefTOKEN" });
        Assert.Equal("загрузка [скрыто] не удалась", m.Mask("загрузка https://my.sub/abcdefTOKEN не удалась"));
    }

    [Theory]
    [InlineData(@"C:\Users\Павел Иванов\AppData\Local\Discord\app.exe", @"%USERPROFILE%\AppData\Local\Discord\app.exe")]
    [InlineData(@"c:\users\pavel\Desktop", @"%USERPROFILE%\Desktop")]
    [InlineData(@"C:\\Users\\pavel\\x.json", @"%USERPROFILE%\\x.json")]
    [InlineData(@"C:\Users\Public\Desktop", @"C:\Users\Public\Desktop")]
    public void Mask_HidesUserProfile(string input, string expected)
    {
        Assert.Equal(expected, Masker(hide: false).Mask(input));
    }

    [Fact]
    public void Mask_Hide_ReplacesIpv4_WithStableLabel_KeepsPort()
    {
        var m = Masker();
        var a = m.Mask("outbound connection to 203.0.113.7:443");
        var b = m.Mask("again 203.0.113.7");
        var c = m.Mask("other 203.0.113.8");

        Assert.Matches(@"outbound connection to \{ip4-pub-[0-9a-f]{6}\}:443", a);
        var label = a.Substring(a.IndexOf('{'), a.IndexOf('}') - a.IndexOf('{') + 1);
        Assert.Contains(label, b);
        Assert.DoesNotContain(label, c);
    }

    [Theory]
    [InlineData("192.168.1.10", "{ip4-lan-")]
    [InlineData("10.0.0.2", "{ip4-lan-")]
    [InlineData("100.64.1.1", "{ip4-lan-")]
    [InlineData("2001:db8::1", "{ip6-pub-")]
    [InlineData("fe80::1", "{ip6-lan-")]
    [InlineData("::ffff:203.0.113.7", "{ip4-pub-")]
    public void Mask_Hide_ClassifiesAddresses(string ip, string prefix)
    {
        Assert.StartsWith(prefix, Masker().Mask(ip));
    }

    [Theory]
    [InlineData("127.0.0.1:9090")]
    [InlineData("172.31.250.1")]
    [InlineData("198.18.0.5")]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8:53")]
    [InlineData("::1")]
    [InlineData("fdfe:dcba:9877::1")]
    [InlineData("fc00::5")]
    [InlineData("224.0.0.251")]
    [InlineData("время 08:41:02.123")]
    [InlineData("MAC aa:bb:cc:dd:ee:ff")]
    [InlineData("версия 1.14.3")]
    [InlineData("sing-box 1.14.3-safelauncher.6")]
    public void Mask_Hide_KeepsServiceAddresses_AndNonAddresses(string text)
    {
        Assert.Equal(text, Masker().Mask(text));
    }

    [Fact]
    public void Mask_Hide_Ipv6InBrackets_KeepsPort()
    {
        Assert.Matches(@"^\[\{ip6-pub-[0-9a-f]{6}\}\]:443$", Masker().Mask("[2001:db8::7]:443"));
    }

    [Fact]
    public void Mask_Hide_ServersBecomeConnectionLabel()
    {
        var m = Masker();
        m.SetServers(new Dictionary<string, string>
        {
            ["vpn.example.com"] = "c1a2b3c4d5e6f70809a0b1c2d3e4f5a6b",
            ["203.0.113.7"] = "c99887766554433221100aabbccddeeff"
        });

        Assert.Equal("dial {srv-1a2b3c4d}:443 ok", m.Mask("dial VPN.example.com:443 ok"));
        Assert.Equal("peer {srv-99887766}:51820", m.Mask("peer 203.0.113.7:51820"));
        // часть другого имени не трогается
        Assert.Equal("cdn.vpn.example.com.evil", m.Mask("cdn.vpn.example.com.evil"));
    }

    [Fact]
    public void Mask_SameKey_SameLabelAcrossInstances()
    {
        var a = Masker();
        var b = Masker();
        Assert.Equal(a.Mask("203.0.113.9"), b.Mask("203.0.113.9"));
        b.SetKey(new byte[] { 1, 2, 3 });
        Assert.NotEqual(a.Mask("203.0.113.9"), b.Mask("203.0.113.9"));
    }

    [Fact]
    public void MaskDomains_KeepsTld_FilesAndTypes()
    {
        var m = Masker(hide: false);
        var text = m.MaskDomains("connection to www.youtube.com:443 via launcher-core.exe at System.Net.Http.HttpClient, dns.google, https://ya.ru/x");

        Assert.Matches(@"connection to \{site-[0-9a-f]{6}\}\.com:443", text);
        Assert.Contains("launcher-core.exe", text);
        Assert.Contains("System.Net.Http.HttpClient", text);
        Assert.Contains("dns.google", text);
        Assert.Matches(@"https://\{site-[0-9a-f]{6}\}\.ru/x", text);
    }

    // ---- хранение ----

    private sealed class Clock
    {
        public DateTime Now = T0;
        public DateTime Get() => Now;
    }

    [Fact]
    public void Store_WritesDailyFiles_AndReadsPeriodSorted()
    {
        using var dir = new TempDir();
        var clock = new Clock();
        using var store = new LogStore(dir.Root, "launcher", LogSources.Launcher, 10_000_000, 1000, clock.Get, background: false);

        store.Append(Line(T0.AddSeconds(2), "второй"));
        store.Append(Line(T0, "первый"));
        store.Append(Line(T0.AddDays(1), "завтра"));

        Assert.True(File.Exists(Path.Combine(dir.Root, "launcher-20261014.log")));
        Assert.True(File.Exists(Path.Combine(dir.Root, "launcher-20261015.log")));

        var lines = store.Read(T0.AddMinutes(-1), T0.AddMinutes(1));
        Assert.Equal(new[] { "первый", "второй" }, lines.Select(l => l.Text));
        Assert.Equal(3, store.Read(T0.AddDays(-1), T0.AddDays(2)).Count);
    }

    [Fact]
    public void Store_RotatesBySize_WithinDay()
    {
        using var dir = new TempDir();
        using var store = new LogStore(dir.Root, "core", LogSources.Core, 30_000, 1000, null, background: false);

        for (var i = 0; i < 200; i++)
            store.Append(Line(T0.AddMilliseconds(i), "строка " + i + new string('x', 100)));

        var files = store.Files();
        Assert.True(files.Count > 1);
        Assert.Contains(files, f => Path.GetFileName(f) == "core-20261014-2.log");
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= store.MaxFileBytes + 400));
    }

    [Fact]
    public void Store_Purge_ByRetentionAndSize()
    {
        using var dir = new TempDir();
        var clock = new Clock();
        using var store = new LogStore(dir.Root, "launcher", LogSources.Launcher, 10_000_000, 1000, clock.Get, background: false);

        for (var d = 0; d < 10; d++)
            store.Append(Line(T0.AddDays(-d), "день " + d));
        Assert.Equal(10, store.Files().Count);

        store.Configure(LogStorage.Files, 3);
        // остаются сегодня и 3 прошлых дня (последний частично в сроке)
        Assert.Equal(4, store.Files().Count);

        store.Configure(LogStorage.Files, 1);
        Assert.Equal(2, store.Files().Count);
    }

    [Fact]
    public void Store_MemoryMode_DeletesFiles_AndKeepsRing()
    {
        using var dir = new TempDir();
        var clock = new Clock();
        using var store = new LogStore(dir.Root, "launcher", LogSources.Launcher, 10_000_000, 150, clock.Get, background: false);
        store.Append(Line(T0, "в файле"));
        Assert.Single(store.Files());

        store.Configure(LogStorage.Memory, 7);
        Assert.Empty(store.Files());

        for (var i = 0; i < 300; i++)
            store.Append(Line(T0.AddSeconds(i), "m" + i));
        var lines = store.Read(T0.AddDays(-1), T0.AddDays(1));
        Assert.Equal(150, lines.Count);
        Assert.Equal("m299", lines[^1].Text);
        Assert.Empty(store.Files());

        // обратно в файлы: память сбрасывается на диск
        store.Configure(LogStorage.Files, 7);
        Assert.Single(store.Files());
        Assert.Equal(150, store.Read(T0.AddDays(-1), T0.AddDays(1)).Count);
    }

    [Fact]
    public void Store_ReadChunk_PagesWithCursor()
    {
        using var dir = new TempDir();
        using var store = new LogStore(dir.Root, "service", LogSources.Service, 100_000_000, 1000, null, background: false);
        for (var i = 0; i < 3000; i++)
            store.Append(Line(T0.AddSeconds(i), "строка номер " + i));

        var all = new List<LogLine>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var chunk = store.ReadChunk(T0.AddSeconds(100), T0.AddSeconds(2899), cursor, 8192);
            all.AddRange(chunk.Lines);
            cursor = chunk.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.True(pages > 5);
        Assert.Equal(2800, all.Count);
        Assert.Equal("строка номер 100", all[0].Text);
        Assert.Equal("строка номер 2899", all[^1].Text);
    }

    [Fact]
    public void Store_Read_FindsStartInBigFile()
    {
        using var dir = new TempDir();
        using var store = new LogStore(dir.Root, "core", LogSources.Core, 300_000_000, 1000, null, background: false);
        for (var i = 0; i < 20_000; i++)
            store.Append(Line(T0.Date.AddSeconds(i * 4), "core line " + i + " " + new string('y', 40)));

        var from = T0.Date.AddSeconds(40_000);
        var lines = store.Read(from, from.AddSeconds(39));
        Assert.Equal(10, lines.Count);
        Assert.Equal("core line 10000 " + new string('y', 40), lines[0].Text);
    }

    [Fact]
    public void Store_Rewrite_TransformsFilesAndMemory()
    {
        using var dir = new TempDir();
        using var store = new LogStore(dir.Root, "launcher", LogSources.Launcher, 10_000_000, 1000, null, background: false);
        store.Append(Line(T0, "адрес 203.0.113.7"));

        var m = Masker();
        store.Rewrite(l => l with { Text = m.Mask(l.Text) });

        var text = File.ReadAllText(store.Files()[0]);
        Assert.DoesNotContain("203.0.113.7", text);
        Assert.Contains("{ip4-pub-", text);
    }

    [Fact]
    public void Store_Background_WritesWithinSeconds_AndCountsDrops()
    {
        using var dir = new TempDir();
        using var store = new LogStore(dir.Root, "launcher", LogSources.Launcher, 10_000_000, 1000);
        store.Append(Line(DateTime.UtcNow, "фон", LogLevel.Warn));
        Assert.True(Wait.Until(() => store.Files().Count == 1 && new FileInfo(store.Files()[0]).Length > 0, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, store.Dropped);
    }

    [Fact]
    public void Store_Clear_DeletesEverything()
    {
        using var dir = new TempDir();
        using var store = new LogStore(dir.Root, "launcher", LogSources.Launcher, 10_000_000, 1000, null, background: false);
        store.Append(Line(T0, "a"));
        store.Append(Line(T0.AddDays(1), "b"));
        Assert.True(store.UsageBytes() > 0);

        Assert.True(store.Clear() > 0);
        Assert.Empty(store.Files());
        Assert.Equal(0, store.UsageBytes());
    }

    // ---- строки ядра ----

    [Theory]
    [InlineData("INFO[0012] [3456 0ms] outbound/wireguard[c1]: outbound connection to x:443", LogLevel.Info, "[3456 0ms] outbound/wireguard[c1]: outbound connection to x:443")]
    [InlineData("WARN[0000] router: something", LogLevel.Warn, "router: something")]
    [InlineData("ERROR dns: exchange failed", LogLevel.Error, "dns: exchange failed")]
    [InlineData("+0300 2026-10-14 11:41:02 DEBUG router: match", LogLevel.Debug, "router: match")]
    [InlineData("\u001b[36mINFO\u001b[0m[0001] colored", LogLevel.Info, "colored")]
    [InlineData("FATAL[0000] start service: bad", LogLevel.Error, "start service: bad")]
    [InlineData("просто текст", LogLevel.Info, "просто текст")]
    public void CoreParser_ReadsLevelAndText(string raw, LogLevel level, string text)
    {
        var parsed = new CoreLogParser().Parse(raw);
        Assert.NotNull(parsed);
        Assert.Equal(level, parsed!.Value.Level);
        Assert.Equal(text, parsed.Value.Text);
    }

    [Fact]
    public void CoreParser_SkipsTrace_AndMarksPanicStack()
    {
        var p = new CoreLogParser();
        Assert.Null(p.Parse("TRACE[0001] noise"));
        Assert.Null(p.Parse("   "));
        Assert.Equal(LogLevel.Error, p.Parse("panic: runtime error: index out of range")!.Value.Level);
        Assert.Equal(LogLevel.Error, p.Parse("goroutine 1 [running]:")!.Value.Level);
        Assert.Equal(LogLevel.Error, p.Parse("\t/src/router.go:12 +0x1f")!.Value.Level);
        Assert.Equal(LogLevel.Info, p.Parse("INFO[0002] after")!.Value.Level);
    }

    // ---- падения ----

    [Fact]
    public void Crash_WritesLineAndFile_OnceOnly()
    {
        CrashReport.ResetForTests();
        using var dir = new TempDir();
        using var store = new LogStore(dir.Root, "launcher", LogSources.Launcher, 10_000_000, 1000, null, background: false);
        Exception ex;
        try
        {
            try
            {
                throw new InvalidOperationException("внутреннее");
            }
            catch (Exception inner)
            {
                throw new ApplicationException(@"снаружи C:\Users\pavel\x", inner);
            }
        }
        catch (Exception e)
        {
            ex = e;
        }

        var path = CrashReport.Write(store, Masker(hide: false), dir.Root, LogSources.Launcher, "AppDomain", ex, "2.1.0", T0, terminating: true);
        var second = CrashReport.Write(store, Masker(hide: false), dir.Root, LogSources.Launcher, "AppDomain", ex, "2.1.0", T0, terminating: true);
        CrashReport.ResetForTests();

        Assert.NotNull(path);
        Assert.Null(second);
        var file = File.ReadAllText(path!);
        Assert.Contains("ApplicationException", file);
        Assert.Contains("внутреннее", file);
        Assert.Contains("%USERPROFILE%", file);
        Assert.DoesNotContain("pavel", file);

        var line = Assert.Single(store.Read(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1)));
        Assert.Equal(LogCodes.Crash, line.Code);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Contains(" ⏎ ", line.Text);
        Assert.Contains("InvalidOperationException", line.Text);
    }

    // ---- конфиг ядра ----

    [Fact]
    public void Config_Sanitize_RemovesSecrets_KeepsStructure()
    {
        var json = """
        {"log":{"level":"info"},
         "experimental":{"clash_api":{"external_controller":"127.0.0.1:9090","secret":"s3cr3t"}},
         "endpoints":[{"type":"wireguard","tag":"c1a2b3c4d5e6f70809a0b1c2d3e4f5a6b","private_key":"yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=",
           "address":["10.7.0.2/32"],
           "peers":[{"address":"203.0.113.7","port":51820,"public_key":"abc","pre_shared_key":"def"}]}],
         "outbounds":[{"type":"vless","server":"vpn.example.com","server_port":443,"uuid":"1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed"}]}
        """;
        var m = Masker();
        m.SetServers(new Dictionary<string, string> { ["vpn.example.com"] = "cffeeddccbbaa99887766554433221100" });

        var plain = ConfigSanitizer.Sanitize(json, m, hideAddresses: false);
        Assert.DoesNotContain("s3cr3t", plain);
        Assert.DoesNotContain("yAnz5TF", plain);
        Assert.DoesNotContain("1b9d6bcd", plain);
        Assert.DoesNotContain("\"def\"", plain);
        Assert.Contains("203.0.113.7", plain);
        Assert.Contains("vpn.example.com", plain);
        Assert.Contains("51820", plain);
        Assert.NotNull(JsonNode.Parse(plain)!["endpoints"]![0]!["peers"]);

        var hidden = ConfigSanitizer.Sanitize(json, m, hideAddresses: true);
        Assert.DoesNotContain("203.0.113.7", hidden);
        Assert.Contains("{srv-ffeeddcc}", hidden);
        Assert.Contains("127.0.0.1:9090", hidden);
        Assert.Contains("{ip4-lan-", hidden);
    }

    [Fact]
    public void Settings_Normalize_SnapsRetention()
    {
        Assert.Equal(7, new LogSettings { RetentionDays = 8 }.Normalized().RetentionDays);
        Assert.Equal(30, new LogSettings { RetentionDays = 100 }.Normalized().RetentionDays);
        Assert.Equal(1, new LogSettings { RetentionDays = 0 }.Normalized().RetentionDays);
        Assert.False(new LogSettings().HideAddresses);
        Assert.Equal(LogStorage.Files, new LogSettings().Storage);
    }
}
