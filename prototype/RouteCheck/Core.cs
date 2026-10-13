using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RouteCheck;

/// <summary>
/// Ядро VPN (sing-box с правкой из core/patches, файл launcher-core.exe) на время проверки. Поднимает адаптер «SafeLauncherTest»,
/// через который идёт весь трафик компьютера. Чужой трафик ядро отправляет напрямую, а соединения
/// процессов с меткой «routecheck» отклоняет: так видно, узнало ли ядро процессы лаунчера, без VPN-сервера.
/// В лаунчере вместо отклонения будет отправка через подключение приложения.
/// </summary>
internal sealed class Core : IDisposable
{
    public const string Tag = "routecheck";
    public const string InterfaceName = "SafeLauncherTest";
    private const string ExeName = "launcher-core.exe";
    private const string ZipName = "launcher-core.zip";

    private readonly Process _process;
    private readonly SafeFileHandle _job;
    private readonly List<string> _log = new();
    private readonly ManualResetEventSlim _started = new();

    private Core(Process process, SafeFileHandle job)
    {
        _process = process;
        _job = job;
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;

    public string[] LogLines
    {
        get
        {
            lock (_log)
                return _log.ToArray();
        }
    }

    /// <summary>Путь к sing-box.exe рядом с программой; если лежит только архив — распаковывает его.</summary>
    public static string Locate(string directory)
    {
        var exe = Path.Combine(directory, ExeName);
        if (File.Exists(exe))
            return exe;

        var zip = Path.Combine(directory, ZipName);
        if (!File.Exists(zip))
            throw new FileNotFoundException($"Рядом с программой нет ни {ExeName}, ни {ZipName}.", exe);

        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new FileNotFoundException($"В {ZipName} нет {ExeName}.", zip);
            entry.ExtractToFile(exe, overwrite: true);
        }

        return exe;
    }

    /// <summary>Копия ядра в другой папке (например, с путём только из латинских букв).</summary>
    public static string CopyTo(string exe, string directory)
    {
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, ExeName);
        File.Copy(exe, copy, overwrite: true);
        return copy;
    }

    /// <summary>Настройки ядра: адаптер на весь трафик, процессы с меткой — отклонить, остальное — напрямую.</summary>
    public static string Config(bool strictRoute) => $$"""
        {
          "log": { "level": "info", "timestamp": true },
          "inbounds": [{
            "type": "tun", "tag": "tun-in",
            "interface_name": "{{InterfaceName}}",
            "address": ["172.19.0.1/30", "fdfe:dcba:9877::1/126"],
            "auto_route": true, "strict_route": {{(strictRoute ? "true" : "false")}}, "stack": "mixed"
          }],
          "outbounds": [{ "type": "direct", "tag": "direct" }],
          "route": {
            "auto_detect_interface": true,
            "find_process": true,
            "rules": [
              { "process_path_regex": ["^launcher:{{Tag}}\\|"], "action": "reject" }
            ],
            "final": "direct"
          }
        }
        """;

    /// <summary>
    /// Запускает ядро. Оно делит с нами консоль (нужно для мягкой остановки через Ctrl+C) и входит в своё
    /// задание: если проверка упадёт, Windows закроет ядро вместе с ней.
    /// </summary>
    public static Core Start(string exe, string workDirectory, string pipeName, bool strictRoute = true)
    {
        Directory.CreateDirectory(workDirectory);
        var config = Path.Combine(workDirectory, "config.json");
        File.WriteAllText(config, Config(strictRoute), new UTF8Encoding(false));

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDirectory
        };
        foreach (var arg in new[] { "run", "-c", config, "-D", workDirectory, "--disable-color" })
            info.ArgumentList.Add(arg);
        info.Environment["SAFE_LAUNCHER_OWNER_PIPE"] = pipeName;
        info.Environment["SAFE_LAUNCHER_OWNER_PID"] = Environment.ProcessId.ToString();

        var job = Native.CreateKillOnCloseJob();
        var process = Process.Start(info) ?? throw new InvalidOperationException("Ядро не запустилось.");
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

        return new Core(process, job);
    }

    /// <summary>Ждёт, пока ядро сообщит о запуске и в системе появится его адаптер.</summary>
    public bool WaitReady(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                return false;
            if (_started.IsSet && IsInterfaceUp())
            {
                Thread.Sleep(1500); // маршруты и правила брандмауэра применяются чуть позже адаптера
                return !_process.HasExited;
            }

            Thread.Sleep(250);
        }

        return false;
    }

    public static bool InterfaceExists() => FindInterface() is not null;

    private static bool IsInterfaceUp() => FindInterface()?.OperationalStatus == OperationalStatus.Up;

    private static NetworkInterface? FindInterface() =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.Name.Equals(InterfaceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ждёт, пока адаптер ядра исчезнет из системы.</summary>
    public static bool WaitInterfaceGone(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!InterfaceExists())
                return true;
            Thread.Sleep(250);
        }

        return !InterfaceExists();
    }

    /// <summary>
    /// Мягкая остановка, как при выходе из лаунчера: Ctrl+C, ядро само убирает адаптер и маршруты.
    /// Сами мы сигнал на это время игнорируем. Если ядро не закрылось за отведённое время — принудительно.
    /// </summary>
    /// <returns>true, если ядро закрылось само.</returns>
    public bool Stop(TimeSpan timeout)
    {
        if (_process.HasExited)
            return true;

        ConsoleCancelEventHandler ignore = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += ignore;
        Native.SetConsoleCtrlHandler(IntPtr.Zero, true);
        try
        {
            if (Native.GenerateConsoleCtrlEvent(Native.CtrlCEvent, 0) && _process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                _process.WaitForExit(); // дочитать вывод
                return true;
            }

            _process.Kill();
            _process.WaitForExit(5000);
            return false;
        }
        finally
        {
            Thread.Sleep(200); // сигнал доставляется асинхронно — не снимаем защиту раньше времени
            Native.SetConsoleCtrlHandler(IntPtr.Zero, false);
            Console.CancelKeyPress -= ignore;
        }
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
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }

        _process.Dispose();
        _job.Dispose();
        _started.Dispose();
    }
}
