using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Ответы ядру VPN на вопрос «чей это процесс». Для каждого нового соединения ядро присылает PID процесса,
/// а лаунчер отвечает меткой приложения, если процесс его (запущен из лаунчера или порождён таким процессом),
/// и пустым ответом иначе. По метке ядро решает, через какое подключение пустить соединение.
///
/// Протокол (core/patches): запрос — uint32 PID; ответ — uint16 длина и метка в UTF-8, длина 0 — «не наш».
/// Отвечаем только ядру: клиент канала должен работать в сеансе служб (0), где обычные программы пользователя
/// запуститься не могут, а когда PID ядра известен — быть именно им.
/// </summary>
public sealed class OwnerPipeServer : IDisposable
{
    public const string NamePrefix = "SafeLauncher-Owner-";

    private readonly Func<uint, string?> _resolve;
    private readonly CancellationTokenSource _stop = new();
    private int _queries;
    private int _rejected;

    /// <param name="resolve">PID → метка приложения или null, если процесс не из лаунчера. Вызывается из фоновых потоков.</param>
    public OwnerPipeServer(Func<uint, string?> resolve)
    {
        _resolve = resolve;
        Name = NamePrefix + Guid.NewGuid().ToString("N");
        new Thread(AcceptLoop) { IsBackground = true, Name = "Owner pipe" }.Start();
    }

    /// <summary>Имя канала для службы (без \\.\pipe\).</summary>
    public string Name { get; }

    /// <summary>PID ядра, когда служба его сообщила; 0 — пока не известен.</summary>
    public int CorePid { get; set; }

    /// <summary>Сколько раз ядро спрашивало.</summary>
    public int Queries => Volatile.Read(ref _queries);

    /// <summary>Сколько подключений к каналу отклонено (не ядро).</summary>
    public int Rejected => Volatile.Read(ref _rejected);

    /// <summary>PID, о которых спрашивало ядро, и ответ на каждый (пустая строка — «не наш»). Для диагностики.</summary>
    public ConcurrentDictionary<uint, string> Answers { get; } = new();

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
            if (!IsCore(pipe))
            {
                Interlocked.Increment(ref _rejected);
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

                    string? tag;
                    try
                    {
                        tag = _resolve(pid);
                    }
                    catch (Exception)
                    {
                        // Не знаем — не отвечаем «не наш»: иначе соединение приложения ушло бы напрямую. Без ответа ядро
                        // оставит процесс с приложением, о котором лаунчер уже говорил (ядро помнит ответы).
                        return;
                    }

                    Answers[pid] = tag ?? string.Empty;
                    var bytes = tag is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(tag);
                    var answer = new byte[2 + bytes.Length];
                    BitConverter.TryWriteBytes(answer.AsSpan(0, 2), (ushort)bytes.Length);
                    bytes.CopyTo(answer, 2);
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

    private bool IsCore(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session) || session != 0)
            return false;
        var corePid = CorePid;
        return corePid == 0 || GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) && clientPid == corePid;
    }

    public void Dispose() => _stop.Cancel();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint clientSessionId);
}
