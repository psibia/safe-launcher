using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SafeLauncher.Updates;

namespace KillSwitchLauncher.Services;

/// <summary>Релиз на GitHub.</summary>
/// <param name="Notes">Описание изменений (Markdown из описания релиза).</param>
/// <param name="SetupUrl">Ссылка на установщик (.exe) или null, если его к релизу не приложили.</param>
/// <param name="Sha256">Контрольная сумма установщика, если GitHub её сообщает.</param>
/// <param name="SignatureUrl">Подпись установщика (SafeLauncher-Setup-X.exe.sig); null — релиз без подписи.</param>
/// <param name="PageUrl">Страница релиза на GitHub.</param>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Title,
    string Notes,
    DateTimeOffset? PublishedAt,
    string? SetupUrl,
    string? SetupName,
    long SetupSize,
    string? Sha256,
    string? SignatureUrl = null,
    string? PageUrl = null);

public sealed record UpdateCheckResult(Version Current, ReleaseInfo? Latest, ReleaseInfo? CurrentRelease)
{
    /// <summary>На GitHub есть версия новее установленной, и к ней приложен установщик.</summary>
    public bool IsUpdateAvailable => Latest is { SetupUrl: not null } && Latest.Version > Current;
}

/// <summary>Сколько скачано. Total null — сервер не сообщил размер. Resumed — сколько было скачано раньше: загрузка продолжена с этого места.</summary>
public readonly record struct DownloadProgress(long Received, long? Total, long Resumed = 0);

/// <summary>Почему не удалась попытка загрузки и что делать дальше (<see cref="UpdateService.ClassifyDownloadFailure"/>).</summary>
public enum DownloadFailureKind
{
    /// <summary>Нет связи, обрыв, зависание, сбой на стороне GitHub, подключение лаунчера не запустилось: повторить с того же места.</summary>
    Transient,

    /// <summary>GitHub ответил 404 или 410: релиз удалён. Сверить со списком релизов.</summary>
    ReleaseGone,

    /// <summary>Скачанный файл не совпал с размером или контрольной суммой: он удалён, скачать заново.</summary>
    Damaged,

    /// <summary>Подпись не та, нет места на диске, другие ответы GitHub: повторять бесполезно.</summary>
    Fatal
}

/// <summary>GitHub перестал присылать данные, хотя соединение не закрыто: оно разорвано.</summary>
public sealed class DownloadStalledException : IOException
{
    public DownloadStalledException(TimeSpan timeout, Exception inner)
        : base($"GitHub не присылал данные {timeout.TotalSeconds:0} секунд", inner)
    {
    }
}

/// <summary>Не открылся маршрут загрузки через подключение лаунчера. Сообщение — для пользователя.</summary>
public sealed class UpdateRouteException : Exception
{
    public UpdateRouteException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Окно лаунчера перед закрытием ради обновления: место и размер (в единицах WPF), развёрнуто ли, какая страница открыта.
/// Новая версия (или прежняя, если обновление не встало) открывается так же.
/// </summary>
public sealed class WindowResume
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>Открытый раздел настроек (SettingsSection); null — открыты плитки приложений.</summary>
    public string? SettingsSection { get; set; }

    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>Скачанное и проверенное обновление.</summary>
/// <param name="SignaturePath">Подпись рядом с установщиком; null — релиз без подписи (ставится обычным установщиком).</param>
public sealed record DownloadedUpdate(Version Version, string SetupPath, string? SignaturePath)
{
    /// <summary>Установщик сверен с подписью автора: его может поставить служба, без запроса прав Windows.</summary>
    public bool IsSigned => SignaturePath is not null;
}

/// <summary>Результат обновления, запущенного в прошлый раз (показывается после перезапуска).</summary>
/// <param name="Error">Почему не получилось, одной строкой (обновление через службу).</param>
/// <param name="RolledBack">Новая версия не встала, и служба вернула прежнюю.</param>
public sealed record UpdateOutcome(Version Target, bool Succeeded, string? Error = null, bool RolledBack = false);

/// <summary>
/// Обновления через релизы GitHub (psibia/safe-launcher), docs/DETAILS.md, «Обновления».
///
///   1. GET api.github.com/repos/…/releases — список релизов. Версия берётся из тега (v1.4.0 или 1.4.0),
///      черновики и пре-релизы пропускаются. Описание релиза — это «Что нового».
///   2. Установщик и его подпись (SafeLauncher-Setup-X.exe и .exe.sig из файлов релиза) скачиваются в
///      %LOCALAPPDATA%\KillSwitchLauncher\updates и сверяются: подпись ключом автора, версия, размер, SHA-256.
///   3. Установка через службу (<see cref="CoreServiceClient.UpdateAsync"/>): служба ещё раз проверяет подпись, ставит
///      обновление без окон и запроса прав, а при неудаче возвращает прежнюю версию. Ход установки показывает окно
///      ожидания обновления (<see cref="StartWaiter"/>), оно же запускает новую версию.
///      Релиз без подписи, старая служба или лаунчер не из Program Files — по-старому (<see cref="StartSetup"/>):
///      установщик с /SILENT и запросом прав, новую версию он запускает сам.
///   4. Новая версия, запустившись, сообщает службе, что работает (<see cref="PendingStartConfirmation"/>, команда
///      updateStarted): без этого помощник службы вернёт прежнюю версию.
///   5. Перед закрытием лаунчер записывает, на какую версию обновляется. После перезапуска он сравнивает её со своей
///      версией и сообщает, получилось ли (<see cref="TakePendingOutcome"/>).
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "psibia/safe-launcher";

    /// <summary>Окно ожидания обновления: KillSwitchLauncher.exe --await-update ВЕРСИЯ "путь к лаунчеру".</summary>
    public const string WaiterArgument = "--await-update";

    private const string ModeService = "service";
    private const string ModeSetup = "setup";

    /// <summary>Сколько ждать данных от GitHub. Дольше — соединение считается зависшим и разрывается, загрузка продолжится со следующей попытки.</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex VersionInTag = new(@"^[vV]?(\d+(?:\.\d+){1,3})", RegexOptions.CultureInvariant);

    /// <summary>Версия запущенного лаунчера (свойство Version в .csproj).</summary>
    public static Version CurrentVersion { get; } =
        Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

    // Объявлен после CurrentVersion: статические поля инициализируются по порядку, а клиенту нужна версия.
    private static readonly HttpClient Http = CreateClient(null);

    private readonly string _pendingFile;
    private readonly string _downloadDir;
    private readonly string _statusFile;

    /// <summary>Открытый ключ подписи выпусков; null — ключ в лаунчер не встроен, подпись не проверяется.</summary>
    private readonly Func<ECDsa?> _publicKey;

    public UpdateService()
        : this(Path.Combine(DataDirectory, "update-pending.json"), Path.Combine(DataDirectory, "updates"), UpdateState.FilePath)
    {
    }

    /// <summary>Для тестов: свои пути к отметке об обновлении, папке загрузки и ходу установки через службу, свой ключ подписи.</summary>
    internal UpdateService(string pendingFile, string downloadDir, string? statusFile = null, Func<ECDsa?>? publicKey = null)
    {
        _pendingFile = pendingFile;
        _downloadDir = downloadDir;
        _statusFile = statusFile ?? Path.Combine(downloadDir, UpdateState.FileName);
        _publicKey = publicKey ?? UpdatePackage.CreatePublicKey;
    }

    private static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillSwitchLauncher");

    /// <summary>Сколько ждать данных от GitHub (для тестов короче).</summary>
    internal TimeSpan StallTimeout { get; init; } = DefaultStallTimeout;

    /// <summary>
    /// Что делать после неудачной попытки загрузки. Повторы ведёт вызывающий (<c>MainViewModel.DownloadUpdateAsync</c>):
    /// каждый раз заново открывает маршрут и продолжает с того же места.
    /// </summary>
    public static DownloadFailureKind ClassifyDownloadFailure(Exception ex) => ex switch
    {
        UpdateSignatureException => DownloadFailureKind.Fatal,
        InvalidDataException => DownloadFailureKind.Damaged,
        HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone } => DownloadFailureKind.ReleaseGone,
        HttpRequestException { StatusCode: null } => DownloadFailureKind.Transient,
        HttpRequestException { StatusCode: { } code } => (int)code >= 500 ||
                                                         code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden
            ? DownloadFailureKind.Transient
            : DownloadFailureKind.Fatal,
        IOException io when IsDiskFull(io) => DownloadFailureKind.Fatal,
        IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or UpdateRouteException => DownloadFailureKind.Transient,
        _ => DownloadFailureKind.Fatal
    };

    /// <summary>Пауза перед повтором после <paramref name="failures"/> неудач подряд: 5 с, 15 с, 30 с, дальше раз в минуту.</summary>
    public static TimeSpan RetryDelay(int failures) => TimeSpan.FromSeconds(failures switch
    {
        <= 1 => 5,
        2 => 15,
        3 => 30,
        _ => 60
    });

    /// <summary>Не хватает места на диске (ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL).</summary>
    public static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27;

    public static string Format(Version version) => version.ToString(3);

    /// <summary>Запущено обновление, а новая версия ещё не стартовала.</summary>
    public static bool IsUpdatePending
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(DataDirectory, "update-pending.json"));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Обновление может поставить служба: в лаунчер встроен ключ подписи выпусков, и лаунчер запущен из установленной
    /// копии в Program Files (ставит её установщик, рядом с ним unins000.exe). Иначе — обычный установщик с запросом прав.
    /// </summary>
    public static bool CanInstallViaService
    {
        get
        {
            var appDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var programFiles = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) + "\\";
            return UpdatePackage.HasPublicKey && OperatingSystem.IsWindows() &&
                   appDir.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(Path.Combine(appDir, "unins000.exe"));
        }
    }

    // ---------------------------------------------------------------- проверка

    /// <param name="via">Маршрут (прокси через подключение лаунчера); null — напрямую.</param>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken, HttpMessageHandler? via = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        using var client = via is null ? null : CreateClient(via);
        using var response = await (client ?? Http).GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", timeout.Token);
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

        string? url = null, name = null, sha = null, signature = null;
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

                // Подпись: файл с тем же именем и .sig в конце (её создаёт build-installer.cmd).
                var signatureName = name + UpdatePackage.SignatureExtension;
                signature = assets.EnumerateArray()
                    .Where(a => string.Equals(GetString(a, "name"), signatureName, StringComparison.OrdinalIgnoreCase))
                    .Select(a => GetString(a, "browser_download_url"))
                    .FirstOrDefault(u => u is not null);
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
            sha,
            signature,
            GetString(item, "html_url"));
    }

    // ---------------------------------------------------------------- загрузка

    /// <summary>Имя установщика в папке загрузки: из релиза, но только имя файла .exe.</summary>
    private static string SetupFileName(ReleaseInfo release)
    {
        var fileName = Path.GetFileName(release.SetupName ?? string.Empty);
        return string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? $"SafeLauncher-Setup-{Format(release.Version)}.exe"
            : fileName;
    }

    /// <summary>
    /// Скачивает установщик (и подпись, если она есть) и проверяет их: подпись ключом автора, версия, размер, SHA-256.
    /// Другие версии из папки загрузки удаляются: хранится только последняя.
    /// </summary>
    /// <param name="verifying">Вызывается, когда файл скачан и начинается его проверка по подписи.</param>
    /// <param name="via">Маршрут (прокси через подключение лаунчера); null — напрямую.</param>
    public async Task<DownloadedUpdate> DownloadAsync(ReleaseInfo release, IProgress<DownloadProgress> progress, CancellationToken cancellationToken,
        HttpMessageHandler? via = null, Action? verifying = null)
    {
        if (release.SetupUrl is null)
            throw new InvalidOperationException("К релизу не приложен установщик.");

        Directory.CreateDirectory(_downloadDir);
        var target = Path.Combine(_downloadDir, SetupFileName(release));
        var signaturePath = target + UpdatePackage.SignatureExtension;
        RemoveOtherDownloads(target);

        using var client = via is null ? null : CreateClient(via);
        var http = client ?? Http;

        // Сначала подпись (маленький файл): если она не сходится с ключом, большой файл качать незачем.
        string? signatureText = null;
        UpdateManifest? manifest = null;
        using var key = release.SignatureUrl is null ? null : _publicKey();
        if (release.SignatureUrl is not null && key is not null)
        {
            signatureText = await DownloadSignatureAsync(http, release.SignatureUrl, cancellationToken);
            manifest = UpdatePackage.VerifySignature(signatureText, key);
            if (manifest.Version != Normalize(release.Version))
                throw new UpdateSignatureException($"В подписи версия {Format(manifest.Version)}, а в релизе {Format(release.Version)}.");
        }

        // Недокачанный файл (.part) остаётся при обрыве связи, зависании и отмене: следующая попытка продолжит его с того же места.
        var partial = target + ".part";
        var expected = manifest?.Size ?? (release.SetupSize > 0 ? release.SetupSize : (long?)null);
        await DownloadPartAsync(http, release.SetupUrl, partial, expected, progress, cancellationToken);

        try
        {
            var received = new FileInfo(partial).Length;
            if (expected is { } size && received != size)
                throw new InvalidDataException($"Файл скачался не полностью ({received} из {size} байт). Он будет скачан заново.");
            if (release.Sha256 is not null &&
                !string.Equals(await Task.Run(() => UpdatePackage.FileSha256(partial), cancellationToken), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма установщика не совпала с указанной на GitHub. Файл повреждён или подменён, он будет скачан заново.");
            File.Move(partial, target, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }

        if (manifest is null)
            return new DownloadedUpdate(Normalize(release.Version), target, null);

        // Проверка уже сохранённого файла: именно его потом получит служба.
        verifying?.Invoke();
        try
        {
            await Task.Run(() => UpdatePackage.Verify(target, signatureText!, key!), cancellationToken);
            File.WriteAllText(signaturePath, signatureText!, new UTF8Encoding(false));
        }
        catch
        {
            TryDelete(target);
            TryDelete(signaturePath);
            throw;
        }

        return new DownloadedUpdate(manifest.Version, target, signaturePath);
    }

    /// <summary>
    /// Скачать установщик в <paramref name="partial"/>. Если там уже есть начало файла (прошлая загрузка прервалась), сервер
    /// просят прислать остальное (Range); не умеет — файл качается целиком заново.
    /// </summary>
    private async Task DownloadPartAsync(HttpClient http, string url, string partial, long? expected, IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (expected is { } size && have > size)
        {
            File.Delete(partial);
            have = 0;
        }

        if (have > 0 && have == expected)
        {
            progress.Report(new DownloadProgress(have, expected, have));
            return; // скачан целиком, но не проверен: проверка дальше
        }

        // Ответ и каждая порция данных должны прийти за StallTimeout, иначе соединение разрывается (DownloadStalledException).
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await StallGuard(() => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token), cancellationToken);
        if (have > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partial);
            throw new IOException("Сервер не смог продолжить загрузку, она начнётся заново.");
        }

        response.EnsureSuccessStatusCode();
        var resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == have;
        if (!resumed)
            have = 0; // сервер прислал файл целиком
        var total = resumed
            ? response.Content.Headers.ContentRange?.Length ?? expected
            : response.Content.Headers.ContentLength ?? expected;

        await using var input = await StallGuard(() => response.Content.ReadAsStreamAsync(stall.Token), cancellationToken);
        await using var output = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        var received = have;
        var lastReport = Stopwatch.StartNew();
        progress.Report(new DownloadProgress(received, total, have));

        while (true)
        {
            stall.CancelAfter(StallTimeout);
            var read = await StallGuard(() => input.ReadAsync(buffer, stall.Token).AsTask(), cancellationToken);
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (lastReport.ElapsedMilliseconds >= 80)
            {
                progress.Report(new DownloadProgress(received, total, have));
                lastReport.Restart();
            }
        }

        progress.Report(new DownloadProgress(received, total ?? received, have));
    }

    /// <summary>Отмена по таймеру зависания (а не пользователем) превращается в <see cref="DownloadStalledException"/>.</summary>
    private async Task<T> StallGuard<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && !cancellationToken.IsCancellationRequested &&
                                   ex is not DownloadStalledException && IsStallCancel(ex))
        {
            throw new DownloadStalledException(StallTimeout, ex);
        }
    }

    private static bool IsStallCancel(Exception ex) =>
        ex is OperationCanceledException || ex.InnerException is OperationCanceledException;

    private static async Task<string> DownloadSignatureAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 16 * 1024)
            throw new UpdateSignatureException("Файл подписи обновления слишком большой: это не подпись.");
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }

    /// <summary>
    /// Обновление до этого релиза уже скачано и проверено (в прошлый раз, до перезапуска лаунчера): не качать заново.
    /// null — файлов нет или они не прошли проверку (тогда они удаляются). Читает весь файл: вызывать не из UI-потока.
    /// </summary>
    public DownloadedUpdate? FindDownloaded(ReleaseInfo release)
    {
        var target = Path.Combine(_downloadDir, SetupFileName(release));
        var signaturePath = target + UpdatePackage.SignatureExtension;
        if (!File.Exists(target))
            return null;

        try
        {
            using var key = release.SignatureUrl is null ? null : _publicKey();
            if (key is not null)
            {
                if (!File.Exists(signaturePath))
                    throw new UpdateSignatureException("Нет подписи.");
                var manifest = UpdatePackage.Verify(target, File.ReadAllText(signaturePath, Encoding.UTF8), key);
                if (manifest.Version != Normalize(release.Version))
                    throw new UpdateSignatureException("Не та версия.");
                return new DownloadedUpdate(manifest.Version, target, signaturePath);
            }

            if (release.SetupSize > 0 && new FileInfo(target).Length != release.SetupSize ||
                release.Sha256 is not null && !string.Equals(UpdatePackage.FileSha256(target), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Файл не совпал с релизом.");
            return new DownloadedUpdate(Normalize(release.Version), target, null);
        }
        catch (Exception ex) when (ex is UpdateSignatureException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TryDelete(target);
            TryDelete(signaturePath);
            return null;
        }
    }

    /// <summary>Удалить всё скачанное: версии, которую качали, больше нет на GitHub.</summary>
    public void DiscardDownloads() => TryDeleteDirectory(_downloadDir);

    /// <summary>В папке загрузки остаётся только нужная версия (и её недокачанный файл).</summary>
    private void RemoveOtherDownloads(string keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_downloadDir))
            {
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(file, keep + UpdatePackage.SignatureExtension, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(file, keep + ".part", StringComparison.OrdinalIgnoreCase))
                    TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не страшно: удалится в следующий раз
        }
    }

    // ---------------------------------------------------------------- установка

    /// <summary>
    /// Обновление по-старому: запускает установщик в тихом режиме (с запросом прав Windows). После этого лаунчер должен
    /// закрыться: установщик заменит файлы и сам запустит новую версию. Бросает Win32Exception (1223 — пользователь
    /// отклонил запрос UAC).
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

        WritePending(release.Version, ModeSetup);
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

    /// <summary>
    /// Копия лаунчера для окна ожидания обновления (%TEMP%\SafeLauncherUpdate): файлы установленной копии установщик
    /// заменит, поэтому окно работает из своей копии. Делается до команды службе: не вышло — обновление ещё не начато.
    /// </summary>
    public static string PrepareWaiter()
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь лаунчера.");
        var dir = WaiterDirectory;
        Directory.CreateDirectory(dir);
        var waiter = Path.Combine(dir, Path.GetFileName(source));
        File.Copy(source, waiter, overwrite: true);
        return waiter;
    }

    private static string WaiterDirectory => Path.Combine(Path.GetTempPath(), "SafeLauncherUpdate");

    /// <summary>Удалить копию лаунчера для окна ожидания (после обновления, когда окно закрылось). Занята или нет её — не страшно.</summary>
    public static void RemoveWaiterCopy()
    {
        try
        {
            if (Directory.Exists(WaiterDirectory))
                Directory.Delete(WaiterDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // окно ожидания ещё открыто: удалится после следующего обновления
        }
    }

    /// <summary>
    /// Служба приняла обновление: запустить окно ожидания (оно покажет ход установки и запустит лаунчер) и записать,
    /// на какую версию обновляемся. После этого лаунчер должен закрыться: установка начнётся, когда он закроется.
    /// </summary>
    public void StartWaiter(string waiter, Version target)
    {
        WritePending(target, ModeService);
        try
        {
            var start = new ProcessStartInfo(waiter) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(waiter)! };
            start.ArgumentList.Add(WaiterArgument);
            start.ArgumentList.Add(Format(target));
            start.ArgumentList.Add(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "KillSwitchLauncher.exe"));
            Process.Start(start);
        }
        catch
        {
            TryDelete(_pendingFile);
            throw;
        }
    }

    private void WritePending(Version target, string mode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_pendingFile)!);
        File.WriteAllText(_pendingFile, JsonSerializer.Serialize(new PendingUpdate
        {
            TargetVersion = Format(target),
            FromVersion = Format(CurrentVersion),
            StartedAt = DateTimeOffset.Now,
            Mode = mode
        }));
    }

    /// <summary>
    /// Эта версия только что установлена через службу, и помощник службы ждёт, что она сообщит о запуске (шаг starting):
    /// номер обновления для команды updateStarted. null — сообщать нечего.
    /// </summary>
    public string? PendingStartConfirmation()
    {
        var state = UpdateState.Read(_statusFile);
        return state is { Done: false } && state.Stage == UpdateStages.Starting && state.Version == Format(CurrentVersion) && state.JobId.Length > 0
            ? state.JobId
            : null;
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

            target = Normalize(target);
            var succeeded = CurrentVersion >= target;
            if (succeeded)
            {
                TryDeleteDirectory(_downloadDir);
                return new UpdateOutcome(target, true);
            }

            // Через службу: почему не получилось, сообщил её помощник.
            if (pending.Mode == ModeService && UpdateState.Read(_statusFile) is { } state && state.Version == Format(target))
            {
                var error = state.Done
                    ? state.Error
                    : "Установка прервалась: помощник службы перестал отвечать.";
                return new UpdateOutcome(target, false, error, state.RolledBack);
            }

            return new UpdateOutcome(target, false);
        }
        catch (Exception)
        {
            TryDelete(_pendingFile);
            return null;
        }
    }

    /// <summary>Окно сохраняется для запуска после обновления; дольше этого после закрытия лаунчера не восстанавливается.</summary>
    private static readonly TimeSpan WindowResumeLifetime = TimeSpan.FromMinutes(30);

    private string WindowResumeFile => Path.Combine(Path.GetDirectoryName(_pendingFile)!, "update-window.json");

    /// <summary>Запомнить окно перед закрытием ради обновления.</summary>
    public void SaveWindowResume(WindowResume resume)
    {
        resume.SavedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(Path.GetDirectoryName(WindowResumeFile)!);
        File.WriteAllText(WindowResumeFile, JsonSerializer.Serialize(resume));
    }

    /// <summary>Окно, каким оно было перед обновлением (один раз: файл удаляется). null — не после обновления или слишком давно.</summary>
    public WindowResume? TakeWindowResume()
    {
        try
        {
            if (!File.Exists(WindowResumeFile))
                return null;
            var resume = JsonSerializer.Deserialize<WindowResume>(File.ReadAllText(WindowResumeFile));
            File.Delete(WindowResumeFile);
            var age = DateTimeOffset.Now - resume?.SavedAt;
            return age is { } a && a >= TimeSpan.Zero && a <= WindowResumeLifetime &&
                   double.IsFinite(resume!.Left) && double.IsFinite(resume.Top) &&
                   double.IsFinite(resume.Width) && double.IsFinite(resume.Height) && resume.Width > 0 && resume.Height > 0
                ? resume
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            TryDelete(WindowResumeFile);
            return null;
        }
    }

    /// <summary>Последние строки журнала установки через службу (update.log) — для технического журнала при неудаче.</summary>
    public IReadOnlyList<string> UpdateLogTail(int count)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(_statusFile)!, "update.log");
            if (!File.Exists(path))
                return Array.Empty<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
            return lines.Skip(Math.Max(0, lines.Count - count)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    // ---------------------------------------------------------------- вспомогательное

    private sealed class PendingUpdate
    {
        public string TargetVersion { get; set; } = string.Empty;
        public string FromVersion { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }

        /// <summary>service — через службу, setup или пусто — обычным установщиком.</summary>
        public string? Mode { get; set; }
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        // Таймаут задаётся отдельно для каждого запроса: проверка — 20 с, загрузка — пока пользователь не отменит.
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SafeLauncher", Format(CurrentVersion)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    internal static Version Normalize(Version v) => UpdatePackage.Normalize(v);

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
            // не страшно: файлы удалятся при следующей загрузке
        }
    }
}
