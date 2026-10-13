using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>Результат одной проверки VPN.</summary>
public sealed record VpnSnapshot(
    bool IsConnected,
    IReadOnlyList<VpnAdapterInfo> Adapters,
    IReadOnlyList<string> ActiveVpnNames,
    string Explanation);

public sealed class VpnStatusChangedEventArgs : EventArgs
{
    public VpnStatusChangedEventArgs(VpnSnapshot? previous, VpnSnapshot current)
    {
        Previous = previous;
        Current = current;
    }

    public VpnSnapshot? Previous { get; }
    public VpnSnapshot Current { get; }

    /// <summary>VPN только что пропал: именно в этот момент срабатывает Kill-Switch.</summary>
    public bool JustDisconnected => Previous is { IsConnected: true } && !Current.IsConnected;
}

/// <summary>
/// Фоновый монитор VPN.
/// Источники сигналов:
///   1. NetworkChange.NetworkAddressChanged / NetworkAvailabilityChanged — Windows присылает их сразу,
///      как только у интерфейса меняется адрес или состояние (подключение/отключение туннеля);
///   2. резервный опрос по таймеру — на случай, если какой-то VPN-клиент меняет состояние
///      адаптера без смены адресов и событие не приходит.
/// При каждом сигнале заново читается список интерфейсов и применяется правило (VpnRule).
/// Событие StatusChanged поднимается только при реальном изменении и всегда в порядке проверок.
/// </summary>
public sealed class VpnMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Timer _pollTimer;
    private readonly TimeSpan _pollInterval;
    private VpnRule _rule;
    private VpnSnapshot? _current;
    private string? _signature;
    private bool _started;
    private bool _disposed;

    public VpnMonitor(VpnRule rule, TimeSpan? pollInterval = null)
    {
        _rule = rule;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _pollTimer = new Timer(_ => Evaluate(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Вызывается в фоновом потоке. Обработчик должен быть быстрым и не блокировать UI-поток синхронно.</summary>
    public event EventHandler<VpnStatusChangedEventArgs>? StatusChanged;

    public VpnSnapshot Current
    {
        get
        {
            lock (_gate)
                return _current ?? EvaluateLocked();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
        }

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        Evaluate();
        _pollTimer.Change(_pollInterval, _pollInterval);
    }

    public void SetRule(VpnRule rule)
    {
        lock (_gate)
            _rule = rule;
        Evaluate();
    }

    /// <summary>Немедленная проверка (используется и перед запуском приложения).</summary>
    public VpnSnapshot Evaluate()
    {
        lock (_gate)
        {
            if (_disposed) return _current ?? new VpnSnapshot(false, Array.Empty<VpnAdapterInfo>(), Array.Empty<string>(), "Монитор остановлен");
            return EvaluateLocked();
        }
    }

    private VpnSnapshot EvaluateLocked()
    {
        var adapters = VpnAdapterDetector.GetAdapters();
        var snapshot = Apply(_rule, adapters);
        var signature = BuildSignature(snapshot);

        if (signature != _signature)
        {
            var previous = _current;
            _current = snapshot;
            _signature = signature;

            // Поднимаем событие под блокировкой, чтобы обработчики видели изменения строго по порядку
            // (иначе «подключён» из одного потока мог бы обогнать «отключён» из другого).
            try
            {
                StatusChanged?.Invoke(this, new VpnStatusChangedEventArgs(previous, snapshot));
            }
            catch
            {
                // ошибка подписчика не должна останавливать мониторинг
            }
        }

        return _current!;
    }

    internal static VpnSnapshot Apply(VpnRule rule, IReadOnlyList<VpnAdapterInfo> adapters)
    {
        bool IsUp(string name) => adapters.Any(a => a.IsUp && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        switch (rule.Mode)
        {
            case VpnRuleMode.AnyOfSelected:
            {
                if (rule.SelectedAdapters.Count == 0)
                    return new VpnSnapshot(false, adapters, Array.Empty<string>(), "Не выбран ни один VPN-адаптер");

                var active = rule.SelectedAdapters.Where(IsUp).ToList();
                return new VpnSnapshot(active.Count > 0, adapters, active,
                    active.Count > 0 ? "Активен один из выбранных VPN" : "Выбранные VPN не активны");
            }

            case VpnRuleMode.AllOfSelected:
            {
                if (rule.SelectedAdapters.Count == 0)
                    return new VpnSnapshot(false, adapters, Array.Empty<string>(), "Не выбран ни один VPN-адаптер");

                var active = rule.SelectedAdapters.Where(IsUp).ToList();
                var missing = rule.SelectedAdapters.Except(active, StringComparer.OrdinalIgnoreCase).ToList();
                return new VpnSnapshot(missing.Count == 0, adapters, active,
                    missing.Count == 0 ? "Активны все выбранные VPN" : "Не подключены: " + string.Join(", ", missing));
            }

            default:
            {
                var active = adapters.Where(a => a.IsVpnLike && a.IsUp).Select(a => a.Name).ToList();
                return new VpnSnapshot(active.Count > 0, adapters, active,
                    active.Count > 0 ? "Обнаружен активный VPN" : "Нет защищённого соединения");
            }
        }
    }

    private static string BuildSignature(VpnSnapshot s) =>
        s.IsConnected + "|" + s.Explanation + "|" +
        string.Join(";", s.Adapters.Select(a => $"{a.Name}:{a.Description}:{a.IsUp}:{a.IsVpnLike}:{a.Index}:{a.HasDns}"));

    private void OnNetworkChanged(object? sender, EventArgs e) => Evaluate();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Evaluate();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _pollTimer.Dispose();
    }
}
