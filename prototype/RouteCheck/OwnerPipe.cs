using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RouteCheck;

/// <summary>
/// Ответы ядру на вопрос «чей это процесс». Так же будет делать лаунчер: ядро присылает PID процесса,
/// который открыл соединение, а мы отвечаем меткой приложения, если процесс входит в его задание
/// (то есть запущен из лаунчера или порождён таким процессом), и пустой строкой иначе.
///
/// Протокол (см. core/patches): запрос — uint32 PID; ответ — uint16 длина и метка в UTF-8, 0 — «не наш».
/// </summary>
internal sealed class OwnerPipe : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly string _tag;
    private readonly CancellationTokenSource _stop = new();
    private int _queries;
    private int _tagged;

    public OwnerPipe(SafeFileHandle job, string tag)
    {
        _job = job;
        _tag = tag;
        Name = "SafeLauncher-Owner-" + Guid.NewGuid().ToString("N");
        new Thread(AcceptLoop) { IsBackground = true, Name = "Owner pipe" }.Start();
    }

    public string Name { get; }

    /// <summary>Отвечать только этому процессу (ядру). 0 — пока не известен, вопросы не принимаются.</summary>
    public int AllowedClientPid { get; set; }

    public int Queries => Volatile.Read(ref _queries);
    public int Tagged => Volatile.Read(ref _tagged);

    /// <summary>PID, которые ядро спрашивало и которые оказались нашими.</summary>
    public ConcurrentDictionary<uint, bool> TaggedPids { get; } = new();

    /// <summary>Все PID, о которых спрашивало ядро.</summary>
    public ConcurrentDictionary<uint, bool> QueriedPids { get; } = new();

    public int RejectedClients;

    private void AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                pipe.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(100);
                continue;
            }

            new Thread(() => Serve(pipe)) { IsBackground = true, Name = "Owner pipe client" }.Start();
        }
    }

    private void Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != AllowedClientPid)
            {
                Interlocked.Increment(ref RejectedClients);
                return;
            }

            var request = new byte[4];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    pipe.ReadExactly(request);
                    var pid = BitConverter.ToUInt32(request);
                    Interlocked.Increment(ref _queries);
                    QueriedPids[pid] = true;

                    var ours = Native.IsInJob(pid, _job);
                    if (ours)
                    {
                        Interlocked.Increment(ref _tagged);
                        TaggedPids[pid] = true;
                    }

                    var tag = ours ? Encoding.UTF8.GetBytes(_tag) : Array.Empty<byte>();
                    var answer = new byte[2 + tag.Length];
                    BitConverter.TryWriteBytes(answer.AsSpan(0, 2), (ushort)tag.Length);
                    tag.CopyTo(answer, 2);
                    pipe.Write(answer);
                    pipe.Flush();
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
            {
                // ядро закрыло канал
            }
        }
    }

    public void Dispose() => _stop.Cancel();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int clientProcessId);
}
