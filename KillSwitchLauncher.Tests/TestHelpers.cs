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
    private readonly Dictionary<string, (int Status, byte[] Body)> _routes = new();

    public LocalHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoop();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Map(string path, byte[] body, int status = 200)
    {
        lock (_routes)
            _routes[path] = (status, body);
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

                var path = head.ToString().Split(' ')[1];
                (int Status, byte[] Body) route;
                lock (_routes)
                    route = _routes.TryGetValue(path, out var r) ? r : (404, Array.Empty<byte>());

                var header = $"HTTP/1.1 {route.Status} {(route.Status == 200 ? "OK" : "Error")}\r\n" +
                             $"Content-Length: {route.Body.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(header);
                stream.Write(bytes, 0, bytes.Length);
                stream.Write(route.Body, 0, route.Body.Length);
                stream.Flush();
            }
            catch (Exception)
            {
                // клиент оборвал соединение
            }
        }
    }

    public void Dispose() => _listener.Stop();
}
