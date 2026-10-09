using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KillSwitchLauncher.Services;

/// <summary>Релиз на GitHub.</summary>
/// <param name="Notes">Описание изменений (Markdown из описания релиза).</param>
/// <param name="SetupUrl">Ссылка на установщик (.exe) или null, если его к релизу не приложили.</param>
/// <param name="Sha256">Контрольная сумма установщика, если GitHub её сообщает.</param>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Title,
    string Notes,
    DateTimeOffset? PublishedAt,
    string? SetupUrl,
    string? SetupName,
    long SetupSize,
    string? Sha256);

public sealed record UpdateCheckResult(Version Current, ReleaseInfo? Latest, ReleaseInfo? CurrentRelease)
{
    /// <summary>На GitHub есть версия новее установленной, и к ней приложен установщик.</summary>
    public bool IsUpdateAvailable => Latest is { SetupUrl: not null } && Latest.Version > Current;
}

public readonly record struct DownloadProgress(long Received, long? Total);

/// <summary>Результат обновления, запущенного в прошлый раз (показывается после перезапуска).</summary>
public sealed record UpdateOutcome(Version Target, bool Succeeded);

/// <summary>
/// Обновления через релизы GitHub (psibia/safe-launcher).
///
/// Как это работает:
///   1. GET api.github.com/repos/…/releases — список релизов. Версия берётся из тега (v1.4.0 или 1.4.0),
///      черновики и пре-релизы пропускаются. Описание релиза — это «Что нового».
///   2. Установщик (.exe из файлов релиза) скачивается во временную папку, проверяются размер и SHA-256.
///   3. Установщик запускается в тихом режиме (/SILENT) в ту же папку, где стоит лаунчер, а лаунчер закрывается.
///      Установщик сам запускает новую версию (параметр /autoupdate=1, см. installer\KillSwitchLauncher.iss).
///   4. Перед запуском установщика лаунчер записывает, на какую версию обновляется. После перезапуска
///      он сравнивает её со своей версией и сообщает, получилось ли.
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "psibia/safe-launcher";

    private static readonly Regex VersionInTag = new(@"^[vV]?(\d+(?:\.\d+){1,3})", RegexOptions.CultureInvariant);

    /// <summary>Версия запущенного лаунчера (свойство Version в .csproj).</summary>
    public static Version CurrentVersion { get; } =
        Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

    // Объявлен после CurrentVersion: статические поля инициализируются по порядку, а клиенту нужна версия.
    private static readonly HttpClient Http = CreateClient();

    private readonly string _pendingFile;
    private readonly string _downloadDir;

    public UpdateService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillSwitchLauncher", "update-pending.json"),
               Path.Combine(Path.GetTempPath(), "SafeLauncherUpdate"))
    {
    }

    /// <summary>Для тестов: свои пути к отметке об обновлении и папке загрузки.</summary>
    internal UpdateService(string pendingFile, string downloadDir)
    {
        _pendingFile = pendingFile;
        _downloadDir = downloadDir;
    }

    public static string Format(Version version) => version.ToString(3);

    /// <summary>Запущено обновление, а новая версия ещё не стартовала (её стартует установщик).</summary>
    public static bool IsUpdatePending
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KillSwitchLauncher", "update-pending.json"));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // ---------------------------------------------------------------- проверка

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("Репозиторий с обновлениями не найден или закрыт.");
        if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
            throw new InvalidOperationException("GitHub временно ограничил число запросов. Попробуйте позже.");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        return BuildResult(json.RootElement, CurrentVersion);
    }

    /// <summary>Ответ GitHub (массив релизов) → результат проверки. Черновики и пре-релизы пропускаются.</summary>
    internal static UpdateCheckResult BuildResult(JsonElement releasesJson, Version current)
    {
        var releases = new List<ReleaseInfo>();
        foreach (var item in releasesJson.EnumerateArray())
        {
            if (GetBool(item, "draft") || GetBool(item, "prerelease"))
                continue;
            if (ParseRelease(item) is { } release)
                releases.Add(release);
        }

        current = Normalize(current);
        var latest = releases.OrderByDescending(r => r.Version).FirstOrDefault();
        var installed = releases.FirstOrDefault(r => r.Version == current);
        return new UpdateCheckResult(current, latest, installed);
    }

    internal static ReleaseInfo? ParseRelease(JsonElement item)
    {
        var tag = GetString(item, "tag_name") ?? string.Empty;
        var match = VersionInTag.Match(tag.Trim());
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
            return null;

        string? url = null, name = null, sha = null;
        long size = 0;
        if (item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            // Нужен установщик: .exe, лучше со словом Setup в имени (SafeLauncher-Setup-1.4.0.exe).
            var exes = assets.EnumerateArray()
                .Where(a => (GetString(a, "name") ?? string.Empty).EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => (GetString(a, "name") ?? string.Empty).Contains("setup", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exes.Count > 0)
            {
                var asset = exes[0];
                url = GetString(asset, "browser_download_url");
                name = GetString(asset, "name");
                size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;

                var digest = GetString(asset, "digest");
                if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..].Trim();
            }
        }

        DateTimeOffset? published = DateTimeOffset.TryParse(GetString(item, "published_at"), out var p) ? p : null;
        var title = GetString(item, "name");

        return new ReleaseInfo(
            Normalize(version),
            tag,
            string.IsNullOrWhiteSpace(title) ? tag : title.Trim(),
            GetString(item, "body") ?? string.Empty,
            published,
            url,
            name,
            size,
            sha);
    }

    // ---------------------------------------------------------------- загрузка

    /// <summary>Скачивает установщик и проверяет его. Возвращает путь к файлу.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        if (release.SetupUrl is null)
            throw new InvalidOperationException("К релизу не приложен установщик.");

        Directory.CreateDirectory(_downloadDir);
        var fileName = Path.GetFileName(release.SetupName ?? $"SafeLauncher-Setup-{Format(release.Version)}.exe");
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            fileName = $"SafeLauncher-Setup-{Format(release.Version)}.exe";

        var target = Path.Combine(_downloadDir, fileName);
        var partial = target + ".part";

        using (var response = await Http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? (release.SetupSize > 0 ? release.SetupSize : null);

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            long received = 0;
            var lastReport = Stopwatch.StartNew();
            progress.Report(new DownloadProgress(0, total));

            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha.AppendData(buffer, 0, read);
                received += read;

                if (lastReport.ElapsedMilliseconds >= 80)
                {
                    progress.Report(new DownloadProgress(received, total));
                    lastReport.Restart();
                }
            }

            progress.Report(new DownloadProgress(received, total ?? received));

            if (release.SetupSize > 0 && received != release.SetupSize)
                throw new InvalidDataException($"Файл скачался не полностью ({received} из {release.SetupSize} байт).");

            var actual = Convert.ToHexString(sha.GetHashAndReset());
            if (release.Sha256 is not null && !string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма установщика не совпала с указанной на GitHub. Файл повреждён или подменён.");
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    // ---------------------------------------------------------------- установка

    /// <summary>
    /// Запускает установщик в тихом режиме. После этого лаунчер должен закрыться: установщик заменит файлы
    /// и сам запустит новую версию. Бросает Win32Exception (1223 — пользователь отклонил запрос UAC).
    /// </summary>
    public void StartSetup(string setupPath, ReleaseInfo release)
    {
        var args = new List<string> { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/autoupdate=1" };

        // Ставим туда же, где стоит лаунчер, и для тех же пользователей. Если лаунчер запущен не из установленной
        // копии (например, из Visual Studio), установщик выберет папку сам.
        var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        if (File.Exists(Path.Combine(appDir, "unins000.exe")))
        {
            args.Add(IsUnderProgramFiles(appDir) ? "/ALLUSERS" : "/CURRENTUSER");
            args.Add($"/DIR=\"{appDir}\"");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_pendingFile)!);
        File.WriteAllText(_pendingFile, JsonSerializer.Serialize(new PendingUpdate
        {
            TargetVersion = Format(release.Version),
            FromVersion = Format(CurrentVersion),
            StartedAt = DateTimeOffset.Now
        }));

        try
        {
            Process.Start(new ProcessStartInfo(setupPath)
            {
                Arguments = string.Join(' ', args),
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(setupPath) ?? string.Empty
            });
        }
        catch
        {
            TryDelete(_pendingFile);
            throw;
        }
    }

    /// <summary>Если в прошлый раз запускалось обновление — его результат (один раз), иначе null.</summary>
    public UpdateOutcome? TakePendingOutcome()
    {
        try
        {
            if (!File.Exists(_pendingFile))
                return null;

            var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(_pendingFile));
            File.Delete(_pendingFile);

            if (pending is null || !Version.TryParse(pending.TargetVersion, out var target))
                return null;

            var succeeded = CurrentVersion >= Normalize(target);
            if (succeeded)
                TryDeleteDirectory(_downloadDir);

            return new UpdateOutcome(Normalize(target), succeeded);
        }
        catch (Exception)
        {
            TryDelete(_pendingFile);
            return null;
        }
    }

    // ---------------------------------------------------------------- вспомогательное

    private sealed class PendingUpdate
    {
        public string TargetVersion { get; set; } = string.Empty;
        public string FromVersion { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
    }

    private static HttpClient CreateClient()
    {
        // Таймаут задаётся отдельно для каждого запроса: проверка — 20 с, загрузка — пока пользователь не отменит.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SafeLauncher", Format(CurrentVersion)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    internal static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static bool IsUnderProgramFiles(string dir)
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetEnvironmentVariable("ProgramW6432") ?? string.Empty
                 })
        {
            if (root.Length > 0 && dir.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // не страшно
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // не страшно: это временная папка
        }
    }
}
