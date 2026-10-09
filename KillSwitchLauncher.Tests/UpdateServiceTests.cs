using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KillSwitchLauncher.Services;
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

        var path = await new UpdateService(dir.File("pending.json"), dir.File("download")).DownloadAsync(release, progress, CancellationToken.None);

        Assert.Equal(dir.File("download", "SafeLauncher-Setup-1.6.0.exe"), path);
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

        var path = await new UpdateService(dir.File("pending.json"), dir.File("download")).DownloadAsync(release, new SyncProgress<DownloadProgress>(), CancellationToken.None);

        Assert.Equal(dir.File("download", expected), path);
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
}
