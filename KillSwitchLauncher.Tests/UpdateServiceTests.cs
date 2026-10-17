using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KillSwitchLauncher.Services;
using SafeLauncher.Updates;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Обновления: разбор релизов GitHub, выбор версии, загрузка с проверкой, итог прошлого обновления.</summary>
public class UpdateServiceTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static string Release(string tag, string assets = "[]", bool draft = false, bool prerelease = false, string? name = null) => $$"""
        {
          "tag_name": "{{tag}}",
          "name": {{(name is null ? "null" : JsonSerializer.Serialize(name))}},
          "body": "Что нового в {{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "published_at": "2026-10-01T10:00:00Z",
          "assets": {{assets}}
        }
        """;

    private static string SetupAsset(string version) =>
        $$"""[{ "name": "SafeLauncher-Setup-{{version}}.exe", "browser_download_url": "https://example.com/{{version}}.exe", "size": 100 }]""";

    // ---------------------------------------------------------------- версия

    [Fact]
    public void CurrentVersion_HasThreeParts_AndMatchesTheLauncherAssembly()
    {
        var assembly = typeof(UpdateService).Assembly.GetName().Version!;

        Assert.Equal(-1, UpdateService.CurrentVersion.Revision);
        Assert.Equal(assembly.ToString(3), UpdateService.Format(UpdateService.CurrentVersion));
        Assert.True(UpdateService.CurrentVersion >= new Version(1, 0, 0));
    }

    [Theory]
    [InlineData(1, 5, -1, "1.5.0")]
    [InlineData(2, 0, 3, "2.0.3")]
    public void Normalize_FillsMissingParts(int major, int minor, int build, string expected)
    {
        var version = build < 0 ? new Version(major, minor) : new Version(major, minor, build);
        Assert.Equal(expected, UpdateService.Format(UpdateService.Normalize(version)));
    }

    // ---------------------------------------------------------------- разбор релиза

    [Fact]
    public void ParseRelease_ReadsAllFields_AndPrefersSetupExe()
    {
        var release = UpdateService.ParseRelease(Json(Release("v1.5.0", name: "Безопасный лаунчер 1.5", assets: """
            [
              { "name": "readme.txt", "browser_download_url": "https://example.com/readme.txt", "size": 5 },
              { "name": "SafeLauncher-portable.exe", "browser_download_url": "https://example.com/portable.exe", "size": 10 },
              { "name": "SafeLauncher-Setup-1.5.0.exe", "browser_download_url": "https://example.com/setup.exe", "size": 12345,
                "digest": "sha256:ABCDEF0123" }
            ]
            """)));

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 5, 0), release!.Version);
        Assert.Equal("v1.5.0", release.Tag);
        Assert.Equal("Безопасный лаунчер 1.5", release.Title);
        Assert.Equal("Что нового в v1.5.0", release.Notes);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.Equal("https://example.com/setup.exe", release.SetupUrl);
        Assert.Equal("SafeLauncher-Setup-1.5.0.exe", release.SetupName);
        Assert.Equal(12345, release.SetupSize);
        Assert.Equal("ABCDEF0123", release.Sha256);
    }

    [Theory]
    [InlineData("v1.5.0", "1.5.0")]
    [InlineData("1.5", "1.5.0")]
    [InlineData("V2.0.1.7", "2.0.1")]
    [InlineData("v1.6.0-beta", "1.6.0")]
    public void ParseRelease_TakesVersionFromTag(string tag, string expected)
    {
        var release = UpdateService.ParseRelease(Json(Release(tag)));
        Assert.Equal(expected, UpdateService.Format(release!.Version));
    }

    [Theory]
    [InlineData("release")]
    [InlineData("")]
    [InlineData("latest-1.5")]
    public void ParseRelease_WithoutVersionInTag_IsSkipped(string tag) =>
        Assert.Null(UpdateService.ParseRelease(Json(Release(tag))));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void ParseRelease_WithoutTitle_UsesTag(string? name) =>
        Assert.Equal("v1.5.0", UpdateService.ParseRelease(Json(Release("v1.5.0", name: name)))!.Title);

    [Fact]
    public void ParseRelease_WithoutExe_HasNoSetup()
    {
        var release = UpdateService.ParseRelease(Json(Release("v1.5.0",
            """[{ "name": "SafeLauncher.zip", "browser_download_url": "https://example.com/a.zip", "size": 1 }]""")))!;

        Assert.Null(release.SetupUrl);
        Assert.Null(release.Sha256);
    }

    [Fact]
    public void ParseRelease_IgnoresDigestThatIsNotSha256()
    {
        var release = UpdateService.ParseRelease(Json(Release("v1.5.0",
            """[{ "name": "Setup.exe", "browser_download_url": "https://example.com/s.exe", "size": 1, "digest": "md5:abc" }]""")))!;

        Assert.Equal("https://example.com/s.exe", release.SetupUrl);
        Assert.Null(release.Sha256);
    }

    [Fact]
    public void ParseRelease_FindsSignatureOfTheSetup_AndReleasePage()
    {
        var json = Release("v1.5.0", """
            [
              { "name": "SafeLauncher-Setup-1.5.0.exe", "browser_download_url": "https://example.com/setup.exe", "size": 10 },
              { "name": "other.exe.sig", "browser_download_url": "https://example.com/other.sig", "size": 1 },
              { "name": "SafeLauncher-Setup-1.5.0.exe.sig", "browser_download_url": "https://example.com/setup.sig", "size": 1 }
            ]
            """).Replace("\"assets\":", "\"html_url\": \"https://github.com/psibia/safe-launcher/releases/tag/v1.5.0\", \"assets\":");

        var release = UpdateService.ParseRelease(Json(json))!;

        Assert.Equal("https://example.com/setup.exe", release.SetupUrl);
        Assert.Equal("https://example.com/setup.sig", release.SignatureUrl);
        Assert.Equal("https://github.com/psibia/safe-launcher/releases/tag/v1.5.0", release.PageUrl);
    }

    [Fact]
    public void ParseRelease_WithoutSignature_IsUnsigned() =>
        Assert.Null(UpdateService.ParseRelease(Json(Release("v1.5.0", SetupAsset("1.5.0"))))!.SignatureUrl);

    // ---------------------------------------------------------------- выбор версии

    private static JsonElement Releases(params string[] items) => Json("[" + string.Join(",", items) + "]");

    [Fact]
    public void BuildResult_SkipsDraftsAndPrereleases_AndFindsLatestAndInstalled()
    {
        var json = Releases(
            Release("v9.0.0", SetupAsset("9.0.0"), draft: true),
            Release("v8.0.0", SetupAsset("8.0.0"), prerelease: true),
            Release("v1.4.0", SetupAsset("1.4.0")),
            Release("v1.6.0", SetupAsset("1.6.0")),
            Release("v1.5.0", SetupAsset("1.5.0")));

        var result = UpdateService.BuildResult(json, new Version(1, 5));

        Assert.Equal(new Version(1, 5, 0), result.Current);
        Assert.Equal(new Version(1, 6, 0), result.Latest!.Version);
        Assert.Equal(new Version(1, 5, 0), result.CurrentRelease!.Version);
        Assert.True(result.IsUpdateAvailable);
    }

    [Fact]
    public void BuildResult_SameOrNewerInstalled_NoUpdate()
    {
        var json = Releases(Release("v1.5.0", SetupAsset("1.5.0")), Release("v1.4.0", SetupAsset("1.4.0")));

        Assert.False(UpdateService.BuildResult(json, new Version(1, 5, 0)).IsUpdateAvailable);

        var newer = UpdateService.BuildResult(json, new Version(1, 7, 0));
        Assert.False(newer.IsUpdateAvailable);
        Assert.Null(newer.CurrentRelease);
    }

    [Fact]
    public void BuildResult_NewerReleaseWithoutSetup_IsNotOffered()
    {
        var json = Releases(Release("v1.6.0"), Release("v1.5.0", SetupAsset("1.5.0")));

        var result = UpdateService.BuildResult(json, new Version(1, 5, 0));

        Assert.Equal(new Version(1, 6, 0), result.Latest!.Version);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public void BuildResult_NoReleases()
    {
        var result = UpdateService.BuildResult(Json("[]"), new Version(1, 5, 0));

        Assert.Null(result.Latest);
        Assert.False(result.IsUpdateAvailable);
    }

    // ---------------------------------------------------------------- итог прошлого обновления

    private static string WritePending(TempDir dir, string target)
    {
        var path = dir.File("update-pending.json");
        File.WriteAllText(path, $$"""{"TargetVersion":"{{target}}","FromVersion":"1.0.0","StartedAt":"2026-10-01T10:00:00+03:00"}""");
        return path;
    }

    [Fact]
    public void TakePendingOutcome_NothingPending_IsNull()
    {
        using var dir = new TempDir();
        Assert.Null(new UpdateService(dir.File("update-pending.json"), dir.File("download")).TakePendingOutcome());
    }

    [Fact]
    public void TakePendingOutcome_InstalledVersionReachedTarget_IsSuccess_AndCleansUp()
    {
        using var dir = new TempDir();
        var pending = WritePending(dir, "1.0.0");
        var download = dir.File("download");
        Directory.CreateDirectory(download);
        File.WriteAllText(Path.Combine(download, "SafeLauncher-Setup-1.0.0.exe"), "x");
        var service = new UpdateService(pending, download);

        var outcome = service.TakePendingOutcome();

        Assert.Equal(new UpdateOutcome(new Version(1, 0, 0), true), outcome);
        Assert.False(File.Exists(pending));
        Assert.False(Directory.Exists(download));

        // Сообщается один раз.
        Assert.Null(service.TakePendingOutcome());
    }

    [Fact]
    public void TakePendingOutcome_TargetNotReached_IsFailure()
    {
        using var dir = new TempDir();
        var pending = WritePending(dir, "99.0");

        var outcome = new UpdateService(pending, dir.File("download")).TakePendingOutcome();

        Assert.Equal(new UpdateOutcome(new Version(99, 0, 0), false), outcome);
        Assert.False(File.Exists(pending));
    }

    private static string WriteServicePending(TempDir dir, string target)
    {
        var path = dir.File("update-pending.json");
        File.WriteAllText(path, $$"""{"TargetVersion":"{{target}}","FromVersion":"1.0.0","StartedAt":"2026-10-01T10:00:00+03:00","Mode":"service"}""");
        return path;
    }

    [Fact]
    public void TakePendingOutcome_ServiceRolledBack_ReportsReasonFromItsStatus()
    {
        using var dir = new TempDir();
        var pending = WriteServicePending(dir, "99.0.0");
        var status = dir.File("status.json");
        new UpdateState
        {
            Version = "99.0.0", FromVersion = "1.0.0", Stage = UpdateStages.Done, Done = true, RolledBack = true,
            Error = "Установщик завершился с кодом 2", Updated = DateTime.UtcNow
        }.Write(status);

        var outcome = new UpdateService(pending, dir.File("download"), status).TakePendingOutcome();

        Assert.Equal(new UpdateOutcome(new Version(99, 0, 0), false, "Установщик завершился с кодом 2", RolledBack: true), outcome);
    }

    [Fact]
    public void TakePendingOutcome_ServiceHelperStoppedMidway_SaysSo()
    {
        using var dir = new TempDir();
        var pending = WriteServicePending(dir, "99.0.0");
        var status = dir.File("status.json");
        new UpdateState { Version = "99.0.0", Stage = UpdateStages.Installing, Updated = DateTime.UtcNow.AddMinutes(-5) }.Write(status);

        var outcome = new UpdateService(pending, dir.File("download"), status).TakePendingOutcome()!;

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.RolledBack);
        Assert.Contains("прервалась", outcome.Error);
    }

    [Fact]
    public void TakePendingOutcome_StatusOfAnotherUpdate_IsNotUsed()
    {
        using var dir = new TempDir();
        var pending = WriteServicePending(dir, "99.0.0");
        var status = dir.File("status.json");
        new UpdateState { Version = "98.0.0", Done = true, Error = "старая ошибка", Updated = DateTime.UtcNow }.Write(status);

        Assert.Equal(new UpdateOutcome(new Version(99, 0, 0), false), new UpdateService(pending, dir.File("download"), status).TakePendingOutcome());
    }

    [Theory]
    [InlineData("{ повреждён")]
    [InlineData("""{"TargetVersion":"не версия"}""")]
    public void TakePendingOutcome_DamagedFile_IsIgnoredAndRemoved(string content)
    {
        using var dir = new TempDir();
        var pending = dir.File("update-pending.json");
        File.WriteAllText(pending, content);

        Assert.Null(new UpdateService(pending, dir.File("download")).TakePendingOutcome());
        Assert.False(File.Exists(pending));
    }

    [Fact]
    public void StartSetup_WhenSetupCannotStart_LeavesNoPendingMark()
    {
        using var dir = new TempDir();
        var pending = dir.File("update-pending.json");
        var service = new UpdateService(pending, dir.File("download"));
        var release = new ReleaseInfo(new Version(9, 9, 9), "v9.9.9", "v9.9.9", "", null, "https://example.com/s.exe", "s.exe", 1, null);

        Assert.ThrowsAny<Exception>(() => service.StartSetup(dir.File("нет-такого-установщика.exe"), release));
        Assert.False(File.Exists(pending));
    }

    // ---------------------------------------------------------------- сообщение о запуске новой версии

    private static readonly string Current = UpdateService.Format(UpdateService.CurrentVersion);

    private static string WriteStatus(TempDir dir, string stage, string? version = null, bool done = false, string jobId = "abc123")
    {
        var path = dir.File("status.json");
        new UpdateState { JobId = jobId, Version = version ?? Current, Stage = stage, Done = done, Updated = DateTime.UtcNow }.Write(path);
        return path;
    }

    [Fact]
    public void PendingStartConfirmation_HelperWaitsForThisVersion_GivesTheJob()
    {
        using var dir = new TempDir();
        var status = WriteStatus(dir, UpdateStages.Starting);

        Assert.Equal("abc123", new UpdateService(dir.File("pending.json"), dir.File("download"), status).PendingStartConfirmation());
    }

    [Theory]
    [InlineData(UpdateStages.Installing, null, false, "abc123")]
    [InlineData(UpdateStages.Starting, "99.0.0", false, "abc123")]
    [InlineData(UpdateStages.Starting, null, true, "abc123")]
    [InlineData(UpdateStages.Starting, null, false, "")]
    public void PendingStartConfirmation_NothingToReport_IsNull(string stage, string? version, bool done, string jobId)
    {
        using var dir = new TempDir();
        var status = WriteStatus(dir, stage, version, done, jobId);

        Assert.Null(new UpdateService(dir.File("pending.json"), dir.File("download"), status).PendingStartConfirmation());
    }

    [Fact]
    public void PendingStartConfirmation_NoStatus_IsNull()
    {
        using var dir = new TempDir();

        Assert.Null(new UpdateService(dir.File("pending.json"), dir.File("download"), dir.File("status.json")).PendingStartConfirmation());
    }

    // ---------------------------------------------------------------- окно после обновления

    private static WindowResume Window(string? section = "Updates") => new()
    {
        Left = -1500, Top = 120, Width = 980, Height = 640, Maximized = true, SettingsSection = section
    };

    [Fact]
    public void WindowResume_SavedBeforeUpdate_IsTakenOnce()
    {
        using var dir = new TempDir();
        var service = new UpdateService(dir.File("pending.json"), dir.File("download"));

        service.SaveWindowResume(Window());
        var resume = new UpdateService(dir.File("pending.json"), dir.File("download")).TakeWindowResume()!;

        Assert.Equal((-1500d, 120d, 980d, 640d, true, "Updates"),
            (resume.Left, resume.Top, resume.Width, resume.Height, resume.Maximized, resume.SettingsSection));
        Assert.Null(service.TakeWindowResume());
    }

    [Fact]
    public void WindowResume_NothingSaved_IsNull()
    {
        using var dir = new TempDir();

        Assert.Null(new UpdateService(dir.File("pending.json"), dir.File("download")).TakeWindowResume());
    }

    [Fact]
    public void WindowResume_SavedLongAgo_IsNotRestored_AndRemoved()
    {
        using var dir = new TempDir();
        var path = dir.File("update-window.json");
        var old = Window();
        old.SavedAt = DateTimeOffset.Now.AddHours(-2);
        File.WriteAllText(path, JsonSerializer.Serialize(old));

        Assert.Null(new UpdateService(dir.File("pending.json"), dir.File("download")).TakeWindowResume());
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("{ повреждён")]
    [InlineData("null")]
    [InlineData("""{"Left":0,"Top":0,"Width":0,"Height":600}""")]
    public void WindowResume_Damaged_IsIgnoredAndRemoved(string content)
    {
        using var dir = new TempDir();
        var path = dir.File("update-window.json");
        File.WriteAllText(path, content.Replace("}", $",\"SavedAt\":\"{DateTimeOffset.Now:O}\"}}"));

        Assert.Null(new UpdateService(dir.File("pending.json"), dir.File("download")).TakeWindowResume());
        Assert.False(File.Exists(path));
    }

    // ---------------------------------------------------------------- загрузка

    private static ReleaseInfo Downloadable(string url, byte[] body, string? name = "SafeLauncher-Setup-1.6.0.exe", string? sha = "auto", long? size = null) =>
        new(new Version(1, 6, 0), "v1.6.0", "v1.6.0", "", null, url, name, size ?? body.Length,
            sha == "auto" ? Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant() : sha);

    private static byte[] Payload(int size = 300_000)
    {
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task DownloadAsync_ChecksSizeAndHash_AndSavesFile()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload();
        var release = Downloadable(server.Map("/setup.exe", body), body);
        var progress = new SyncProgress<DownloadProgress>();

        var update = await new UpdateService(dir.File("pending.json"), dir.File("download")).DownloadAsync(release, progress, CancellationToken.None);
        var path = update.SetupPath;

        Assert.Equal(dir.File("download", "SafeLauncher-Setup-1.6.0.exe"), path);
        Assert.False(update.IsSigned);
        Assert.Equal(new Version(1, 6, 0), update.Version);
        Assert.Equal(body, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".part"));
        Assert.Equal(new DownloadProgress(body.Length, body.Length), progress.Items[^1]);
    }

    [Fact]
    public async Task DownloadAsync_WrongHash_Fails_AndDoesNotKeepTheFile()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload();
        var release = Downloadable(server.Map("/setup.exe", body), body, sha: new string('0', 64));
        var service = new UpdateService(dir.File("pending.json"), dir.File("download"));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None));
        Assert.False(File.Exists(dir.File("download", "SafeLauncher-Setup-1.6.0.exe")));
    }

    [Fact]
    public async Task DownloadAsync_IncompleteFile_Fails()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload();
        var release = Downloadable(server.Map("/setup.exe", body), body, sha: null, size: body.Length + 1);
        var service = new UpdateService(dir.File("pending.json"), dir.File("download"));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None));
    }

    [Fact]
    public async Task DownloadAsync_ServerError_Fails()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var release = Downloadable(server.Map("/setup.exe", Array.Empty<byte>(), status: 404), Array.Empty<byte>());
        var service = new UpdateService(dir.File("pending.json"), dir.File("download"));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None));
    }

    [Fact]
    public async Task DownloadAsync_WithoutSetup_Fails()
    {
        using var dir = new TempDir();
        var release = new ReleaseInfo(new Version(1, 6, 0), "v1.6.0", "v1.6.0", "", null, null, null, 0, null);
        var service = new UpdateService(dir.File("pending.json"), dir.File("download"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None));
    }

    [Theory]
    [InlineData("sub/evil.exe", "evil.exe")]
    [InlineData("notes.txt", "SafeLauncher-Setup-1.6.0.exe")]
    [InlineData(null, "SafeLauncher-Setup-1.6.0.exe")]
    public async Task DownloadAsync_FileNameFromGitHub_CannotLeaveTheDownloadFolder(string? name, string expected)
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload(1000);
        var release = Downloadable(server.Map("/setup.exe", body), body, name: name);

        var update = await new UpdateService(dir.File("pending.json"), dir.File("download")).DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal(dir.File("download", expected), update.SetupPath);
    }

    [Fact]
    public async Task DownloadAsync_Cancelled_Stops()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload();
        var release = Downloadable(server.Map("/setup.exe", body), body);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateService(dir.File("pending.json"), dir.File("download")).DownloadAsync(release, new SyncProgress<DownloadProgress>(), cts.Token));
    }

    // ---------------------------------------------------------------- докачка

    private const int AlreadyHave = 120_000;

    /// <summary>Недокачанный в прошлый раз установщик: первые <paramref name="count"/> байт.</summary>
    private static string WritePartial(TempDir dir, byte[] body, int count = AlreadyHave)
    {
        var folder = dir.File("download");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "SafeLauncher-Setup-1.6.0.exe.part");
        File.WriteAllBytes(path, body[..count]);
        return path;
    }

    [Fact]
    public async Task DownloadAsync_Interrupted_ContinuesFromTheSamePlace()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload();
        var partial = WritePartial(dir, body);
        var release = Downloadable(server.Map("/setup.exe", body, ranges: true), body);
        var progress = new SyncProgress<DownloadProgress>();

        var update = await new UpdateService(dir.File("pending.json"), dir.File("download")).DownloadAsync(release, progress, CancellationToken.None);

        Assert.Equal($"bytes={AlreadyHave}-", server.LastRange("/setup.exe"));
        Assert.Equal(body, File.ReadAllBytes(update.SetupPath));
        Assert.False(File.Exists(partial));
        Assert.Equal(new DownloadProgress(body.Length, body.Length, AlreadyHave), progress.Items[^1]);
    }

    [Fact]
    public async Task DownloadAsync_ServerCannotContinue_DownloadsTheWholeFile()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload();
        WritePartial(dir, body);
        var release = Downloadable(server.Map("/setup.exe", body), body);
        var progress = new SyncProgress<DownloadProgress>();

        var update = await new UpdateService(dir.File("pending.json"), dir.File("download")).DownloadAsync(release, progress, CancellationToken.None);

        Assert.Equal(body, File.ReadAllBytes(update.SetupPath));
        Assert.Equal(new DownloadProgress(body.Length, body.Length), progress.Items[^1]);
    }

    [Fact]
    public async Task DownloadAsync_DamagedStart_IsCaughtByTheChecksum_AndTheNextDownloadStartsOver()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload();
        var damaged = (byte[])body.Clone();
        damaged[10] ^= 0x01;
        var partial = WritePartial(dir, damaged);
        var release = Downloadable(server.Map("/setup.exe", body, ranges: true), body);
        var service = new UpdateService(dir.File("pending.json"), dir.File("download"));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None));
        Assert.False(File.Exists(partial));

        var update = await service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None);

        Assert.Null(server.LastRange("/setup.exe"));
        Assert.Equal(body, File.ReadAllBytes(update.SetupPath));
    }

    [Fact]
    public async Task DownloadAsync_ConnectionDropped_IsTransient_AndTheNextAttemptContinues()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload();
        var release = Downloadable(server.Map("/setup.exe", body, ranges: true, dropFirstAfter: 100_000), body);
        var service = new UpdateService(dir.File("pending.json"), dir.File("download"));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None));
        Assert.Equal(DownloadFailureKind.Transient, UpdateService.ClassifyDownloadFailure(failure));

        var update = await service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal(2, server.Hits("/setup.exe"));
        Assert.Equal(body, File.ReadAllBytes(update.SetupPath));
        Assert.False(File.Exists(update.SetupPath + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_Stalled_IsCutOff_KeepsWhatCame_AndTheNextAttemptContinues()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload();
        var release = Downloadable(server.Map("/setup.exe", body, ranges: true, stallFirstAfter: 100_000), body);
        var service = new UpdateService(dir.File("pending.json"), dir.File("download")) { StallTimeout = TimeSpan.FromSeconds(1) };
        var partial = dir.File("download", "SafeLauncher-Setup-1.6.0.exe.part");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stalled = await Assert.ThrowsAsync<DownloadStalledException>(() => service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadFailureKind.Transient, UpdateService.ClassifyDownloadFailure(stalled));
        var kept = new FileInfo(partial).Length;
        Assert.InRange(kept, 1, 100_000);

        var update = await service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal($"bytes={kept}-", server.LastRange("/setup.exe"));
        Assert.Equal(body, File.ReadAllBytes(update.SetupPath));
    }

    [Fact]
    public async Task DownloadAsync_CancelledWhileStalled_IsACancel_NotAStall()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload();
        var release = Downloadable(server.Map("/setup.exe", body, ranges: true, stallFirstAfter: 100_000), body);
        var service = new UpdateService(dir.File("pending.json"), dir.File("download")) { StallTimeout = TimeSpan.FromSeconds(20) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), cts.Token));
    }

    public static TheoryData<Exception, DownloadFailureKind> Failures => new()
    {
        { new HttpRequestException("нет связи"), DownloadFailureKind.Transient },
        { new HttpRequestException("404", null, HttpStatusCode.NotFound), DownloadFailureKind.ReleaseGone },
        { new HttpRequestException("410", null, HttpStatusCode.Gone), DownloadFailureKind.ReleaseGone },
        { new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable), DownloadFailureKind.Transient },
        { new HttpRequestException("429", null, HttpStatusCode.TooManyRequests), DownloadFailureKind.Transient },
        { new HttpRequestException("403", null, HttpStatusCode.Forbidden), DownloadFailureKind.Transient },
        { new HttpRequestException("400", null, HttpStatusCode.BadRequest), DownloadFailureKind.Fatal },
        { new IOException("обрыв"), DownloadFailureKind.Transient },
        { new IOException("диск", unchecked((int)0x80070070)), DownloadFailureKind.Fatal },
        { new DownloadStalledException(TimeSpan.FromSeconds(30), new OperationCanceledException()), DownloadFailureKind.Transient },
        { new UpdateRouteException("подключение «X» не запустилось", new Exception()), DownloadFailureKind.Transient },
        { new TaskCanceledException(), DownloadFailureKind.Transient },
        { new InvalidDataException("сумма"), DownloadFailureKind.Damaged },
        { new UpdateSignatureException("чужая подпись"), DownloadFailureKind.Fatal },
        { new InvalidOperationException("не выбрано подключение"), DownloadFailureKind.Fatal }
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void ClassifyDownloadFailure_SeparatesWhatToRetry(Exception failure, DownloadFailureKind expected) =>
        Assert.Equal(expected, UpdateService.ClassifyDownloadFailure(failure));

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 30)]
    [InlineData(4, 60)]
    [InlineData(100, 60)]
    public void RetryDelay_GrowsToAMinute(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), UpdateService.RetryDelay(failures));

    [Fact]
    public void DiscardDownloads_RemovesEverythingDownloaded()
    {
        using var dir = new TempDir();
        WritePartial(dir, Payload(1000), 500);
        File.WriteAllText(dir.File("download", "SafeLauncher-Setup-1.6.0.exe"), "x");

        new UpdateService(dir.File("pending.json"), dir.File("download")).DiscardDownloads();

        Assert.False(Directory.Exists(dir.File("download")));
    }

    [Fact]
    public async Task DownloadAsync_Cancelled_KeepsWhatWasDownloaded()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload();
        var partial = WritePartial(dir, body);
        var release = Downloadable(server.Map("/setup.exe", body, ranges: true), body);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateService(dir.File("pending.json"), dir.File("download")).DownloadAsync(release, new SyncProgress<DownloadProgress>(), cts.Token));

        Assert.Equal(AlreadyHave, new FileInfo(partial).Length);
    }

    [Fact]
    public async Task DownloadAsync_PartLongerThanTheFile_IsDiscarded()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        var body = Payload(AlreadyHave - 1);
        WritePartial(dir, Payload());
        var release = Downloadable(server.Map("/setup.exe", body, ranges: true), body);

        var update = await new UpdateService(dir.File("pending.json"), dir.File("download")).DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None);

        Assert.Null(server.LastRange("/setup.exe"));
        Assert.Equal(body, File.ReadAllBytes(update.SetupPath));
    }

    // ---------------------------------------------------------------- подписанные выпуски

    /// <summary>Пара ключей автора для тестов: закрытым подписываем, открытый «встроен в лаунчер».</summary>
    private sealed class Signer : IDisposable
    {
        private readonly ECDsa _private = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public ECDsa? Public()
        {
            var key = ECDsa.Create();
            key.ImportFromPem(_private.ExportSubjectPublicKeyInfoPem());
            return key;
        }

        /// <summary>Текст файла .sig для установщика с содержимым <paramref name="body"/>.</summary>
        public string Sign(TempDir dir, byte[] body, string version = "1.6.0")
        {
            var folder = dir.File("sign");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"SafeLauncher-Setup-{version}.exe");
            File.WriteAllBytes(path, body);
            return UpdatePackage.Sign(path, Version.Parse(version), _private);
        }

        public void Dispose() => _private.Dispose();
    }

    private static ReleaseInfo SignedRelease(LocalHttpServer server, byte[] served, string signature, string? sha = "auto") =>
        Downloadable(server.Map("/setup.exe", served), served, sha: sha) with
        {
            SignatureUrl = server.Map("/setup.exe.sig", Encoding.UTF8.GetBytes(signature))
        };

    [Fact]
    public async Task DownloadAsync_Signed_ChecksSignature_AndKeepsItNextToTheSetup()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        using var signer = new Signer();
        var body = Payload();
        var signature = signer.Sign(dir, body);
        var release = SignedRelease(server, body, signature);
        var verifying = 0;

        var update = await new UpdateService(dir.File("pending.json"), dir.File("download"), publicKey: signer.Public)
            .DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None, verifying: () => verifying++);

        Assert.True(update.IsSigned);
        Assert.Equal(new Version(1, 6, 0), update.Version);
        Assert.Equal(body, File.ReadAllBytes(update.SetupPath));
        Assert.Equal(dir.File("download", "SafeLauncher-Setup-1.6.0.exe.sig"), update.SignaturePath);
        Assert.Equal(signature, File.ReadAllText(update.SignaturePath!));
        Assert.Equal(1, verifying);
    }

    [Fact]
    public async Task DownloadAsync_SignatureOfAnotherKey_Fails_WithoutDownloadingTheSetup()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        using var author = new Signer();
        using var stranger = new Signer();
        var body = Payload();
        var release = SignedRelease(server, body, stranger.Sign(dir, body));
        var service = new UpdateService(dir.File("pending.json"), dir.File("download"), publicKey: author.Public);

        await Assert.ThrowsAsync<UpdateSignatureException>(() => service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None));
        Assert.Equal(0, server.Hits("/setup.exe"));
        Assert.Empty(Directory.EnumerateFiles(dir.File("download")));
    }

    [Fact]
    public async Task DownloadAsync_SetupDiffersFromTheSignedOne_Fails_AndRemovesFiles()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        using var signer = new Signer();
        var signed = Payload();
        var served = (byte[])signed.Clone();
        served[1000] ^= 0xFF; // тот же размер, другое содержимое; контрольной суммы GitHub нет
        var release = SignedRelease(server, served, signer.Sign(dir, signed), sha: null);
        var service = new UpdateService(dir.File("pending.json"), dir.File("download"), publicKey: signer.Public);

        await Assert.ThrowsAsync<UpdateSignatureException>(() => service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(dir.File("download")));
    }

    [Fact]
    public async Task DownloadAsync_SignatureOfAnotherVersion_Fails()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        using var signer = new Signer();
        var body = Payload();
        var release = SignedRelease(server, body, signer.Sign(dir, body, version: "1.5.0"));
        var service = new UpdateService(dir.File("pending.json"), dir.File("download"), publicKey: signer.Public);

        var error = await Assert.ThrowsAsync<UpdateSignatureException>(() => service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None));
        Assert.Contains("1.5.0", error.Message);
        Assert.Equal(0, server.Hits("/setup.exe"));
    }

    [Fact]
    public async Task DownloadAsync_NoKeyInLauncher_IgnoresSignature_AndChecksTheGitHubChecksum()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        using var signer = new Signer();
        var body = Payload();
        var release = SignedRelease(server, body, signer.Sign(dir, body));

        var update = await new UpdateService(dir.File("pending.json"), dir.File("download"), publicKey: () => null)
            .DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None);

        Assert.False(update.IsSigned);
        Assert.Equal(0, server.Hits("/setup.exe.sig"));
    }

    [Fact]
    public async Task FindDownloaded_FindsSignedDownloadAgain_AndRemovesItOnceDamaged()
    {
        using var dir = new TempDir();
        using var server = new LocalHttpServer();
        using var signer = new Signer();
        var body = Payload();
        var release = SignedRelease(server, body, signer.Sign(dir, body));
        var service = new UpdateService(dir.File("pending.json"), dir.File("download"), publicKey: signer.Public);
        var update = await service.DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None);

        // Лаунчер перезапущен: заново не качаем.
        Assert.Equal(update, new UpdateService(dir.File("pending.json"), dir.File("download"), publicKey: signer.Public).FindDownloaded(release));

        var bytes = File.ReadAllBytes(update.SetupPath);
        bytes[10] ^= 0x01;
        File.WriteAllBytes(update.SetupPath, bytes);

        Assert.Null(service.FindDownloaded(release));
        Assert.False(File.Exists(update.SetupPath));
        Assert.False(File.Exists(update.SignaturePath));
    }

    [Fact]
    public void FindDownloaded_NothingDownloaded_IsNull()
    {
        using var dir = new TempDir();
        var release = Downloadable("https://example.com/setup.exe", Payload(10));

        Assert.Null(new UpdateService(dir.File("pending.json"), dir.File("download")).FindDownloaded(release));
    }
}
