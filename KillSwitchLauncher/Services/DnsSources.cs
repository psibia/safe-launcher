using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace KillSwitchLauncher.Services;

/// <summary>Откуда журнал DNS получает запросы. Dispose останавливает получение.</summary>
public interface IDnsEventSource : IDisposable
{
    /// <summary>Источник перестал работать сам (помощник закрыли, сессию остановили). Любой поток.</summary>
    event EventHandler? Stopped;
}

/// <summary>
/// Как запустить источник событий DNS. Сначала прямо в лаунчере: это работает, если лаунчер запущен
/// от имени администратора или пользователь входит в группу «Пользователи журналов производительности».
/// Иначе — через помощника: та же программа, запущенная с правами администратора (Windows спросит
/// разрешение), которая только читает события DNS и передаёт их лаунчеру.
/// </summary>
public static class DnsSources
{
    public static async Task<IDnsEventSource> StartAsync(Action<DnsQueryEvent> onEvent)
    {
        try
        {
            return InProcessDnsSource.Start(onEvent);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == EtwNative.ErrorAccessDenied)
        {
            return await CollectorDnsSource.StartAsync(onEvent);
        }
    }
}

/// <summary>Сессия ETW прямо в процессе лаунчера.</summary>
internal sealed class InProcessDnsSource : IDnsEventSource
{
    private readonly DnsEtwSession _session;

    private InProcessDnsSource(DnsEtwSession session)
    {
        _session = session;
        _session.Stopped += (_, _) => Stopped?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Stopped;

    public static InProcessDnsSource Start(Action<DnsQueryEvent> onEvent) => new(DnsEtwSession.Start(onEvent));

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Обмен с помощником по именованному каналу. Канал создаёт лаунчер со случайным именем и доступом только
/// для текущего пользователя; помощник проверяет, что на том конце именно лаунчер, запустивший его,
/// а лаунчер — что подключился именно запущенный помощник. По каналу идут только записи о запросах.
/// </summary>
internal static class DnsPipeProtocol
{
    public const byte Ready = 1; // int: 0 — сессия запущена, иначе код ошибки Windows
    public const byte Query = 2; // long FILETIME UTC, int PID, ushort тип, string имя

    public static void WriteQuery(BinaryWriter writer, DnsQueryEvent e)
    {
        writer.Write(Query);
        writer.Write(e.TimeUtc.ToFileTimeUtc());
        writer.Write(e.ProcessId);
        writer.Write(e.Type);
        writer.Write(e.Name);
    }

    public static DnsQueryEvent ReadQuery(BinaryReader reader)
    {
        var time = DateTime.FromFileTimeUtc(reader.ReadInt64());
        var pid = reader.ReadInt32();
        var type = reader.ReadUInt16();
        var name = reader.ReadString();
        return new DnsQueryEvent(time, pid, name, type);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

/// <summary>Источник через помощника с правами администратора (сторона лаунчера).</summary>
internal sealed class CollectorDnsSource : IDnsEventSource
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly NamedPipeServerStream _pipe;
    private readonly Action<DnsQueryEvent> _onEvent;
    private int _disposed;

    private CollectorDnsSource(NamedPipeServerStream pipe, Action<DnsQueryEvent> onEvent)
    {
        _pipe = pipe;
        _onEvent = onEvent;
    }

    public event EventHandler? Stopped;

    /// <summary>
    /// Запускает помощника и ждёт, пока он подключится и создаст сессию. Синхронная часть (окно UAC)
    /// выполняется в вызывающем потоке. Win32Exception 1223 — пользователь отказал в запросе UAC.
    /// </summary>
    public static async Task<CollectorDnsSource> StartAsync(Action<DnsQueryEvent> onEvent)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к лаунчеру");
        var pipeName = "SafeLauncher-DNS-" + Guid.NewGuid().ToString("N");

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);

        try
        {
            int? collectorPid;
            using (var process = Process.Start(new ProcessStartInfo(exe)
                   {
                       UseShellExecute = true,
                       Verb = "runas",
                       Arguments = $"{DnsCollector.Argument} {pipeName} {Environment.ProcessId}",
                       WindowStyle = ProcessWindowStyle.Hidden
                   }))
            {
                collectorPid = process?.Id;
            }

            using (var timeout = new CancellationTokenSource(ConnectTimeout))
            {
                try
                {
                    await pipe.WaitForConnectionAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("Помощник журнала DNS не ответил");
                }
            }

            if (collectorPid is { } expected &&
                (!DnsPipeProtocol.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var actual) || actual != expected))
                throw new InvalidOperationException("К каналу журнала DNS подключилась чужая программа");

            // Первая запись — удалось ли помощнику создать сессию.
            var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
            var ready = Task.Run(() => (Kind: reader.ReadByte(), Code: reader.ReadInt32()));
            if (await Task.WhenAny(ready, Task.Delay(ConnectTimeout)) != ready)
                throw new TimeoutException("Помощник журнала DNS не ответил");

            var (kind, code) = await ready;
            if (kind != DnsPipeProtocol.Ready)
                throw new InvalidDataException("Помощник журнала DNS ответил непонятно");
            if (code != 0)
                throw new Win32Exception(code);

            var source = new CollectorDnsSource(pipe, onEvent);
            new Thread(() => source.ReadLoop(reader)) { IsBackground = true, Name = "Журнал DNS (помощник)" }.Start();
            return source;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private void ReadLoop(BinaryReader reader)
    {
        try
        {
            while (true)
            {
                if (reader.ReadByte() != DnsPipeProtocol.Query)
                    break;
                var e = DnsPipeProtocol.ReadQuery(reader);
                try
                {
                    _onEvent(e);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }
        }
        catch (Exception)
        {
            // канал закрыт: помощник завершился или мы сами закрылись
        }

        if (Volatile.Read(ref _disposed) == 0)
            Stopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Закрытие канала — сигнал помощнику: он останавливает сессию и выходит.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _pipe.Dispose();
    }
}

/// <summary>
/// Помощник журнала DNS: тот же exe лаунчера, запущенный с ключом --dns-collector и правами администратора.
/// Окон не показывает. Подключается к каналу лаунчера, создаёт сессию ETW и пересылает запросы,
/// пока канал открыт. Лаунчер закрылся или упал — канал рвётся, помощник останавливает сессию и выходит.
/// </summary>
public static class DnsCollector
{
    public const string Argument = "--dns-collector";

    /// <summary>Аргументы: имя канала и PID лаунчера. Возвращает код выхода.</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[2], out var launcherPid) ||
            !args[1].StartsWith("SafeLauncher-DNS-", StringComparison.Ordinal))
            return 1;

        try
        {
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10_000);

            // Отдаём события только тому, кто нас запустил.
            if (!DnsPipeProtocol.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != launcherPid)
                return 2;

            using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
            using var stop = new ManualResetEventSlim();
            var gate = new object();

            DnsEtwSession session;
            try
            {
                session = DnsEtwSession.Start(e =>
                {
                    lock (gate)
                    {
                        try
                        {
                            DnsPipeProtocol.WriteQuery(writer, e);
                            writer.Flush();
                        }
                        catch (Exception)
                        {
                            stop.Set(); // лаунчер закрыл канал
                        }
                    }
                });
            }
            catch (Win32Exception ex)
            {
                lock (gate)
                {
                    writer.Write(DnsPipeProtocol.Ready);
                    writer.Write(ex.NativeErrorCode == 0 ? -1 : ex.NativeErrorCode);
                    writer.Flush();
                }

                return 3;
            }

            using (session)
            {
                session.Stopped += (_, _) => stop.Set();
                lock (gate)
                {
                    writer.Write(DnsPipeProtocol.Ready);
                    writer.Write(0);
                    writer.Flush();
                }

                // Лаунчер ничего не пишет: чтение вернёт 0, когда он закроет канал или завершится.
                new Thread(() =>
                {
                    var buffer = new byte[16];
                    try
                    {
                        while (pipe.Read(buffer, 0, buffer.Length) > 0)
                        {
                        }
                    }
                    catch (Exception)
                    {
                        // канал оборван
                    }

                    stop.Set();
                }) { IsBackground = true }.Start();

                stop.Wait();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return 4;
        }
    }
}
