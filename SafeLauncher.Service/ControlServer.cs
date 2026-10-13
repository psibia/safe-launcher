using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace SafeLauncher.Service;

/// <summary>
/// Канал управления: лаунчер пользователя командует, служба запускает и останавливает ядро.
/// Ядро живёт, пока открыт канал лаунчера, который его запустил: если лаунчер закрылся или упал,
/// канал рвётся, и служба сразу останавливает ядро. Одновременно ядром владеет только один лаунчер.
/// </summary>
internal sealed class ControlServer : IDisposable
{
    private static readonly Regex OwnerPipePattern = new("^SafeLauncher-Owner-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GracefulStop = TimeSpan.FromSeconds(8);

    private readonly CoreRunner _core;
    private readonly NetworkGuard _guard;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _ownerSync = new();
    private Session? _owner;

    public ControlServer(CoreRunner core, NetworkGuard guard, Action<string> log)
    {
        _core = core;
        _guard = guard;
        _log = log;
        _core.Exited += OnCoreExited;
    }

    public void Start() => new Thread(AcceptLoop) { IsBackground = true, Name = "Control pipe" }.Start();

    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // Пользователи за компьютером могут подключаться, но не создавать свои экземпляры канала с тем же именем.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private void AcceptLoop()
    {
        var first = true;
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(ControlProtocol.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 64 * 1024, 64 * 1024, Security());
                first = false;
                pipe.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log("Канал управления: " + ex.Message);
                Thread.Sleep(1000);
                continue;
            }

            new Thread(() => Serve(pipe)) { IsBackground = true, Name = "Control client" }.Start();
        }
    }

    private void Serve(NamedPipeServerStream pipe)
    {
        if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid))
        {
            pipe.Dispose();
            return;
        }

        var session = new Session(pipe, (int)clientPid);
        var input = new BufferedStream(pipe); // только для чтения: ответы пишутся прямо в канал
        try
        {
            while (!_stop.IsCancellationRequested && ReadMessage(input) is { } line)
            {
                ControlMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<ControlMessage>(line, ControlProtocol.JsonOptions);
                }
                catch (JsonException)
                {
                    message = null;
                }

                if (message is null)
                {
                    session.Send(new ControlReply { Ok = false, Error = "Непонятная команда." });
                    continue;
                }

                session.Send(Handle(session, message));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // лаунчер отключился
        }
        finally
        {
            Release(session);
            pipe.Dispose();
        }
    }

    private ControlReply Handle(Session session, ControlMessage message)
    {
        try
        {
            switch (message.Op)
            {
                case ControlProtocol.OpStart:
                    return Start(session, message);
                case ControlProtocol.OpStop:
                    lock (_ownerSync)
                    {
                        bool? graceful = null;
                        if (ReferenceEquals(_owner, session))
                        {
                            graceful = _core.Stop(GracefulStop);
                            _owner = null;
                            _guard.SetCore(null);
                        }

                        return new ControlReply { Id = message.Id, Ok = true, Graceful = graceful };
                    }
                case ControlProtocol.OpStatus:
                    lock (_ownerSync)
                    {
                        var mine = ReferenceEquals(_owner, session);
                        return new ControlReply
                        {
                            Id = message.Id,
                            Ok = true,
                            Running = _core.IsRunning,
                            CorePid = mine ? _core.ProcessId : null,
                            Version = typeof(ControlServer).Assembly.GetName().Version?.ToString(3),
                            Protocol = ControlProtocol.Revision,
                            Log = mine ? new(_core.LogTail) : null
                        };
                    }

                case ControlProtocol.OpGuard:
                {
                    var error = _guard.Set(session, message.Guard, out var notice, out var unguarded);
                    return error is null
                        ? new ControlReply { Id = message.Id, Ok = true, Warning = notice, Unguarded = unguarded.Count > 0 ? unguarded : null }
                        : new ControlReply { Id = message.Id, Ok = false, Error = "Не удалось включить блокировку трафика мимо VPN: " + error + "." };
                }

                default:
                    return new ControlReply { Id = message.Id, Ok = false, Error = $"Неизвестная команда «{message.Op}»." };
            }
        }
        catch (CoreStartException ex)
        {
            _log("Ядро не запустилось: " + ex.Message);
            return new ControlReply { Id = message.Id, Ok = false, Error = ex.Message, Log = new(ex.Log) };
        }
        catch (CoreConfigException ex)
        {
            return new ControlReply { Id = message.Id, Ok = false, Error = ex.Message };
        }
        catch (Exception ex)
        {
            _log("Ошибка команды " + message.Op + ": " + ex);
            return new ControlReply { Id = message.Id, Ok = false, Error = ex.Message };
        }
    }

    private ControlReply Start(Session session, ControlMessage message)
    {
        if (message.OwnerPipe is null || !OwnerPipePattern.IsMatch(message.OwnerPipe))
            return new ControlReply { Id = message.Id, Ok = false, Error = "Недопустимое имя канала лаунчера." };
        if (message.Request is null)
            return new ControlReply { Id = message.Id, Ok = false, Error = "Не переданы настройки ядра." };

        var api = new CoreApi(FreeLoopbackPort(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        var config = CoreConfigBuilder.Build(message.Request, api).ToJsonString();
        lock (_ownerSync)
        {
            if (_owner is not null && !ReferenceEquals(_owner, session) && _core.IsRunning)
                return new ControlReply { Id = message.Id, Ok = false, Error = "Ядро уже использует другая копия лаунчера (возможно, другого пользователя)." };

            // Новые настройки от того же лаунчера: перезапуск ядра.
            _core.Stop(GracefulStop);
            _owner = session;
            int pid;
            try
            {
                pid = _core.Start(config, message.OwnerPipe, session.ClientPid, ReadyTimeout);
            }
            catch
            {
                _guard.SetCore(null);
                throw;
            }

            // Приложениям «через ядро» сеть разрешена только через его адаптер — теперь новый.
            _guard.SetCore(NetworkGuard.CoreInterfaceLuid());
            _log($"Ядро запущено (PID {pid}) для лаунчера PID {session.ClientPid}");
            return new ControlReply
            {
                Id = message.Id,
                Ok = true,
                CorePid = pid,
                ApiPort = api.Port,
                ApiSecret = api.Secret,
                Protocol = ControlProtocol.Revision
            };
        }
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Лаунчер отключился: его ядро останавливается, его фильтры снимаются.</summary>
    private void Release(Session session)
    {
        lock (_ownerSync)
        {
            if (ReferenceEquals(_owner, session))
            {
                _owner = null;
                if (_core.IsRunning)
                {
                    _log($"Лаунчер PID {session.ClientPid} отключился, ядро останавливается");
                    _core.Stop(GracefulStop);
                }

                _guard.SetCore(null);
            }
        }

        _guard.Remove(session);
    }

    private void OnCoreExited(int exitCode, System.Collections.Generic.IReadOnlyList<string> log)
    {
        _log($"Ядро завершилось само, код {exitCode}");
        _guard.SetCore(null);
        Session? owner;
        lock (_ownerSync)
            owner = _owner;
        try
        {
            owner?.Send(new ControlReply { Event = ControlProtocol.EventCoreExited, Ok = false, ExitCode = exitCode, Log = new(log) });
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // лаунчер уже отключился
        }
    }

    /// <summary>Строка до \n не длиннее <see cref="ControlProtocol.MaxMessageBytes"/>; null — канал закрыт.</summary>
    private static string? ReadMessage(Stream pipe)
    {
        var buffer = new MemoryStream();
        while (true)
        {
            var b = pipe.ReadByte();
            if (b < 0)
                return buffer.Length == 0 ? null : throw new IOException("Канал закрыт посреди сообщения.");
            if (b == '\n')
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            if (buffer.Length >= ControlProtocol.MaxMessageBytes)
                throw new IOException("Слишком длинное сообщение.");
            buffer.WriteByte((byte)b);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_ownerSync)
            _owner = null;
    }

    private sealed class Session
    {
        private readonly Stream _pipe;
        private readonly object _write = new();

        public Session(Stream pipe, int clientPid)
        {
            _pipe = pipe;
            ClientPid = clientPid;
        }

        public int ClientPid { get; }

        public void Send(ControlReply reply)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, ControlProtocol.JsonOptions);
            lock (_write)
            {
                _pipe.Write(bytes);
                _pipe.WriteByte((byte)'\n');
                _pipe.Flush();
            }
        }
    }
}
