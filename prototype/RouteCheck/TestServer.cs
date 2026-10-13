using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RouteCheck;

/// <summary>
/// Тестовый «VPN-сервер» на этом же компьютере: ещё одна копия ядра (launcher-core.exe) без адаптера, только
/// прокси SOCKS5 на 127.0.0.1. Лаунчер получает его как подключение «Локальный прокси», а по журналу сервера видно,
/// какие адреса через него открывались. Так проверяется, что трафик приложения действительно идёт через подключение,
/// и при этом настоящий VPN-сервер не нужен.
/// </summary>
internal sealed class TestServer : IDisposable
{
    public const string ExeName = "launcher-core.exe";
    public const string ZipName = "launcher-core.zip";

    private readonly Process _process;
    private readonly SafeFileHandle _job;
    private readonly List<string> _log = new();
    private readonly ManualResetEventSlim _started = new();

    private TestServer(Process process, SafeFileHandle job, int port)
    {
        _process = process;
        _job = job;
        Port = port;
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public int Port { get; }

    public string[] LogLines
    {
        get
        {
            lock (_log)
                return _log.ToArray();
        }
    }

    /// <summary>Путь к launcher-core.exe рядом с программой; если лежит только архив — распаковывает его.</summary>
    public static string Locate(string directory)
    {
        var exe = Path.Combine(directory, ExeName);
        var zip = Path.Combine(directory, ZipName);
        if (File.Exists(zip))
        {
            using var archive = ZipFile.OpenRead(zip);
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new FileNotFoundException($"В {ZipName} нет {ExeName}.", zip);
            // Распаковываем, если рядом нет ядра или лежит другое (например, от прошлой версии проверки).
            var current = new FileInfo(exe);
            if (!current.Exists || current.Length != entry.Length || current.LastWriteTime != entry.LastWriteTime.LocalDateTime)
                entry.ExtractToFile(exe, overwrite: true);
        }

        if (!File.Exists(exe))
            throw new FileNotFoundException($"Рядом с программой нет ни {ExeName}, ни {ZipName}.", exe);
        return exe;
    }

    /// <summary>Свободный порт на 127.0.0.1. Если на нём никто не слушает — это «выключенный сервер».</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static TestServer Start(string exe, string workDirectory)
    {
        Directory.CreateDirectory(workDirectory);
        var port = FreePort();
        var config = Path.Combine(workDirectory, "server.json");
        File.WriteAllText(config, $$"""
            {
              "log": { "level": "info", "timestamp": true },
              "inbounds": [{ "type": "socks", "tag": "vpn-server", "listen": "127.0.0.1", "listen_port": {{port}} }],
              "outbounds": [{ "type": "direct", "tag": "internet" }]
            }
            """, new UTF8Encoding(false));

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDirectory
        };
        foreach (var arg in new[] { "run", "-c", config, "-D", workDirectory, "--disable-color" })
            info.ArgumentList.Add(arg);

        var job = Native.CreateKillOnCloseJob();
        var process = Process.Start(info) ?? throw new InvalidOperationException("Тестовый сервер не запустился.");
        try
        {
            Native.Assign(job, process.SafeHandle);
        }
        catch
        {
            process.Kill();
            job.Dispose();
            throw;
        }

        return new TestServer(process, job, port);
    }

    /// <summary>Ждёт, пока сервер начнёт принимать соединения.</summary>
    public bool WaitReady(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                return false;
            if (_started.IsSet)
            {
                try
                {
                    using var client = new TcpClient();
                    client.Connect(IPAddress.Loopback, Port);
                    return true;
                }
                catch (SocketException)
                {
                    // ещё не слушает
                }
            }

            Thread.Sleep(200);
        }

        return false;
    }

    /// <summary>Сколько строк в журнале сейчас: чтобы потом смотреть только новые.</summary>
    public int Mark()
    {
        lock (_log)
            return _log.Count;
    }

    /// <summary>Открывалось ли через сервер соединение к адресу (после отметки <paramref name="since"/>).</summary>
    public bool Saw(IPEndPoint target, int since)
    {
        var needle = "connection to " + target;
        lock (_log)
            return _log.Skip(since).Any(l => l.Contains(needle, StringComparison.Ordinal));
    }

    /// <summary>
    /// Приходил ли через сервер UDP (после отметки). Адрес назначения UDP через SOCKS5 в журнале сервера не пишется,
    /// поэтому засчитывается любое UDP-соединение: в это время через сервер ходит только программа лаунчера.
    /// </summary>
    public bool SawPackets(int since)
    {
        lock (_log)
            return _log.Skip(since).Any(l => l.Contains("inbound packet connection", StringComparison.Ordinal));
    }

    private void OnLine(string? line)
    {
        if (line is null)
            return;
        lock (_log)
            _log.Add(line);
        if (line.Contains("sing-box started", StringComparison.Ordinal))
            _started.Set();
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        _job.Dispose();
        _started.Dispose();
    }
}
