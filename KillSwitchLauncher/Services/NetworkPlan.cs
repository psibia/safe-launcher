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
    MissingConnection,

    /// <summary>Без сети: фильтр Windows пускает программы приложения только внутри компьютера, VPN не нужен.</summary>
    Offline
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
        bool closeOnDrop, OutsideCopies outsideCopies, IReadOnlyList<string> directDomains)
    {
        Kind = kind;
        Source = source;
        AdapterRule = adapterRule;
        Connection = connection;
        CloseOnDrop = closeOnDrop;
        OutsideCopies = outsideCopies;
        DirectDomains = directDomains;
    }

    public NetworkPlanKind Kind { get; }

    /// <summary>Условие запуска пришло из общих настроек («Как в общих») или задано у приложения.</summary>
    public NetworkPlanSource Source { get; }

    /// <summary>Правило адаптеров для <see cref="NetworkPlanKind.SystemAdapters"/>, иначе null.</summary>
    public VpnRule? AdapterRule { get; }

    /// <summary>Подключение для <see cref="NetworkPlanKind.LauncherConnection"/>, иначе null.</summary>
    public VpnConnection? Connection { get; }

    /// <summary>«Закрывать при обрыве».</summary>
    public bool CloseOnDrop { get; }

    /// <summary>
    /// Копии приложения, запущенные мимо лаунчера. «Через лаунчер» действует для всех приложений, остальное — только когда
    /// трафик ведёт ядро лаунчера.
    /// </summary>
    public OutsideCopies OutsideCopies { get; }

    /// <summary>
    /// Домены, которые идут мимо подключения лаунчера: общие и свои исключения (или только свои, «Вместо общих»).
    /// Пусто, если трафик ведёт не лаунчер.
    /// </summary>
    public IReadOnlyList<string> DirectDomains { get; }

    /// <summary>Трафик приложения ведёт ядро лаунчера (или должно вести, но подключение не выбрано).</summary>
    public bool UsesLauncherCore => Kind is NetworkPlanKind.LauncherConnection or NetworkPlanKind.MissingConnection;

    /// <param name="offlineRun">Приложение запущено разово без сети («Запустить без сети» в меню плитки).</param>
    public static NetworkPlan For(AppEntry app, AppSettings settings, bool offlineRun = false)
    {
        var network = app.Network ?? new AppNetwork();
        var closeOnDrop = Resolve(network.CloseOnDrop, settings.KillSwitchEnabled);
        var outsideCopies = Resolve(network.OutsideCopies, settings.OutsideCopies);

        // Без сети обрывать нечего: Kill-Switch не нужен.
        if (offlineRun || network.Mode == AppNetworkMode.Offline)
            return new NetworkPlan(NetworkPlanKind.Offline, NetworkPlanSource.App, null, null, false, outsideCopies, Array.Empty<string>());

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
            new(NetworkPlanKind.SystemAdapters, source, rule, null, closeOnDrop, outsideCopies, Array.Empty<string>());

        NetworkPlan Launcher(NetworkPlanSource source, Guid? connectionId)
        {
            var connection = connectionId is { } id ? settings.Connections.FirstOrDefault(c => c.Id == id) : null;
            var domains = DomainList.Combine(settings.DirectDomains, network.DirectDomains, network.DomainsMode).ToArray();
            // Своё подключение без ядра невозможно, поэтому без подключения запуск запрещён.
            return connection is null
                ? new NetworkPlan(NetworkPlanKind.MissingConnection, source, null, null, closeOnDrop, outsideCopies, domains)
                : new NetworkPlan(NetworkPlanKind.LauncherConnection, source, null, connection, closeOnDrop, outsideCopies, domains);
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

    /// <summary>Значение «Если открыто не из лаунчера» у приложения: своё или из общих настроек.</summary>
    public static OutsideCopies Resolve(OutsideCopiesOverride value, OutsideCopies global) => value switch
    {
        OutsideCopiesOverride.Vpn => OutsideCopies.Vpn,
        OutsideCopiesOverride.Block => OutsideCopies.Block,
        OutsideCopiesOverride.Allow => OutsideCopies.Allow,
        OutsideCopiesOverride.Launcher => OutsideCopies.Launcher,
        _ => global
    };
}
