using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using SafeLauncher.Logging;

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
    private readonly HashSet<Session> _active = new();
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
                ServiceLog.Error(LogAreas.Control, LogCodes.CmdError, null, "Канал управления: " + ex.Message);
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
                    ServiceLog.Warn(LogAreas.Control, LogCodes.CmdError, null, $"Непонятная команда от PID {session.ClientPid}");
                    session.Send(new ControlReply { Ok = false, Error = "Непонятная команда." });
                    continue;
                }

                ServiceLog.CurrentCid = message.Cid;
                try
                {
                    session.Send(Handle(session, message));
                }
                finally
                {
                    ServiceLog.CurrentCid = null;
                }
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
        var watch = Stopwatch.StartNew();
        try
        {
            if (message.Op.StartsWith("log", StringComparison.Ordinal))
            {
                var reply = ServiceLog.Handle(message, session.Sid, session.ClientPid);
                ServiceLog.Debug(LogAreas.Control, LogCodes.CmdLog, message.Cid,
                    $"{message.Op} от PID {session.ClientPid}: {(reply.Ok ? "ок" : reply.Error)}, {watch.ElapsedMilliseconds} мс");
                return reply;
            }

            Activate(session);
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
                            graceful = StopCore("команда лаунчера stop", message.Cid);
                            _owner = null;
                            ServiceLog.CoreOwner(session.Sid, session.ClientPid, owns: false);
                            _guard.SetCore(null);
                        }
                        else
                        {
                            ServiceLog.Info(LogAreas.Control, LogCodes.CmdStop, message.Cid, $"stop от PID {session.ClientPid}: ядро не его, ничего не делаем");
                        }

                        return new ControlReply { Id = message.Id, Ok = true, Graceful = graceful };
                    }
                case ControlProtocol.OpStatus:
                    lock (_ownerSync)
                    {
                        var mine = ReferenceEquals(_owner, session);
                        ServiceLog.Debug(LogAreas.Control, LogCodes.CmdStatus, message.Cid,
                            $"status от PID {session.ClientPid}: ядро {(_core.IsRunning ? "работает" : "не работает")}{(mine ? ", его" : string.Empty)}");
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
                    ServiceLog.Write(error is null ? LogLevel.Info : LogLevel.Error, LogAreas.Control, error is null ? LogCodes.CmdGuard : LogCodes.GuardFail, message.Cid,
                        $"guard от PID {session.ClientPid}: {DescribeGuard(message.Guard)}; {(error ?? "применено")}" +
                        (notice is null ? string.Empty : "; предупреждение: " + notice) +
                        (unguarded.Count > 0 ? "; не проверены: " + string.Join(", ", unguarded) : string.Empty) +
                        $", {watch.ElapsedMilliseconds} мс");
                    return error is null
                        ? new ControlReply { Id = message.Id, Ok = true, Warning = notice, Unguarded = unguarded.Count > 0 ? unguarded : null }
                        : new ControlReply { Id = message.Id, Ok = false, Error = "Не удалось включить блокировку трафика мимо VPN: " + error + "." };
                }

                default:
                    ServiceLog.Warn(LogAreas.Control, LogCodes.CmdError, message.Cid, $"Неизвестная команда «{message.Op}» от PID {session.ClientPid}");
                    return new ControlReply { Id = message.Id, Ok = false, Error = $"Неизвестная команда «{message.Op}»." };
            }
        }
        catch (CoreStartException ex)
        {
            _log("Ядро не запустилось: " + ex.Message);
            ServiceLog.Error(LogAreas.Core, LogCodes.CoreFail, message.Cid, $"Ядро не запустилось за {watch.ElapsedMilliseconds} мс: {ex.Message}");
            ServiceLog.CoreGone();
            return new ControlReply { Id = message.Id, Ok = false, Error = ex.Message, Log = new(ex.Log) };
        }
        catch (CoreConfigException ex)
        {
            ServiceLog.Warn(LogAreas.Control, LogCodes.CmdReject, message.Cid, $"{message.Op} от PID {session.ClientPid} отклонена: {ex.Message}");
            return new ControlReply { Id = message.Id, Ok = false, Error = ex.Message };
        }
        catch (Exception ex)
        {
            _log("Ошибка команды " + message.Op + ": " + ex);
            ServiceLog.Error(LogAreas.Control, LogCodes.CmdError, message.Cid, "Ошибка команды " + message.Op + ": " + ex);
            return new ControlReply { Id = message.Id, Ok = false, Error = ex.Message };
        }
    }

    /// <summary>Лаунчер начал работать со службой (первая команда, кроме журналов): с этого момента его строки службы — его.</summary>
    private void Activate(Session session)
    {
        if (session.Active)
            return;
        session.Active = true;
        lock (_ownerSync)
            _active.Add(session);
        ServiceLog.ClientActive(session.Sid, session.ClientPid);
        ServiceLog.Info(LogAreas.Control, LogCodes.ClientConnect, null, $"Лаунчер PID {session.ClientPid} подключился");
    }

    /// <summary>Остановить ядро с записью причины и времени. true — закрылось само (мягко).</summary>
    private bool StopCore(string reason, string? cid)
    {
        var pid = _core.ProcessId;
        if (pid is null)
            return true;
        var watch = Stopwatch.StartNew();
        var graceful = _core.Stop(GracefulStop);
        ServiceLog.CoreGone();
        ServiceLog.Info(LogAreas.Core, LogCodes.CoreStop, cid,
            $"Ядро PID {pid} остановлено ({reason}): {(graceful ? "мягко" : "принудительно")}, {watch.ElapsedMilliseconds} мс");
        return graceful;
    }

    private static string DescribeGuard(GuardRequest? guard)
    {
        if (guard is null)
            return "снять фильтры";
        var apps = guard.Apps ?? new List<GuardRule>();
        return $"приложений {apps.Count} (через ядро {apps.Count(a => a.ViaCore)}, через адаптеры {apps.Count(a => !a.ViaCore && a.Interfaces.Count > 0)}, " +
               $"без сети {apps.Count(a => !a.ViaCore && a.Interfaces.Count == 0)}), путей {apps.Sum(a => a.Paths.Count)}" +
               (apps.Any(a => a.LoopbackPorts is { Count: > 0 }) ? ", закрытые порты прокси есть" : string.Empty) +
               (guard.DnsInterfaces is { Count: > 0 } dns ? $", DNS через адаптеры [{string.Join(",", dns)}]" : string.Empty) +
               (guard.DnsPaths is { Count: > 0 } dnsPaths ? $", DNS программ {dnsPaths.Count}" : string.Empty);
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
            {
                ServiceLog.Warn(LogAreas.Control, LogCodes.CmdReject, message.Cid, $"start от PID {session.ClientPid}: ядро занято лаунчером PID {_owner.ClientPid}");
                return new ControlReply { Id = message.Id, Ok = false, Error = "Ядро уже использует другая копия лаунчера (возможно, другого пользователя)." };
            }

            // Новые настройки от того же лаунчера: перезапуск ядра.
            var restart = _core.IsRunning;
            StopCore("новые настройки от лаунчера, перезапуск", message.Cid);
            _owner = session;
            ServiceLog.CoreOwner(session.Sid, session.ClientPid, owns: true);
            ServiceLog.CoreStarting(message.Request, config, api, message.Cid,
                (restart ? "перезапуск" : "запуск") + $" по команде лаунчера PID {session.ClientPid}");
            var watch = Stopwatch.StartNew();
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

            ServiceLog.CoreStarted(pid, api, watch.Elapsed, message.Cid);
            // Приложениям «через ядро» сеть разрешена только через его адаптер — теперь новый.
            var luid = NetworkGuard.CoreInterfaceLuid();
            ServiceLog.Info(LogAreas.Net, LogCodes.NetSnapshot, message.Cid, "Адаптер ядра: " + (luid is { } l ? "LUID " + l : "не найден") + "; " + NetworkSnapshot.Describe());
            _guard.SetCore(luid);
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
        bool last;
        lock (_ownerSync)
        {
            if (ReferenceEquals(_owner, session))
            {
                _owner = null;
                if (_core.IsRunning)
                {
                    _log($"Лаунчер PID {session.ClientPid} отключился, ядро останавливается");
                    StopCore($"лаунчер PID {session.ClientPid} отключился", null);
                }

                ServiceLog.CoreOwner(session.Sid, session.ClientPid, owns: false);
                _guard.SetCore(null);
            }

            last = _active.Remove(session) && _active.Count == 0;
        }

        _guard.Remove(session);
        if (session.Active)
        {
            ServiceLog.Info(LogAreas.Control, LogCodes.ClientDisconnect, null, $"Лаунчер PID {session.ClientPid} отключился");
            ServiceLog.ClientGone(session.Sid, session.ClientPid);
        }
        if (last)
            ServiceLog.AllClientsGone();
    }

    private void OnCoreExited(int exitCode, System.Collections.Generic.IReadOnlyList<string> log)
    {
        _log($"Ядро завершилось само, код {exitCode}");
        ServiceLog.CoreGone();
        ServiceLog.Error(LogAreas.Core, LogCodes.CoreExit, null, $"Ядро завершилось само, код {exitCode}");
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

        /// <summary>Пользователь Windows клиента (по процессу): чьи периоды журнала он может читать.</summary>
        public string? Sid => _sid ??= Native.ProcessUserSid(ClientPid);

        /// <summary>Клиент прислал команду, кроме журналов (настоящий лаунчер, а не короткое подключение за журналами).</summary>
        public bool Active { get; set; }

        private string? _sid;

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
