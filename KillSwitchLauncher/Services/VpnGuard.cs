using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KillSwitchLauncher.Services;

/// <summary>Фильтры Windows через службу, как их видит <see cref="VpnGuard"/>: служба или подмена в тестах.</summary>
public interface IGuardBackend : IDisposable
{
    bool IsConnected { get; }

    /// <summary>Заменить набор фильтров. Возвращает замечание службы и непроверенные пути. Ошибка — <see cref="CoreServiceException"/>.</summary>
    Task<GuardReply> ApplyAsync(GuardRequest request, CancellationToken cancellation);

    /// <summary>Связь со службой пропала: её фильтры сняты. Любой поток.</summary>
    event Action? Lost;
}

/// <summary>Своё соединение со службой «Безопасный лаунчер — сеть» только для фильтров: ядро живёт отдельно от них.</summary>
public sealed class ServiceGuardBackend : IGuardBackend
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private CoreServiceClient? _client;
    private bool _disposed;
    private int _lost;

    public event Action? Lost;

    public bool IsConnected
    {
        get
        {
            lock (_sync)
                return _client is { IsConnected: true };
        }
    }

    public async Task<GuardReply> ApplyAsync(GuardRequest request, CancellationToken cancellation)
    {
        CoreServiceClient? client;
        lock (_sync)
            client = _client;
        if (client is null)
        {
            client = await CoreServiceClient.ConnectAsync(ConnectTimeout, cancellation).ConfigureAwait(false);
            try
            {
                var status = await client.StatusAsync(cancellation).ConfigureAwait(false);
                if (status.Protocol < CoreServiceClient.RequiredProtocol)
                    throw new CoreServiceException(CoreServiceClient.OutdatedServiceMessage);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            client.Disconnected += () =>
            {
                if (Interlocked.Exchange(ref _lost, 1) == 0)
                    Lost?.Invoke();
            };
            lock (_sync)
            {
                if (_disposed)
                {
                    // Закрылись, пока подключались: соединение не нужно (служба сразу снимет его фильтры).
                    client.Dispose();
                    throw new CoreServiceException("лаунчер закрывается");
                }

                _client = client;
            }
        }

        return await client.GuardAsync(request, cancellation).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _lost, 1); // закрываемся сами
        lock (_sync)
        {
            _disposed = true;
            _client?.Dispose(); // служба снимет фильтры этого соединения
            _client = null;
        }
    }
}

/// <summary>
/// Блокировка трафика мимо VPN: держит в службе актуальный набор фильтров Windows (какие программы пускать в сеть
/// только через VPN). Набор собирает лаунчер (<see cref="Submit"/>) при запуске приложений, появлении их процессов
/// и изменении VPN; отправляется он в фоне, последний побеждает. Перед запуском приложения набор ставится сразу
/// (<see cref="ApplyNowAsync"/>), и без него приложение не запускается. Связь со службой пропала — фильтры служба
/// уже сняла, набор отправляется заново. Лаунчер закрылся — служба снимает фильтры сама.
/// </summary>
public sealed class VpnGuard : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private const string Closing = "лаунчер закрывается";
    private static readonly JsonSerializerOptions KeyOptions = new(CoreServiceClient.JsonOptions);

    private readonly Func<IGuardBackend> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private readonly Timer _retry;
    private IGuardBackend? _backend;
    private GuardRequest _desired = new();
    private string _desiredKey;
    private string? _appliedKey;
    private string? _lastError;
    private string? _lastNotice;
    private HashSet<string> _unguarded = new(StringComparer.OrdinalIgnoreCase);
    private int _pumping;
    private int _pumpRequested;
    private bool _disposed;

    public VpnGuard(Func<IGuardBackend> factory)
    {
        _factory = factory;
        _desiredKey = Key(_desired);
        _retry = new Timer(_ => RetryIfNeeded(), null, RetryInterval, RetryInterval);
    }

    /// <summary>Фоновая отправка не удалась: причина для журнала (одна и та же — один раз). Любой поток.</summary>
    public event Action<string>? Failed;

    /// <summary>После неудачи фильтры снова на месте. Любой поток.</summary>
    public event Action? Restored;

    /// <summary>Замечание службы (например, какие программы она не ограничивает), одно и то же — один раз. Любой поток.</summary>
    public event Action<string>? Notice;

    /// <summary>Фильтры, которые сейчас должны действовать, совпадают с отправленными.</summary>
    public bool IsApplied
    {
        get
        {
            lock (_state)
                return _appliedKey == _desiredKey;
        }
    }

    /// <summary>
    /// Служба не смогла проверить эту программу (сетевой диск и т. п.) и не ограничила её — по последнему отправленному
    /// набору. Сравнивается путь в том виде, в каком он был в наборе.
    /// </summary>
    public bool IsUnguarded(string path)
    {
        lock (_state)
            return _unguarded.Contains(path);
    }

    /// <summary>Новый набор: отправится в фоне.</summary>
    public void Submit(GuardRequest request)
    {
        lock (_state)
        {
            if (_disposed)
                return;
            _desired = request;
            _desiredKey = Key(request);
        }

        Pump();
    }

    /// <summary>Поставить набор сейчас (перед запуском приложения). null — получилось, иначе причина для пользователя.</summary>
    public async Task<string?> ApplyNowAsync(GuardRequest request, CancellationToken cancellation = default)
    {
        lock (_state)
        {
            if (_disposed)
                return Closing;
            _desired = request;
            _desiredKey = Key(request);
        }

        var error = await ApplyDesiredAsync(cancellation).ConfigureAwait(false);
        lock (_state)
            _lastError = error;
        return error;
    }

    private static string Key(GuardRequest request) => JsonSerializer.Serialize(new GuardRequest
    {
        // Порядок не важен: одинаковые наборы не отправляются повторно.
        Apps = request.Apps
            .Select(r => new GuardRule
            {
                Paths = r.Paths.Select(p => p.ToLowerInvariant()).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList(),
                ViaCore = r.ViaCore,
                Interfaces = r.Interfaces.Distinct().OrderBy(i => i).ToList(),
                LoopbackPorts = r.LoopbackPorts is { Count: > 0 } ports ? ports.Distinct().OrderBy(p => p).ToList() : null
            })
            .OrderBy(r => string.Join("|", r.Paths), StringComparer.Ordinal)
            .ThenBy(r => r.ViaCore)
            .ToList(),
        DnsInterfaces = request.DnsInterfaces?.Distinct().OrderBy(i => i).ToList(),
        DnsPaths = request.DnsPaths is { Count: > 0 } dnsPaths
            ? dnsPaths.Select(p => p.ToLowerInvariant()).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList()
            : null
    }, KeyOptions);

    private async Task<string?> ApplyDesiredAsync(CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            GuardRequest request;
            string key;
            IGuardBackend? backend;
            lock (_state)
            {
                if (_disposed)
                    return Closing;
                request = _desired;
                key = _desiredKey;
                backend = _backend;
                if (key == _appliedKey && (backend?.IsConnected ?? request.IsEmpty))
                    return null;

                // Фильтров нет и не нужно: со службой даже не связываемся.
                if (request.IsEmpty && backend is null)
                {
                    _appliedKey = key;
                    return null;
                }
            }

            if (backend is null)
            {
                backend = _factory();
                backend.Lost += OnLost;
                lock (_state)
                {
                    if (_disposed)
                    {
                        backend.Dispose();
                        return Closing;
                    }

                    _backend = backend;
                }
            }

            GuardReply reply;
            try
            {
                reply = await backend.ApplyAsync(request, cancellation).ConfigureAwait(false);
                lock (_state)
                {
                    _appliedKey = key;
                    _unguarded = new HashSet<string>(reply.Unguarded, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // CoreServiceException — отказ или обрыв связи; прочее (связь закрылась на полуслове и т. п.) — тоже сбой,
                // а не падение лаунчера.
                lock (_state)
                    _appliedKey = null; // что сейчас в службе — неизвестно: отправим снова
                if (!backend.IsConnected)
                    DropBackend(backend);
                return ex is CoreServiceException ? ex.Message : "нет связи со службой «Безопасный лаунчер — сеть» (" + ex.Message + ")";
            }

            if (reply.Notice is { } notice && notice != _lastNotice)
            {
                _lastNotice = notice;
                Notice?.Invoke(notice);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Pump()
    {
        Interlocked.Exchange(ref _pumpRequested, 1);
        if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _pumpRequested, 0) == 1)
                {
                    string? error;
                    try
                    {
                        error = await ApplyDesiredAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        error = ex.Message;
                    }

                    Report(error);
                }
            }
            finally
            {
                Volatile.Write(ref _pumping, 0);
                if (Volatile.Read(ref _pumpRequested) == 1 && !_disposed)
                    Pump();
            }
        });
    }

    private void Report(string? error)
    {
        string? previous;
        lock (_state)
        {
            if (_disposed)
                return; // лаунчер закрывается: не о чем сообщать
            previous = _lastError;
            _lastError = error;
        }

        if (error is not null && error != previous)
            Failed?.Invoke(error);
        else if (error is null && previous is not null)
            Restored?.Invoke();
    }

    private void RetryIfNeeded()
    {
        bool needed;
        lock (_state)
            needed = !_disposed && _lastError is not null && _appliedKey != _desiredKey;
        if (needed)
            Pump();
    }

    private void OnLost()
    {
        IGuardBackend? backend;
        lock (_state)
        {
            backend = _backend;
            _appliedKey = null; // служба сняла фильтры этого соединения
        }

        if (backend is not null)
            DropBackend(backend);
        Pump();
    }

    private void DropBackend(IGuardBackend backend)
    {
        lock (_state)
        {
            if (!ReferenceEquals(_backend, backend))
                return;
            _backend = null;
        }

        backend.Lost -= OnLost;
        backend.Dispose();
    }

    public void Dispose()
    {
        IGuardBackend? backend;
        lock (_state)
        {
            if (_disposed)
                return;
            _disposed = true;
            backend = _backend;
            _backend = null;
        }

        _retry.Dispose();
        if (backend is not null)
        {
            backend.Lost -= OnLost;
            backend.Dispose(); // служба снимает фильтры, как только закрывается связь
        }
    }
}
