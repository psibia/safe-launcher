using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>Приложение в настройках ядра: метка его процессов, подключение и защита.</summary>
public sealed record CoreApp(Guid AppId, string Name, string Tag, Guid ConnectionId, string ConnectionName, bool RequireVpn, bool CloseOnDrop);

/// <summary>
/// Что должно делать ядро при текущих настройках: все приложения, которые ходят через подключения лаунчера, и эти
/// подключения. В план входят и не запущенные приложения: тогда запуск ещё одного приложения не требует перезапуска
/// ядра (а перезапуск на секунды оставляет уже работающие приложения без сети). Неизменяемый снимок.
/// </summary>
public sealed class CorePlan
{
    private CorePlan(CoreStartRequest request, IReadOnlyDictionary<Guid, CoreApp> apps, IReadOnlyDictionary<Guid, string> connectionTags,
        IReadOnlyDictionary<Guid, string> problems)
    {
        Request = request;
        Apps = apps;
        ConnectionTags = connectionTags;
        Problems = problems;
        Fingerprint = JsonSerializer.Serialize(request, CoreServiceClient.JsonOptions);
    }

    public static readonly CorePlan Empty = new(new CoreStartRequest(), new Dictionary<Guid, CoreApp>(), new Dictionary<Guid, string>(),
        new Dictionary<Guid, string>());

    /// <summary>Что отправить службе.</summary>
    public CoreStartRequest Request { get; }

    /// <summary>Приложения через ядро, по Id.</summary>
    public IReadOnlyDictionary<Guid, CoreApp> Apps { get; }

    /// <summary>Подключение → его tag в ядре.</summary>
    public IReadOnlyDictionary<Guid, string> ConnectionTags { get; }

    /// <summary>Приложения, которые должны идти через ядро, но не могут: почему (по-русски, для пользователя).</summary>
    public IReadOnlyDictionary<Guid, string> Problems { get; }

    /// <summary>Одинаковый у одинаковых планов: перезапускать ядро не нужно.</summary>
    public string Fingerprint { get; }

    public static string AppTag(Guid appId) => "a" + appId.ToString("N");

    public static string ConnectionTag(Guid connectionId) => "c" + connectionId.ToString("N");

    public static CorePlan Build(AppSettings settings, bool ipv6)
    {
        var request = new CoreStartRequest { Ipv6 = ipv6, HideDns = settings.HideDns };
        var apps = new Dictionary<Guid, CoreApp>();
        var tags = new Dictionary<Guid, string>();
        var problems = new Dictionary<Guid, string>();
        var broken = new Dictionary<Guid, string>();

        foreach (var app in settings.Apps)
        {
            var plan = NetworkPlan.For(app, settings);
            if (plan.Kind == NetworkPlanKind.SystemAdapters)
                continue;

            if (plan.Connection is not { } connection)
            {
                problems[app.Id] = plan.Source == NetworkPlanSource.Global
                    ? "Подключение лаунчера в общих настройках не выбрано. Выберите его в настройках."
                    : $"У «{app.Name}» не выбрано подключение. Выберите его в настройках приложения.";
                continue;
            }

            if (broken.TryGetValue(connection.Id, out var reason))
            {
                problems[app.Id] = reason;
                continue;
            }

            if (!tags.TryGetValue(connection.Id, out var connectionTag))
            {
                JsonObject outbound;
                string? dns;
                try
                {
                    (outbound, dns) = ConnectionOutbound.BuildWithDns(connection, ConnectionTag(connection.Id));
                }
                catch (ConnectionParseException ex)
                {
                    broken[connection.Id] = ex.Message;
                    problems[app.Id] = ex.Message;
                    continue;
                }

                connectionTag = ConnectionTag(connection.Id);
                tags[connection.Id] = connectionTag;
                request.Outbounds.Add(outbound);
                if (dns is not null)
                    (request.OutboundDns ??= new Dictionary<string, string>())[connectionTag] = dns;
            }

            var tag = AppTag(app.Id);
            request.Apps.Add(new CoreAppRoute
            {
                Tag = tag,
                Outbound = connectionTag,
                DirectDomains = DomainList.Normalize(plan.DirectDomains)
            });
            apps[app.Id] = new CoreApp(app.Id, app.Name, tag, connection.Id, connection.Name, plan.RequireVpn, plan.CloseOnDrop);
        }

        return new CorePlan(request, apps, tags, problems);
    }

    /// <summary>
    /// Есть ли в сети IPv6-интернет. Тогда адаптеру ядра нужен и IPv6-адрес, иначе соединения приложений по IPv6
    /// не попадут в ядро. Без IPv6 в сети адрес не нужен: браузерные движки пробуют IPv6 и ждут впустую.
    /// </summary>
    public static bool DetectIpv6()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                    VpnAdapterDetector.IsOwnTunnel(adapter.Name))
                    continue;

                var properties = adapter.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetworkV6 && !g.Address.Equals(System.Net.IPAddress.IPv6Any));
                var hasGlobal = properties.UnicastAddresses.Any(u =>
                    u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal &&
                    !u.Address.IsIPv6SiteLocal && !u.Address.IsIPv6UniqueLocal && !u.Address.Equals(System.Net.IPAddress.IPv6Loopback));
                if (hasGateway && hasGlobal)
                    return true;
            }
        }
        catch (NetworkInformationException)
        {
        }

        return false;
    }
}
