using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SafeLauncher.Service;

/// <summary>Ядро не запустилось. Сообщение — для пользователя, в Log — последние строки журнала ядра.</summary>
public sealed class CoreStartException : Exception
{
    public CoreStartException(string message, IReadOnlyList<string> log) : base(message) => Log = log;

    public IReadOnlyList<string> Log { get; }
}

/// <summary>
/// Процесс ядра (launcher-core.exe рядом со службой). Настройки ядро получает через стандартный ввод, на диск
/// они не пишутся (в них ключи подключений). Ядро входит в задание службы: если служба упадёт, Windows закроет
/// и его. Останавливается мягко, через Ctrl+C в общей со службой консоли, — тогда ядро само убирает адаптер и маршруты.
/// </summary>
internal sealed class CoreRunner : IDisposable
{
    public const string ExeName = "launcher-core.exe";
    private const int LogLines = 300;

    private readonly string _exe;
    private readonly string _workDirectory;
    private readonly SafeFileHandle _job = Native.CreateKillOnCloseJob();
    private readonly object _sync = new();
    private readonly LinkedList<string> _log = new();
    private Process? _process;
    private ManualResetEventSlim? _started;
    private bool _stopping;
    private bool _starting;

    public CoreRunner(string directory)
    {
        _exe = Path.Combine(directory, ExeName);
        _workDirectory = Path.Combine(directory, "run");
    }

    /// <summary>Ядро завершилось само, не по команде: код выхода и последние строки журнала.</summary>
    public event Action<int, IReadOnlyList<string>>? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _process is { HasExited: false };
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_sync)
                return _process is { HasExited: false } p ? p.Id : null;
        }
    }

    public IReadOnlyList<string> LogTail
    {
        get
        {
            lock (_log)
                return _log.ToList();
        }
    }

    /// <summary>Запускает ядро и ждёт, пока оно сообщит о запуске и поднимет адаптер. Возвращает PID ядра.</summary>
    public int Start(string config, string ownerPipe, int ownerPid, TimeSpan readyTimeout)
    {
        lock (_sync)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("Ядро уже запущено.");
            lock (_log)
                _log.Clear();
            if (!File.Exists(_exe))
                throw new CoreStartException($"Не найден файл ядра: {_exe}", Array.Empty<string>());
            Directory.CreateDirectory(_workDirectory);

            var info = new ProcessStartInfo(_exe)
            {
                UseShellExecute = false,
                CreateNoWindow = false, // общая со службой консоль: через неё ядро получает Ctrl+C
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = _workDirectory
            };
            foreach (var arg in new[] { "run", "-c", "stdin", "-D", _workDirectory, "--disable-color" })
                info.ArgumentList.Add(arg);
            info.Environment["SAFE_LAUNCHER_OWNER_PIPE"] = ownerPipe;
            info.Environment["SAFE_LAUNCHER_OWNER_PID"] = ownerPid.ToString();

            var started = new ManualResetEventSlim();
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(e.Data, started);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data, started);
            process.Exited += (_, _) => OnExited(process);
            _stopping = false;
            _starting = true;
            process.Start();
            try
            {
                Native.Assign(_job, process.SafeHandle);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.StandardInput.Write(config);
                process.StandardInput.Close();
            }
            catch
            {
                _starting = false;
                KillQuietly(process);
                throw;
            }

            _process = process;
            _started = started;
        }

        var ready = WaitReady(readyTimeout);
        lock (_sync)
            _starting = false;
        if (ready)
            return _process!.Id;

        var exited = !IsRunning;
        Stop(TimeSpan.FromSeconds(5));
        throw new CoreStartException(
            exited ? "Ядро закрылось сразу после запуска." : $"Ядро не запустилось за {readyTimeout.TotalSeconds:0} секунд.",
            LogTail);
    }

    private bool WaitReady(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Process? process;
            ManualResetEventSlim? started;
            lock (_sync)
            {
                process = _process;
                started = _started;
            }

            if (process is null || process.HasExited)
                return false;
            if (started?.IsSet == true && IsInterfaceUp())
                return true;
            Thread.Sleep(200);
        }

        return false;
    }

    public static bool InterfaceExists() =>
        NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name.Equals(CoreConfigBuilder.InterfaceName, StringComparison.OrdinalIgnoreCase));

    private static bool IsInterfaceUp() =>
        NetworkInterface.GetAllNetworkInterfaces().Any(n =>
            n.Name.Equals(CoreConfigBuilder.InterfaceName, StringComparison.OrdinalIgnoreCase) && n.OperationalStatus == OperationalStatus.Up);

    /// <summary>Мягкая остановка (Ctrl+C), если не вышло за отведённое время — принудительно.</summary>
    /// <returns>true, если ядро закрылось само.</returns>
    public bool Stop(TimeSpan graceful)
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            if (process is null)
                return true;
            _stopping = true;
        }

        var clean = true;
        if (!process.HasExited)
        {
            // Сигнал получат все процессы консоли: ядро и сама служба (она его игнорирует, см. Program).
            clean = Native.GenerateConsoleCtrlEvent(Native.CtrlCEvent, 0) && process.WaitForExit((int)graceful.TotalMilliseconds);
            if (!clean)
                KillQuietly(process);
            process.WaitForExit(5000);
        }

        lock (_sync)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _started = null;
            }
        }

        process.Dispose();
        Native.FlushDnsCache();
        return clean;
    }

    private void OnLine(string? line, ManualResetEventSlim started)
    {
        if (line is null)
            return;
        var tail = ServiceLog.CoreLine(line);
        lock (_log)
        {
            _log.AddLast(tail);
            while (_log.Count > LogLines)
                _log.RemoveFirst();
        }

        if (line.Contains("sing-box started", StringComparison.Ordinal))
            started.Set();
    }

    private void OnExited(Process process)
    {
        bool unexpected;
        int code;
        lock (_sync)
        {
            // Сбой при запуске сообщается ответом на команду start, а не событием.
            unexpected = !_stopping && !_starting && ReferenceEquals(_process, process);
            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = -1;
            }
        }

        Native.FlushDnsCache();
        if (unexpected)
        {
            Thread.Sleep(100); // дочитать последние строки журнала
            Exited?.Invoke(code, LogTail);
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception)
        {
            // уже завершается
        }
    }

    /// <summary>Ядра, оставшиеся от прошлого запуска службы (если её закрыли так, что задание не сработало).</summary>
    public void KillLeftovers()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, _exe, StringComparison.OrdinalIgnoreCase))
                        process.Kill();
                }
                catch (Exception)
                {
                    // нет доступа или уже завершился
                }
            }
        }
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(5));
        _job.Dispose();
    }
}
