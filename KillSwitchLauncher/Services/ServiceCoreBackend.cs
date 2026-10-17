using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Ядро VPN через службу «Безопасный лаунчер — сеть»: связь со службой, канал «чей процесс», проверка серверов и
/// список соединений через управление ядром. Одна связь на всё время работы лаунчера; пропала — <see cref="Lost"/>,
/// следующий запуск подключится заново.
/// </summary>
public sealed class ServiceCoreBackend : ICoreBackend
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly OwnerPipeServer _owner;
    private readonly object _sync = new();
    private CoreServiceClient? _client;
    private CoreApiClient? _api;
    private CoreCheckProxy? _check;
    private int _lost;
    private int _corePid;
    private int _starting;

    /// <param name="resolve">PID → метка приложения для ядра или null («не наш», идёт напрямую). Любой поток.</param>
    public ServiceCoreBackend(Func<uint, string?> resolve) => _owner = new OwnerPipeServer(resolve);

    public event Action<string, IReadOnlyList<string>>? Lost;

    public CoreCheckProxy? CheckProxy
    {
        get
        {
            lock (_sync)
                return _check;
        }
    }

    public async Task StartAsync(CoreStartRequest request, CancellationToken cancellation)
    {
        CoreServiceClient? client;
        lock (_sync)
            client = _client;
        if (client is null || !client.IsConnected)
        {
            client?.Dispose();
            client = await CoreServiceClient.ConnectAsync(ConnectTimeout, cancellation).ConfigureAwait(false);
            try
            {
                // Старая служба запустила бы ядро без управления: сервер «не отвечал» бы при любом подключении.
                var status = await client.StatusAsync(cancellation).ConfigureAwait(false);
                if (status.Protocol < CoreServiceClient.RequiredProtocol)
                    throw new CoreServiceException(CoreServiceClient.OutdatedServiceMessage);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            client.CoreExited += OnCoreExited;
            client.Disconnected += () => RaiseLost("Пропала связь со службой «Безопасный лаунчер — сеть».", Array.Empty<string>());
            lock (_sync)
                _client = client;
        }

        // Новое ядро подключается к каналу «чей процесс» ещё до ответа службы: PID прежнего ядра его бы не пустил.
        _owner.CorePid = 0;
        Volatile.Write(ref _corePid, 0);
        Interlocked.Increment(ref _starting);
        CoreStarted started;
        try
        {
            started = await client.StartCoreAsync(_owner.Name, request, cancellation).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _starting);
        }

        _owner.CorePid = started.Pid;
        Volatile.Write(ref _corePid, started.Pid);
        lock (_sync)
        {
            _api?.Dispose();
            _api = started.ApiPort is { } port && started.ApiSecret is { } secret ? new CoreApiClient(port, secret) : null;
            _check = started.CheckPort is { } checkPort && started.ApiSecret is { } checkSecret ? new CoreCheckProxy(checkPort, checkSecret) : null;
        }
    }

    public async Task StopAsync(CancellationToken cancellation)
    {
        CoreServiceClient? client;
        lock (_sync)
        {
            client = _client;
            _api?.Dispose();
            _api = null;
            _check = null;
        }

        if (client is { IsConnected: true })
            await client.StopAsync(cancellation).ConfigureAwait(false);
        _owner.CorePid = 0;
    }

    public async Task<CoreProbe> ProbeAsync(string outboundTag, TimeSpan timeout, CancellationToken cancellation)
    {
        CoreApiClient? api;
        lock (_sync)
            api = _api;
        return api is null
            ? CoreProbe.Failed("служба не сообщила, как управлять ядром (старая версия службы?)")
            : await api.ProbeAsync(outboundTag, timeout, cancellation).ConfigureAwait(false);
    }

    public async Task<CoreConnectionList> ConnectionsAsync(TimeSpan timeout, CancellationToken cancellation)
    {
        CoreApiClient? api;
        lock (_sync)
            api = _api;
        return api is null
            ? CoreConnectionList.Failed("служба не сообщила, как управлять ядром (старая версия службы?)")
            : await api.ConnectionsAsync(timeout, cancellation).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> LogTailAsync(CancellationToken cancellation)
    {
        CoreServiceClient? client;
        lock (_sync)
            client = _client;
        if (client is not { IsConnected: true })
            return Array.Empty<string>();
        return (await client.StatusAsync(cancellation).ConfigureAwait(false)).Log;
    }

    /// <summary>
    /// Сообщение о прежнем ядре (упало перед перезапуском) приходит, когда служба уже запускает или запустила новое: такое
    /// не значит, что пропало новое ядро.
    /// </summary>
    private void OnCoreExited(int code, IReadOnlyList<string> log, int? pid)
    {
        var current = Volatile.Read(ref _corePid);
        if (pid is { } exited ? exited != current : Volatile.Read(ref _starting) > 0)
        {
            TechLog.Info(LogAreas.Network, LogCodes.CoreExit, $"Прежнее ядро{(pid is { } p ? $" (PID {p})" : string.Empty)} завершилось с кодом {code}: " +
                                                              "служба уже запускает новое");
            return;
        }

        RaiseLost($"Ядро VPN неожиданно завершилось (код {code}).", log);
    }

    private void RaiseLost(string reason, IReadOnlyList<string> log)
    {
        if (Interlocked.Exchange(ref _lost, 1) == 0)
            Lost?.Invoke(reason, log);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _lost, 1); // закрываемся сами: это не потеря
        lock (_sync)
        {
            _api?.Dispose();
            _api = null;
            _check = null;
            _client?.Dispose(); // служба остановит ядро
            _client = null;
        }

        _owner.Dispose();
    }
}
