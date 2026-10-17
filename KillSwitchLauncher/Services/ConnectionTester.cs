using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Итог проверки подключения. Ok — сервер пропустил запрос в интернет; LatencyMs — время ответа через него;
/// Ip и Country — какими сайты видят приложения, идущие через это подключение. Error — почему не работает.
/// </summary>
public sealed record ConnectionCheck(bool Ok, int? LatencyMs, string? Ip, string? CountryCode, string? Country, string? Error, IReadOnlyList<string> Log);

/// <summary>Прокси работающего ядра для проверки подключений: порт на 127.0.0.1 и ключ, свои на каждый запуск ядра.</summary>
public sealed record CoreCheckProxy(int Port, string Secret);

/// <summary>Проверка через работающее ядро: его прокси и метка подключения в ядре (имя пользователя прокси).</summary>
public sealed record CoreCheckRoute(CoreCheckProxy Proxy, string Tag);

/// <summary>Прокси через подключение лаунчера (<see cref="ConnectionTester.OpenProxyAsync"/>). Dispose закрывает копию ядра, если она своя.</summary>
public sealed class ConnectionProxy : IDisposable
{
    private readonly WebProxy _proxy;
    private Action? _stop;

    internal ConnectionProxy(WebProxy proxy, Action? stop)
    {
        _proxy = proxy;
        _stop = stop;
    }

    /// <summary>Обработчик запросов через этот прокси (освобождает вызывающий).</summary>
    public HttpMessageHandler CreateHandler() => new HttpClientHandler { Proxy = _proxy, UseProxy = true };

    public void Dispose() => Interlocked.Exchange(ref _stop, null)?.Invoke();
}

/// <summary>
/// Проверка подключения без влияния на работу компьютера: отдельная копия ядра (launcher-core.exe) с правами пользователя,
/// без адаптера, только прокси на 127.0.0.1, за которым стоит проверяемый сервер. Через этот прокси лаунчер открывает
/// маленькую страницу (задержка) и узнаёт свой внешний адрес и страну.
/// WireGuard, который уже работает в ядре службы, проверяется через само ядро (<see cref="CoreCheckRoute"/>): второй
/// туннель с тем же ключом отбил бы у ядра связь с сервером.
/// </summary>
public sealed class ConnectionTester
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly string _coreExe;
    private readonly Uri _latencyUrl;
    private readonly Uri _traceUrl;

    /// <summary>Ядро из папки службы (C:\Program Files\SafeLauncher\core).</summary>
    public ConnectionTester() : this(Path.Combine(CoreServiceClient.InstallDirectory, "launcher-core.exe"))
    {
    }

    public ConnectionTester(string coreExe)
        : this(coreExe, new Uri("http://cp.cloudflare.com/generate_204"), new Uri("https://www.cloudflare.com/cdn-cgi/trace"))
    {
    }

    /// <summary>Для тестов: свои адреса проверки.</summary>
    internal ConnectionTester(string coreExe, Uri latencyUrl, Uri traceUrl)
    {
        _coreExe = coreExe;
        _latencyUrl = latencyUrl;
        _traceUrl = traceUrl;
    }

    /// <param name="viaCore">Проверять через работающее ядро (см. <see cref="LauncherNetwork.CheckRouteFor"/>); null — отдельной копией.</param>
    public async Task<ConnectionCheck> CheckAsync(JsonObject server, CoreCheckRoute? viaCore = null, CancellationToken cancellation = default)
    {
        using var operation = TechLog.CurrentCid is null ? TechLog.Begin(out _) : null;
        var watch = Stopwatch.StartNew();
        TechLog.Info(LogAreas.Check, LogCodes.CheckStart,
            $"Проверка подключения: {(string?)server["type"] ?? "?"}, сервер {(string?)server["server"] ?? FirstPeer(server) ?? "?"}" +
            (viaCore is null ? string.Empty : $", через работающее ядро ({viaCore.Tag}): оно уже держит туннель с этим ключом"));
        var result = viaCore is null
            ? await CheckCoreAsync(server, cancellation).ConfigureAwait(false)
            : await CheckViaCoreAsync(server, viaCore, cancellation).ConfigureAwait(false);
        if (result.Ok)
            TechLog.Info(LogAreas.Check, LogCodes.CheckResult,
                $"Проверка прошла за {watch.ElapsedMilliseconds} мс: задержка {result.LatencyMs} мс, внешний адрес {result.Ip ?? "?"}, страна {result.CountryCode ?? "?"}");
        else
            TechLog.Warn(LogAreas.Check, LogCodes.CheckFail, $"Проверка не прошла за {watch.ElapsedMilliseconds} мс: {result.Error}");
        return result;
    }

    private static string? FirstPeer(JsonObject server) =>
        server["peers"] is JsonArray { Count: > 0 } peers && peers[0] is JsonObject peer ? (string?)peer["address"] : null;

    private async Task<ConnectionCheck> CheckCoreAsync(JsonObject server, CancellationToken cancellation)
    {
        if (!File.Exists(_coreExe))
            return Fail("Не найдено ядро VPN. Переустановите лаунчер.", Array.Empty<string>());

        var port = FreePort();
        var work = Path.Combine(Path.GetTempPath(), "SafeLauncher-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var log = new List<string>();
        using var process = StartCore(Config(server, port), work, log);
        using var job = OperatingSystem.IsWindows() ? KillOnCloseJob(process) : null;
        try
        {
            if (!await WaitListening(process, port, cancellation).ConfigureAwait(false))
                return Fail("Ядро не смогло открыть подключение: " + (LastError(log) ?? "причина неизвестна") + ".", Snapshot(log));

            var wireGuard = (string?)server["type"] == "wireguard";
            var (latency, ip, code) = await RequestsAsync(new WebProxy($"http://127.0.0.1:{port}"), wireGuard, cancellation).ConfigureAwait(false);
            return latency is null
                ? Fail(Explain(log, wireGuard, amnezia: server["amnezia"] is not null), Snapshot(log))
                : new ConnectionCheck(true, latency, ip, code, CountryName(code), null, Snapshot(log));
        }
        finally
        {
            StopCore(process, work);
        }
    }

    private static void StopCore(Process process, string work)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
            process.WaitForExit(3000);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (Exception)
        {
            // временная папка, не страшно
        }
    }

    /// <summary>
    /// Прокси через подключение на время загрузки (обновления лаунчера через подключение лаунчера): через работающее ядро,
    /// если подключение в нём уже есть (<paramref name="viaCore"/>), иначе отдельной копией ядра, как при проверке.
    /// Не вышло — <see cref="InvalidOperationException"/> с объяснением.
    /// </summary>
    public async Task<ConnectionProxy> OpenProxyAsync(JsonObject server, CoreCheckRoute? viaCore, CancellationToken cancellation)
    {
        if (viaCore is not null)
            return new ConnectionProxy(new WebProxy($"http://127.0.0.1:{viaCore.Proxy.Port}")
            {
                Credentials = new NetworkCredential(viaCore.Tag, viaCore.Proxy.Secret)
            }, null);

        if (!File.Exists(_coreExe))
            throw new InvalidOperationException("Не найдено ядро VPN. Переустановите лаунчер.");
        var port = FreePort();
        var work = Path.Combine(Path.GetTempPath(), "SafeLauncher-proxy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var log = new List<string>();
        var process = StartCore(Config(server, port), work, log);
        var job = OperatingSystem.IsWindows() ? KillOnCloseJob(process) : null;
        void Stop()
        {
            StopCore(process, work);
            job?.Dispose();
            process.Dispose();
        }

        try
        {
            if (!await WaitListening(process, port, cancellation).ConfigureAwait(false))
                throw new InvalidOperationException("Ядро не смогло открыть подключение: " + (LastError(log) ?? "причина неизвестна") + ".");
        }
        catch
        {
            Stop();
            throw;
        }

        return new ConnectionProxy(new WebProxy($"http://127.0.0.1:{port}"), Stop);
    }

    /// <summary>
    /// Через прокси работающего ядра: туннель к серверу уже есть, и приложения через него не теряют связь. Журнала ядра
    /// здесь нет (он у службы), поэтому причина неудачи общая.
    /// </summary>
    private async Task<ConnectionCheck> CheckViaCoreAsync(JsonObject server, CoreCheckRoute route, CancellationToken cancellation)
    {
        var proxy = new WebProxy($"http://127.0.0.1:{route.Proxy.Port}")
        {
            Credentials = new NetworkCredential(route.Tag, route.Proxy.Secret)
        };
        var wireGuard = (string?)server["type"] == "wireguard";
        var (latency, ip, code) = await RequestsAsync(proxy, wireGuard, cancellation).ConfigureAwait(false);
        return latency is null
            ? Fail(ExplainViaCore(amnezia: server["amnezia"] is not null), Array.Empty<string>())
            : new ConnectionCheck(true, latency, ip, code, CountryName(code), null, Array.Empty<string>());
    }

    /// <summary>Задержка, внешний адрес и код страны через прокси; задержка null — сайт через сервер не открылся.</summary>
    private async Task<(int? Latency, string? Ip, string? Code)> RequestsAsync(WebProxy proxy, bool wireGuard, CancellationToken cancellation)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = proxy,
            UseProxy = true,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SafeLauncher/2.1");

        // Первый запрос включает подключение к серверу, второй показывает обычную задержку. WireGuard сначала
        // договаривается с сервером о ключах (повтор каждые 5 с), поэтому ему даётся ещё одна попытка.
        var first = await Measure(client, cancellation).ConfigureAwait(false);
        if (first is null && wireGuard)
            first = await Measure(client, cancellation).ConfigureAwait(false);
        if (first is null)
            return (null, null, null);

        var second = await Measure(client, cancellation).ConfigureAwait(false);
        var latency = second is null ? first.Value : Math.Min(first.Value, second.Value);

        var (ip, code) = await Trace(client, cancellation).ConfigureAwait(false);
        return (latency, ip, code);
    }

    /// <summary>Настройки проверочного ядра: прокси на 127.0.0.1 → проверяемый сервер, больше ничего.</summary>
    internal static JsonObject Config(JsonObject server, int port)
    {
        var outbound = (JsonObject)server.DeepClone();
        outbound["tag"] = "check";
        var endpoint = (string?)outbound["type"] == "wireguard";
        var config = new JsonObject
        {
            // info: в журнале видно, через что ушло каждое соединение проверки (см. RouteLines).
            ["log"] = new JsonObject { ["level"] = "info" },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "check-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = port
            }),
            ["outbounds"] = endpoint ? new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }) : new JsonArray(outbound),
            ["route"] = new JsonObject { ["final"] = "check" }
        };
        if (endpoint)
            config["endpoints"] = new JsonArray(outbound);
        return config;
    }

    private Process StartCore(JsonObject config, string work, List<string> log)
    {
        var info = new ProcessStartInfo(_coreExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = work
        };
        foreach (var arg in new[] { "run", "-c", "stdin", "-D", work, "--disable-color" })
            info.ArgumentList.Add(arg);

        var process = new Process { StartInfo = info };
        var parser = new CoreLogParser();
        var cid = TechLog.CurrentCid;
        DataReceivedEventHandler onLine = (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (log)
                TechLog.CheckCoreLine(parser, e.Data, cid);
            lock (log)
            {
                log.Add(e.Data);
                if (log.Count > 200)
                    log.RemoveAt(0);
            }
        };
        process.OutputDataReceived += onLine;
        process.ErrorDataReceived += onLine;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Write(config.ToJsonString());
        process.StandardInput.Close();
        return process;
    }

    private static async Task<bool> WaitListening(Process process, int port, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                return false;
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, cancellation).ConfigureAwait(false);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(100, cancellation).ConfigureAwait(false);
            }
        }

        return false;
    }

    private async Task<int?> Measure(HttpClient client, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(_latencyUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            // Ошибку соединения с сервером ядро возвращает как ответ прокси с кодом 5xx — это не успех.
            return response.IsSuccessStatusCode ? (int)watch.ElapsedMilliseconds : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Внешний IP и код страны: страница cdn-cgi/trace Cloudflare отдаёт строки «ip=…» и «loc=…».</summary>
    private async Task<(string? Ip, string? Code)> Trace(HttpClient client, CancellationToken cancellation)
    {
        try
        {
            using var response = await client.GetAsync(_traceUrl, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, null);
            var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            string? Value(string key) => text.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
            var code = Value("loc");
            return (Value("ip"), code is { Length: 2 } ? code.ToUpperInvariant() : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            return (null, null);
        }
    }

    internal static string? CountryName(string? code)
    {
        if (code is null || code == "XX" || code == "T1")
            return null;
        try
        {
            return new RegionInfo(code).DisplayName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    /// <summary>Понятная причина по журналу ядра.</summary>
    /// <param name="wireGuard">Проверялся файл WireGuard: у него нет ключа в ссылке и подписки, причины другие.</param>
    /// <param name="amnezia">Это файл AmneziaWG: сервер молчит ещё и на чужие параметры AmneziaWG.</param>
    internal static string Explain(IReadOnlyList<string> log, bool wireGuard = false, bool amnezia = false)
    {
        string[] lines;
        lock (log)
            lines = log.Where(l => l.Contains("ERROR", StringComparison.Ordinal) || l.Contains("WARN", StringComparison.Ordinal) ||
                                   l.Contains("FATAL", StringComparison.Ordinal)).ToArray();
        var text = string.Join("\n", lines).ToLowerInvariant();
        if (text.Contains("refused"))
            return "Сервер отклонил соединение. Проверьте адрес и порт или включите программу, чей это прокси.";
        if (text.Contains("no such host") || text.Contains("lookup "))
            return "Не удалось найти сервер по имени. Возможно, адрес в ссылке устарел.";
        if (text.Contains("reality verification failed") || text.Contains("reality"))
            return "Сервер не подтвердил Reality: проверьте ключ (pbk) и short id (sid) в ссылке.";
        if (text.Contains("x509") || text.Contains("certificate"))
            return "Сертификат сервера не прошёл проверку.";
        if (text.Contains("authentication") || text.Contains("unauthorized") || text.Contains("invalid user") || text.Contains("auth failed"))
            return "Сервер не принял ключ доступа. Возможно, подписка закончилась.";
        if (text.Contains("timeout") || text.Contains("deadline"))
            return "Сервер не отвечает.";
        // Сервер с чужим ключом обычно молча закрывает соединение, и ядро ничего не пишет.
        if (LastError(lines) is { } error)
            return "Через сервер не удалось открыть сайт: " + error + ".";
        // Сервер WireGuard молча не отвечает на чужой ключ и на ключ, который уже подключён в другом месте.
        if (wireGuard && amnezia)
            return "Сервер AmneziaWG не ответил. Если этот же файл сейчас подключён в AmneziaVPN, AmneziaWG или на другом " +
                   "устройстве, отключите его там: один ключ работает только в одном месте. Иначе проверьте, что файл выдан " +
                   "этим сервером и не устарел: ключи, адрес и параметры AmneziaWG должны совпадать с сервером.";
        return wireGuard
            ? "Сервер WireGuard не ответил. Если этот же файл сейчас подключён в приложении WireGuard или на другом " +
              "устройстве, отключите его там: один ключ работает только в одном месте. Иначе проверьте ключи и адрес сервера в файле."
            : "Сервер не пропустил запрос. Чаще всего это неверный ключ доступа в ссылке или закончившаяся подписка.";
    }

    /// <summary>Причина неудачи проверки через работающее ядро: тот же туннель у приложений, значит, и у них сети нет.</summary>
    internal static string ExplainViaCore(bool amnezia) =>
        $"Сервер {(amnezia ? "AmneziaWG" : "WireGuard")} не ответил. Это подключение сейчас работает в лаунчере, и проверка шла " +
        "через него же: у приложений через него интернета, скорее всего, тоже нет. Если этот же файл подключён ещё где-то " +
        "(в другой программе или на другом устройстве), отключите его там: один ключ работает только в одном месте.";

    /// <summary>Строки журнала проверочного ядра о том, куда и через что ушли соединения проверки (для журнала событий).</summary>
    internal static IReadOnlyList<string> RouteLines(IReadOnlyList<string> log)
    {
        string[] lines;
        lock (log)
            lines = log.ToArray();
        return lines.Where(l => l.Contains("outbound connection", StringComparison.Ordinal) ||
                                l.Contains("ERROR", StringComparison.Ordinal) || l.Contains("WARN", StringComparison.Ordinal))
            .Take(8).ToList();
    }

    private static string? LastError(IReadOnlyList<string> log)
    {
        string[] lines;
        lock (log)
            lines = log.ToArray();
        var line = lines.LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal) || l.Contains("FATAL", StringComparison.Ordinal));
        if (line is null)
            return null;
        var colon = line.LastIndexOf(": ", StringComparison.Ordinal);
        return colon >= 0 ? line[(colon + 2)..].Trim() : line.Trim();
    }

    private static IReadOnlyList<string> Snapshot(List<string> log)
    {
        lock (log)
            return log.ToArray();
    }

    private static ConnectionCheck Fail(string error, IReadOnlyList<string> log) => new(false, null, null, null, null, error, log);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Если лаунчер закроется посреди проверки, Windows закроет и проверочное ядро.</summary>
    private static SafeKernelHandle? KillOnCloseJob(Process process)
    {
        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            return null;
        }

        var info = new NativeMethods.JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose;
        NativeMethods.SetInformationJobObject(job, NativeMethods.JobInfoExtendedLimit, ref info,
            (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>());
        NativeMethods.AssignProcessToJobObject(job, process.SafeHandle);
        return job;
    }
}
