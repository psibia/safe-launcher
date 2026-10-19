using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace KillSwitchLauncher.Services;

/// <summary>Служба отказала или ядро не запустилось. Сообщение — для пользователя, Log — последние строки журнала ядра.</summary>
public sealed class CoreServiceException : Exception
{
    public CoreServiceException(string message, IReadOnlyList<string>? log = null) : base(message) =>
        Log = log ?? Array.Empty<string>();

    public IReadOnlyList<string> Log { get; }
}

/// <summary>Что ядро должно делать: подключения и какие приложения через какое из них идут.</summary>
public sealed class CoreStartRequest
{
    /// <summary>Подключения в формате outbound/endpoint sing-box, у каждого свой tag.</summary>
    public List<JsonObject> Outbounds { get; set; } = new();

    public List<CoreAppRoute> Apps { get; set; } = new();

    /// <summary>Давать адаптеру ядра адрес IPv6 (только если в сети есть IPv6).</summary>
    public bool Ipv6 { get; set; }

    /// <summary>«Защита от утечки DNS»: подменные адреса для службы DNS Windows (настройка на весь компьютер).</summary>
    public bool HideDns { get; set; }

    /// <summary>DNS-сервер внутри подключения (tag → IP), например из файла WireGuard.</summary>
    public Dictionary<string, string>? OutboundDns { get; set; }

    /// <summary>При <see cref="HideDns"/>: подключение (tag) для DNS-запросов, на которые ядро не отвечает подменным адресом; null — DNS компьютера.</summary>
    public string? DnsOutbound { get; set; }
}

/// <summary>Какие программы пускать в сеть только через VPN (см. SafeLauncher.Service/Protocol.cs, GuardRequest).</summary>
public sealed class GuardRequest
{
    public List<GuardRule> Apps { get; set; } = new();

    /// <summary>DNS всего компьютера — только через эти адаптеры (IfIndex); null — DNS не трогать.</summary>
    public List<int>? DnsInterfaces { get; set; }

    /// <summary>Программы, у которых закрыт и их собственный DNS мимо <see cref="DnsInterfaces"/> (когда другого фильтра нет).</summary>
    public List<string>? DnsPaths { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Apps.Count == 0 && DnsInterfaces is not { Count: > 0 };
}

/// <summary>Программы приложения: сеть только через адаптер ядра (ViaCore) или через эти адаптеры системного VPN.</summary>
public sealed class GuardRule
{
    public List<string> Paths { get; set; } = new();

    public bool ViaCore { get; set; }

    public List<int> Interfaces { get; set; } = new();

    /// <summary>Закрытые порты на 127.0.0.1: системный прокси другой VPN-программы (<see cref="SystemProxy"/>).</summary>
    public List<int>? LoopbackPorts { get; set; }
}

/// <summary>Фильтры поставлены. Notice — замечание службы; Unguarded — пути из запроса, которые служба не смогла проверить
/// (сетевой диск и т. п.) и поэтому не ограничила.</summary>
public sealed record GuardReply(string? Notice, IReadOnlyList<string> Unguarded)
{
    public static readonly GuardReply Empty = new(null, Array.Empty<string>());
}

/// <summary>
/// Приложение: процессы с меткой Tag идут через подключение Outbound, кроме доменов DirectDomains. Процессы программ
/// Programs без метки (копия, запущенная мимо лаунчера) идут так же, а с BlockOutside остаются без сети.
/// </summary>
public sealed class CoreAppRoute
{
    public string Tag { get; set; } = string.Empty;

    public string Outbound { get; set; } = string.Empty;

    public List<string> DirectDomains { get; set; } = new();

    /// <summary>Программы приложения для процессов без метки; null — такие процессы идут напрямую (служба до ревизии 8 так и делает).</summary>
    public List<CoreProgram>? Programs { get; set; }

    public bool BlockOutside { get; set; }
}

/// <summary>Программа приложения: exe с этим именем в папке и, если Subfolders, во вложенных папках.</summary>
public sealed class CoreProgram
{
    public string Folder { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Subfolders { get; set; }
}

/// <summary>Обновление лаунчера через службу (SafeLauncher.Service/Protocol.cs, UpdateRequest): скачанные установщик и подпись.</summary>
public sealed class ServiceUpdateRequest
{
    public string SetupPath { get; set; } = string.Empty;

    public string SignaturePath { get; set; } = string.Empty;

    /// <summary>Для updateStarted: номер обновления из status.json.</summary>
    public string? JobId { get; set; }
}

/// <summary>Запрос к журналам службы (SafeLauncher.Service/Protocol.cs, LogRequest).</summary>
public sealed class ServiceLogRequest
{
    public string? Storage { get; set; }

    public int? RetentionDays { get; set; }

    public bool? HideAddresses { get; set; }

    public DateTime? DetailedUntilUtc { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public List<string>? Sources { get; set; }

    public string? Cursor { get; set; }

    public int? MaxBytes { get; set; }

    public string? Text { get; set; }
}

/// <summary>Ответ журналов службы (SafeLauncher.Service/Protocol.cs, LogReply).</summary>
public sealed class ServiceLogReply
{
    public string? MaskKey { get; set; }

    public long? ServiceBytes { get; set; }

    public long? CoreBytes { get; set; }

    public long? Dropped { get; set; }

    public bool? Detailed { get; set; }

    public string? Storage { get; set; }

    public List<string>? Lines { get; set; }

    public string? Cursor { get; set; }

    public List<ServiceLogFile>? Files { get; set; }

    public long? Freed { get; set; }
}

/// <summary>Файл рядом с журналами службы: очищенный конфиг ядра или описание падения службы.</summary>
public sealed class ServiceLogFile
{
    public string Name { get; set; } = string.Empty;

    public DateTime Utc { get; set; }

    public string Content { get; set; } = string.Empty;
}

/// <summary>Ядро запущено: его PID и управление на 127.0.0.1 (порт и ключ, свои на каждый запуск).</summary>
/// <param name="CheckPort">Прокси для проверки подключений через ядро (служба с протоколом 7), иначе null.</param>
public sealed record CoreStarted(int Pid, int? ApiPort, string? ApiSecret, int? CheckPort = null);

/// <summary>Состояние ядра по словам службы.</summary>
/// <param name="Protocol">Что умеет служба (см. <see cref="CoreServiceClient.RequiredProtocol"/>); у служб до него — 1.</param>
public sealed record CoreStatus(bool Running, int? CorePid, string? ServiceVersion, IReadOnlyList<string> Log, int Protocol = 1);

/// <summary>Служба Windows в системе: установлена ли, работает ли и её PID.</summary>
public readonly record struct CoreServiceState(bool Installed, bool Running, int ProcessId);

/// <summary>
/// Связь лаунчера со службой «Безопасный лаунчер — сеть» (SafeLauncher.Service), которая от имени системы запускает
/// ядро VPN. Пока соединение открыто, ядро работает; закрылось соединение (лаунчер вышел или упал) — служба
/// останавливает ядро. Сообщения — по строке JSON, как описано в SafeLauncher.Service/Protocol.cs.
///
/// Перед командами проверяется, что на том конце канала именно служба (PID сервера канала совпадает с PID службы):
/// иначе чужая программа могла бы занять имя канала и получить ключи подключений.
/// </summary>
public sealed class CoreServiceClient : IDisposable
{
    public const string ServiceName = "SafeLauncherNet";
    public const string PipeName = "SafeLauncher.Service";

    /// <summary>Сколько ждать ответа на start: служба ждёт ядро до 30 секунд и может сначала остановить прежнее.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Сколько ждать ответа на update: служба копирует и проверяет установщик и сохраняет текущую версию для отката.</summary>
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(3);

    /// <summary>С какой ревизии служба ставит обновления сама (команда update).</summary>
    public const int UpdateProtocol = 9;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly NamedPipeClientStream _pipe;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Reply>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private int _nextId;
    private int _closing;
    private int _disposed;

    private CoreServiceClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _ = Task.Run(ReadLoop);
    }

    /// <summary>
    /// Самая старая служба, с которой работает этот лаунчер (ControlProtocol.Revision в службе): 8 — ядро узнаёт копии
    /// приложений мимо лаунчера (Programs, BlockOutside). Служба старше молча пропустила бы эти поля, и копии шли бы напрямую.
    /// </summary>
    public const int RequiredProtocol = 8;

    public const string OutdatedServiceMessage =
        "Установлена старая версия службы «Безопасный лаунчер — сеть»: она не умеет защищать приложения так, как нужно " +
        "этой версии лаунчера. Обновите службу: переустановите лаунчер.";

    /// <summary>Папка службы и ядра: только латиница в пути и запись только для администраторов (см. core/README.md).</summary>
    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SafeLauncher", "core");

    /// <summary>
    /// Ядро завершилось само: код выхода, последние строки журнала и PID ядра (null — служба его не сообщает). Вызывается из
    /// фонового потока.
    /// </summary>
    public event Action<int, IReadOnlyList<string>, int?>? CoreExited;

    /// <summary>Связь со службой пропала (служба остановлена или упала); ядро при этом тоже остановлено.</summary>
    public event Action? Disconnected;

    public bool IsConnected => !_closed.IsCancellationRequested && _pipe.IsConnected;

    /// <summary>Подключиться к службе. Не установлена или не отвечает — <see cref="CoreServiceException"/>.</summary>
    public static async Task<CoreServiceClient> ConnectAsync(TimeSpan timeout, CancellationToken cancellation = default)
    {
        var state = QueryService();
        if (!state.Installed)
            throw new CoreServiceException("Служба «Безопасный лаунчер — сеть» не установлена. Переустановите лаунчер.");
        if (!state.Running)
            throw new CoreServiceException("Служба «Безопасный лаунчер — сеть» не запущена.");

        var pipe = await OpenPipeAsync(timeout, cancellation).ConfigureAwait(false);
        try
        {
            // Служба могла перезапуститься между проверками: сверяем с её текущим PID.
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != QueryService().ProcessId)
                throw new CoreServiceException("Канал службы занят другой программой. Перезагрузите компьютер.");
            return new CoreServiceClient(pipe);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Открывает канал службы. Права запрашиваются явно (чтение и запись данных): обычный NamedPipeClientStream просит
    /// GENERIC_WRITE, а в него входит право создавать свои экземпляры канала, которого у пользователей нет и быть не должно.
    /// Служба получает право только узнать, кто подключился, но не действовать от имени пользователя.
    /// </summary>
    private static async Task<NamedPipeClientStream> OpenPipeAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var handle = CreateFileW(@"\\.\pipe\" + PipeName, PipeAccess, 0, IntPtr.Zero, OpenExisting,
                FileFlagOverlapped | SecuritySqosPresent | SecurityIdentification, IntPtr.Zero);
            if (!handle.IsInvalid)
                return new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, handle);

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == ErrorAccessDenied)
                throw new CoreServiceException("Нет доступа к службе «Безопасный лаунчер — сеть».");
            if (error != ErrorPipeBusy && error != ErrorFileNotFound)
                throw new CoreServiceException($"Не удалось связаться со службой «Безопасный лаунчер — сеть» (ошибка Windows {error}).");
            if (DateTime.UtcNow >= deadline)
                throw new CoreServiceException("Служба «Безопасный лаунчер — сеть» не отвечает.");
            await Task.Delay(100, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Запустить ядро (или перезапустить с новыми настройками). Возвращает PID ядра.</summary>
    /// <param name="ownerPipe">Имя канала, по которому ядро спрашивает лаунчер «чей процесс» (<see cref="OwnerPipeServer"/>).</param>
    public async Task<int> StartAsync(string ownerPipe, CoreStartRequest request, CancellationToken cancellation = default) =>
        (await StartCoreAsync(ownerPipe, request, cancellation).ConfigureAwait(false)).Pid;

    /// <summary>Запустить ядро (или перезапустить с новыми настройками): PID и адрес управления ядром.</summary>
    public async Task<CoreStarted> StartCoreAsync(string ownerPipe, CoreStartRequest request, CancellationToken cancellation = default)
    {
        var reply = await SendAsync(new Message { Op = "start", OwnerPipe = ownerPipe, Request = request }, StartTimeout, cancellation)
            .ConfigureAwait(false);
        var pid = reply.CorePid ?? throw new CoreServiceException("Служба не сообщила, запущено ли ядро.");
        return new CoreStarted(pid, reply.ApiPort, reply.ApiSecret, reply.CheckPort);
    }

    /// <summary>Остановить ядро. true — оно закрылось само и убрало за собой адаптер и маршруты.</summary>
    public async Task<bool> StopAsync(CancellationToken cancellation = default)
    {
        var reply = await SendAsync(new Message { Op = "stop" }, StartTimeout, cancellation).ConfigureAwait(false);
        return reply.Graceful ?? true;
    }

    /// <summary>
    /// Заменить набор фильтров этого соединения (пустой — снять). Возвращает замечание службы (например, какие
    /// программы она не ограничивает) и непроверенные пути. Не удалось — <see cref="CoreServiceException"/>.
    /// </summary>
    public async Task<GuardReply> GuardAsync(GuardRequest request, CancellationToken cancellation = default)
    {
        var reply = await SendAsync(new Message { Op = "guard", Guard = request }, CommandTimeout, cancellation).ConfigureAwait(false);
        return new GuardReply(reply.Warning, reply.Unguarded ?? new List<string>());
    }

    /// <summary>
    /// Команда журналов службы (logConfig, logRead, logMark, logClear, logFiles; служба Revision 5).
    /// Старая служба отвечает «неизвестная команда» — <see cref="CoreServiceException"/>.
    /// </summary>
    public async Task<ServiceLogReply> LogsAsync(string op, ServiceLogRequest? request, CancellationToken cancellation = default, string? cid = null)
    {
        var reply = await SendAsync(new Message { Op = op, Logs = request, Cid = cid }, CommandTimeout, cancellation).ConfigureAwait(false);
        return reply.Logs ?? new ServiceLogReply();
    }

    /// <summary>
    /// Передать службе обновление (служба Revision 9): она проверит подпись и версию, сохранит текущую версию для отката
    /// и начнёт установку, когда лаунчер закроется. Возвращает устанавливаемую версию. Отказ — <see cref="CoreServiceException"/>
    /// с объяснением службы; при этом ничего не изменено.
    /// </summary>
    public async Task<string> UpdateAsync(string setupPath, string signaturePath, CancellationToken cancellation = default)
    {
        var reply = await SendAsync(new Message { Op = "update", Update = new ServiceUpdateRequest { SetupPath = setupPath, SignaturePath = signaturePath } },
            UpdateTimeout, cancellation).ConfigureAwait(false);
        return reply.Version ?? throw new CoreServiceException("Служба не сообщила, какую версию ставит.");
    }

    /// <summary>
    /// Сообщить службе (ревизия 10), что эта версия запустилась после обновления: помощник обновления ждёт этого и без
    /// сообщения вернёт прежнюю версию. Отказ — <see cref="CoreServiceException"/> с объяснением службы.
    /// </summary>
    public async Task ConfirmUpdateStartedAsync(string jobId, CancellationToken cancellation = default) =>
        await SendAsync(new Message { Op = "updateStarted", Update = new ServiceUpdateRequest { JobId = jobId } }, CommandTimeout, cancellation)
            .ConfigureAwait(false);

    public async Task<CoreStatus> StatusAsync(CancellationToken cancellation = default)
    {
        var reply = await SendAsync(new Message { Op = "status" }, CommandTimeout, cancellation).ConfigureAwait(false);
        return new CoreStatus(reply.Running == true, reply.CorePid, reply.Version, reply.Log ?? new List<string>(), reply.Protocol ?? 1);
    }

    private async Task<Reply> SendAsync(Message message, TimeSpan timeout, CancellationToken cancellation)
    {
        if (!IsConnected)
            throw new CoreServiceException("Нет связи со службой «Безопасный лаунчер — сеть».");

        message.Id = Interlocked.Increment(ref _nextId);
        message.Cid ??= TechLog.CurrentCid;
        var waiter = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[message.Id] = waiter;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            await _write.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                await _pipe.WriteAsync(bytes, cancellation).ConfigureAwait(false);
                await _pipe.WriteAsync(new[] { (byte)'\n' }, cancellation).ConfigureAwait(false);
                await _pipe.FlushAsync(cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // ObjectDisposedException — связь закрылась, пока команда ждала очереди. Обрыв замечен здесь раньше,
                // чем при чтении: сообщить о нём должны мы, чтение его уже не увидит.
                if (Close())
                    _ = Task.Run(RaiseDisconnected);
                throw new CoreServiceException("Связь со службой «Безопасный лаунчер — сеть» прервалась.");
            }
            finally
            {
                _write.Release();
            }

            Reply reply;
            try
            {
                reply = await waiter.Task.WaitAsync(timeout, cancellation).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new CoreServiceException("Служба «Безопасный лаунчер — сеть» не ответила вовремя.");
            }

            if (!reply.Ok)
                throw new CoreServiceException(reply.Error ?? "Служба отказала без объяснения.", reply.Log);
            return reply;
        }
        finally
        {
            _pending.TryRemove(message.Id, out _);
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            using var reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            while (!_closed.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_closed.Token).ConfigureAwait(false);
                if (line is null)
                    break;

                Reply? reply;
                try
                {
                    reply = JsonSerializer.Deserialize<Reply>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (reply is null)
                    continue;
                if (reply.Event == "coreExited")
                    CoreExited?.Invoke(reply.ExitCode ?? -1, reply.Log ?? new List<string>(), reply.CorePid);
                else if (reply.Id is { } id && _pending.TryGetValue(id, out var waiter))
                    waiter.TrySetResult(reply);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // канал закрыт
        }

        if (Close())
            RaiseDisconnected();
    }

    /// <summary>Связь пропала сама (не <see cref="Dispose"/>).</summary>
    private void RaiseDisconnected()
    {
        if (Volatile.Read(ref _disposed) == 0)
            Disconnected?.Invoke();
    }

    /// <summary>Закрыть канал. true — закрыли именно сейчас (раньше он был открыт).</summary>
    private bool Close()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
            return false;
        _closed.Cancel();
        foreach (var waiter in _pending.Values)
            waiter.TrySetException(new CoreServiceException("Связь со службой «Безопасный лаунчер — сеть» прервалась."));
        _pipe.Dispose();
        return true;
    }

    /// <summary>Закрыть связь: служба остановит ядро, если оно было запущено этим лаунчером.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Close();
    }

    /// <summary>Установлена ли служба, работает ли она и её PID (для проверки канала).</summary>
    public static CoreServiceState QueryService()
    {
        using var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager.IsInvalid)
            return default;
        using var service = OpenServiceW(manager, ServiceName, ServiceQueryStatus);
        if (service.IsInvalid)
            return default;

        var status = new ServiceStatusProcess();
        if (!QueryServiceStatusEx(service, ScStatusProcessInfo, ref status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
            return new CoreServiceState(true, false, 0);
        var running = status.CurrentState == ServiceRunning && status.ProcessId != 0;
        return new CoreServiceState(true, running, running ? (int)status.ProcessId : 0);
    }

    // ---- сообщения (те же поля, что в SafeLauncher.Service/Protocol.cs)

    internal sealed class Message
    {
        public int Id { get; set; }

        public string Op { get; set; } = string.Empty;

        public string? OwnerPipe { get; set; }

        public CoreStartRequest? Request { get; set; }

        public GuardRequest? Guard { get; set; }

        /// <summary>Id операции лаунчера: служба пишет его в свои строки журнала по этой команде.</summary>
        public string? Cid { get; set; }

        public ServiceLogRequest? Logs { get; set; }

        public ServiceUpdateRequest? Update { get; set; }
    }

    internal sealed class Reply
    {
        public int? Id { get; set; }

        public string? Event { get; set; }

        public bool Ok { get; set; }

        public string? Error { get; set; }

        public int? CorePid { get; set; }

        public bool? Running { get; set; }

        public string? Version { get; set; }

        public int? Protocol { get; set; }

        public int? ExitCode { get; set; }

        public bool? Graceful { get; set; }

        public int? ApiPort { get; set; }

        public string? ApiSecret { get; set; }

        public int? CheckPort { get; set; }

        public List<string>? Log { get; set; }

        public string? Warning { get; set; }

        public List<string>? Unguarded { get; set; }

        public ServiceLogReply? Logs { get; set; }
    }

    // ---- Windows

    private const uint FileReadData = 0x0001;
    private const uint FileWriteData = 0x0002;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileWriteAttributes = 0x0100;
    private const uint Synchronize = 0x00100000;
    private const uint PipeAccess = FileReadData | FileWriteData | FileReadAttributes | FileWriteAttributes | Synchronize;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint SecuritySqosPresent = 0x00100000;
    private const uint SecurityIdentification = 0x00010000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorPipeBusy = 231;

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    private sealed class ScHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ScHandle() : base(true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ScHandle OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ScHandle OpenServiceW(ScHandle manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(ScHandle service, int infoLevel, ref ServiceStatusProcess status, int size, out int needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int serverProcessId);
}
