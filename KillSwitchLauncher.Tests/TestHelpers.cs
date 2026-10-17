using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KillSwitchLauncher.Tests;

/// <summary>Временная папка теста (%TEMP%\KslTests\…), удаляется после теста. Настоящие настройки и журнал не трогаются.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "KslTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(params string[] parts) => Path.Combine(Root, Path.Combine(parts));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // файл ещё занят процессом: временная папка, не страшно
        }
    }
}

public static class Wait
{
    /// <summary>Ждёт, пока условие станет истинным. false — не дождались.</summary>
    public static bool Until(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
                return true;
            Thread.Sleep(50);
        }

        return condition();
    }

    /// <summary>То же, но не занимает поток: для таймеров, которые работают в пуле потоков.</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }

        return condition();
    }
}

/// <summary>IProgress без SynchronizationContext: отчёты приходят сразу.</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly List<T> _items = new();

    public IReadOnlyList<T> Items
    {
        get
        {
            lock (_items)
                return _items.ToArray();
        }
    }

    public void Report(T value)
    {
        lock (_items)
            _items.Add(value);
    }
}

/// <summary>Маленький HTTP-сервер на 127.0.0.1 для проверки загрузки обновления без интернета.</summary>
public sealed class LocalHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<string, Route> _routes = new();
    private readonly Dictionary<string, int> _hits = new();
    private readonly Dictionary<string, string?> _ranges = new();
    private readonly ManualResetEventSlim _stopped = new();

    /// <param name="Ranges">Сервер умеет присылать часть файла (Range: bytes=N-), как GitHub.</param>
    /// <param name="DropFirstAfter">Первый ответ обрывается после стольких байт, будто пропала связь.</param>
    /// <param name="StallFirstAfter">Первый ответ замирает после стольких байт: соединение открыто, данных больше нет.</param>
    private sealed record Route(int Status, byte[] Body, bool Ranges, int? DropFirstAfter, int? StallFirstAfter = null);

    public LocalHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoop();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Сколько раз запрашивали путь.</summary>
    public int Hits(string path)
    {
        lock (_routes)
            return _hits.TryGetValue(path, out var n) ? n : 0;
    }

    /// <summary>Заголовок Range последнего запроса пути; null — его не было.</summary>
    public string? LastRange(string path)
    {
        lock (_routes)
            return _ranges.TryGetValue(path, out var range) ? range : null;
    }

    public string Map(string path, byte[] body, int status = 200, bool ranges = false, int? dropFirstAfter = null, int? stallFirstAfter = null)
    {
        lock (_routes)
            _routes[path] = new Route(status, body, ranges, dropFirstAfter, stallFirstAfter);
        return $"http://127.0.0.1:{Port}{path}";
    }

    private async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return; // сервер остановлен
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    var b = stream.ReadByte();
                    if (b < 0) return;
                    head.Append((char)b);
                }

                var lines = head.ToString().Split("\r\n");
                var path = lines[0].Split(' ')[1];
                string? range = null;
                foreach (var line in lines)
                {
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        range = line["Range:".Length..].Trim();
                }

                Route route;
                int hit;
                lock (_routes)
                {
                    route = _routes.TryGetValue(path, out var r) ? r : new Route(404, Array.Empty<byte>(), false, null);
                    hit = _hits[path] = (_hits.TryGetValue(path, out var n) ? n : 0) + 1;
                    _ranges[path] = range;
                }

                var status = route.Status;
                var body = route.Body;
                var extra = string.Empty;
                if (route.Ranges && status == 200 && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) && range.EndsWith('-'))
                {
                    var from = int.Parse(range[6..^1]);
                    if (from >= body.Length)
                    {
                        status = 416;
                        body = Array.Empty<byte>();
                    }
                    else
                    {
                        status = 206;
                        extra = $"Content-Range: bytes {from}-{body.Length - 1}/{body.Length}\r\n";
                        body = body[from..];
                    }
                }

                var header = $"HTTP/1.1 {status} {(status is 200 or 206 ? "OK" : "Error")}\r\n" + extra +
                             $"Content-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(header);
                stream.Write(bytes, 0, bytes.Length);
                // Обрыв связи: часть файла и закрытое соединение при заявленной полной длине.
                var send = hit == 1 && route.DropFirstAfter is { } drop ? Math.Min(drop, body.Length) : body.Length;
                if (hit == 1 && route.StallFirstAfter is { } stall)
                    send = Math.Min(stall, body.Length);
                stream.Write(body, 0, send);
                stream.Flush();
                if (hit == 1 && route.StallFirstAfter is not null)
                {
                    // Зависание: соединение открыто, данных больше нет (пока клиент не оборвёт или сервер не остановят).
                    _stopped.Wait(TimeSpan.FromSeconds(30));
                    return;
                }

                if (send < body.Length)
                    client.Client.LingerState = new LingerOption(true, 0);
            }
            catch (Exception)
            {
                // клиент оборвал соединение
            }
        }
    }

    public void Dispose()
    {
        _stopped.Set();
        _listener.Stop();
    }
}
