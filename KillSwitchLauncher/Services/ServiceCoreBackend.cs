using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Ядро VPN через службу «Безопасный лаунчер — сеть»: связь со службой, канал «чей процесс» и проверка серверов
/// через управление ядром. Одна связь на всё время работы лаунчера; пропала — <see cref="Lost"/>, следующий запуск
/// подключится заново.
/// </summary>
public sealed class ServiceCoreBackend : ICoreBackend
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly OwnerPipeServer _owner;
    private readonly object _sync = new();
    private CoreServiceClient? _client;
    private CoreApiClient? _api;
    private int _lost;

    /// <param name="resolve">PID → метка приложения для ядра или null («не наш», идёт напрямую). Любой поток.</param>
    public ServiceCoreBackend(Func<uint, string?> resolve) => _owner = new OwnerPipeServer(resolve);

    public event Action<string, IReadOnlyList<string>>? Lost;

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

            client.CoreExited += (code, log) => RaiseLost($"Ядро VPN неожиданно завершилось (код {code}).", log);
            client.Disconnected += () => RaiseLost("Пропала связь со службой «Безопасный лаунчер — сеть».", Array.Empty<string>());
            lock (_sync)
                _client = client;
        }

        var started = await client.StartCoreAsync(_owner.Name, request, cancellation).ConfigureAwait(false);
        _owner.CorePid = started.Pid;
        lock (_sync)
        {
            _api?.Dispose();
            _api = started.ApiPort is { } port && started.ApiSecret is { } secret ? new CoreApiClient(port, secret) : null;
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

    public async Task<IReadOnlyList<string>> LogTailAsync(CancellationToken cancellation)
    {
        CoreServiceClient? client;
        lock (_sync)
            client = _client;
        if (client is not { IsConnected: true })
            return Array.Empty<string>();
        return (await client.StatusAsync(cancellation).ConfigureAwait(false)).Log;
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
            _client?.Dispose(); // служба остановит ядро
            _client = null;
        }

        _owner.Dispose();
    }
}
