using System;
using System.Collections.Generic;
using System.Linq;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>Чем обеспечивается VPN у приложения после учёта общих настроек.</summary>
public enum NetworkPlanKind
{
    /// <summary>VPN включает другая программа; лаунчер проверяет адаптеры по правилу <see cref="NetworkPlan.AdapterRule"/>.</summary>
    SystemAdapters,

    /// <summary>Трафик ведёт ядро лаунчера через <see cref="NetworkPlan.Connection"/>.</summary>
    LauncherConnection,

    /// <summary>Подключение лаунчера не выбрано или удалено: приложение запускать нельзя, пока его не выберут.</summary>
    MissingConnection
}

/// <summary>Откуда взята сетевая настройка приложения: из общих настроек или из его собственных.</summary>
public enum NetworkPlanSource
{
    Global,
    App
}

/// <summary>
/// Итоговые сетевые условия одного приложения: условие запуска «Как в общих» раскрыто, защита
/// «как в общих» заменена общими значениями. Неизменяемый снимок, безопасен для фоновых потоков.
/// </summary>
public sealed class NetworkPlan
{
    private NetworkPlan(NetworkPlanKind kind, NetworkPlanSource source, VpnRule? adapterRule, VpnConnection? connection,
        bool requireVpn, bool closeOnDrop, IReadOnlyList<string> directDomains)
    {
        Kind = kind;
        Source = source;
        AdapterRule = adapterRule;
        Connection = connection;
        RequireVpn = requireVpn;
        CloseOnDrop = closeOnDrop;
        DirectDomains = directDomains;
    }

    public NetworkPlanKind Kind { get; }

    /// <summary>Условие запуска пришло из общих настроек («Как в общих») или задано у приложения.</summary>
    public NetworkPlanSource Source { get; }

    /// <summary>Правило адаптеров для <see cref="NetworkPlanKind.SystemAdapters"/>, иначе null.</summary>
    public VpnRule? AdapterRule { get; }

    /// <summary>Подключение для <see cref="NetworkPlanKind.LauncherConnection"/>, иначе null.</summary>
    public VpnConnection? Connection { get; }

    /// <summary>«Не запускать без VPN».</summary>
    public bool RequireVpn { get; }

    /// <summary>«Закрывать при обрыве».</summary>
    public bool CloseOnDrop { get; }

    /// <summary>Домены, которые идут мимо подключения лаунчера. Пусто, если трафик ведёт не лаунчер.</summary>
    public IReadOnlyList<string> DirectDomains { get; }

    /// <summary>Трафик приложения ведёт ядро лаунчера (или должно вести, но подключение не выбрано).</summary>
    public bool UsesLauncherCore => Kind != NetworkPlanKind.SystemAdapters;

    public static NetworkPlan For(AppEntry app, AppSettings settings)
    {
        var network = app.Network ?? new AppNetwork();
        var requireVpn = Resolve(network.RequireVpn, settings.RequireVpn);
        var closeOnDrop = Resolve(network.CloseOnDrop, settings.KillSwitchEnabled);

        switch (network.Mode)
        {
            case AppNetworkMode.OwnAdapters:
                return Adapters(NetworkPlanSource.App, new VpnRule(network.RuleMode, network.SelectedAdapters ?? new List<string>()));

            case AppNetworkMode.OwnConnection:
                return Launcher(NetworkPlanSource.App, network.ConnectionId);

            default:
                return settings.NetworkMode == GlobalNetworkMode.LauncherConnection
                    ? Launcher(NetworkPlanSource.Global, settings.ConnectionId)
                    : Adapters(NetworkPlanSource.Global, new VpnRule(settings.RuleMode, settings.SelectedAdapters));
        }

        NetworkPlan Adapters(NetworkPlanSource source, VpnRule rule) =>
            new(NetworkPlanKind.SystemAdapters, source, rule, null, requireVpn, closeOnDrop, Array.Empty<string>());

        NetworkPlan Launcher(NetworkPlanSource source, Guid? connectionId)
        {
            var connection = connectionId is { } id ? settings.Connections.FirstOrDefault(c => c.Id == id) : null;
            var domains = (network.DirectDomains ?? new List<string>()).ToArray();
            // Своё подключение без ядра невозможно, поэтому без подключения запуск запрещён, что бы ни стояло в «Не запускать без VPN».
            return connection is null
                ? new NetworkPlan(NetworkPlanKind.MissingConnection, source, null, null, true, closeOnDrop, domains)
                : new NetworkPlan(NetworkPlanKind.LauncherConnection, source, null, connection, requireVpn, closeOnDrop, domains);
        }
    }

    /// <summary>Подключения, которыми пользуются приложения (включая общее, если кто-то «как в общих»).</summary>
    public static IReadOnlySet<Guid> ConnectionsInUse(AppSettings settings)
    {
        var used = new HashSet<Guid>();
        foreach (var app in settings.Apps)
        {
            if (For(app, settings).Connection is { } connection)
                used.Add(connection.Id);
        }

        return used;
    }

    private static bool Resolve(ProtectionOverride value, bool global) => value switch
    {
        ProtectionOverride.On => true,
        ProtectionOverride.Off => false,
        _ => global
    };
}
