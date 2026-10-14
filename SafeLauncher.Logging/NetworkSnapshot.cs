using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace SafeLauncher.Logging;

/// <summary>
/// Снимок сетевых адаптеров для журнала и выгрузки: имя, тип, состояние, есть ли IPv4/IPv6/шлюз/DNS, адреса.
/// Адреса пишутся как есть: маскирует тот, кто пишет строку в журнал (по настройке).
/// </summary>
public static class NetworkSnapshot
{
    public sealed record Adapter(string Name, string Description, string Type, bool Up, int? Index, IReadOnlyList<string> Addresses,
        bool Ipv4, bool Ipv6, bool Gateway, bool Ipv6Gateway, bool Dns);

    public static IReadOnlyList<Adapter> Adapters()
    {
        var result = new List<Adapter>();
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var nic in all)
        {
            try
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var props = nic.GetIPProperties();
                var unicast = props.UnicastAddresses.Select(a => a.Address).ToList();
                int? index = null;
                try
                {
                    index = props.GetIPv4Properties()?.Index;
                }
                catch (Exception)
                {
                    try
                    {
                        index = props.GetIPv6Properties()?.Index;
                    }
                    catch (Exception)
                    {
                        // нет ни того, ни другого
                    }
                }

                var gateways = props.GatewayAddresses.Select(g => g.Address).ToList();
                result.Add(new Adapter(
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType.ToString(),
                    nic.OperationalStatus == OperationalStatus.Up,
                    index,
                    unicast.Select(a => a.ToString()).ToList(),
                    unicast.Any(a => a.AddressFamily == AddressFamily.InterNetwork),
                    unicast.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal),
                    gateways.Any(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(System.Net.IPAddress.Any)),
                    gateways.Any(g => g.AddressFamily == AddressFamily.InterNetworkV6),
                    props.DnsAddresses.Count > 0));
            }
            catch (Exception)
            {
                // адаптер пропал во время опроса
            }
        }

        return result;
    }

    /// <summary>Одной строкой для журнала: только работающие адаптеры.</summary>
    public static string Describe()
    {
        var up = Adapters().Where(a => a.Up).ToList();
        if (up.Count == 0)
            return "работающих адаптеров нет";
        return "адаптеры: " + string.Join("; ", up.Select(Describe));
    }

    public static string Describe(Adapter a) =>
        $"{a.Name} ({a.Type}{(a.Index is { } i ? ", #" + i : string.Empty)}, {(a.Up ? "up" : "down")}, " +
        $"IPv4 {(a.Ipv4 ? "да" : "нет")}, IPv6 {(a.Ipv6 ? "да" : "нет")}, шлюз {(a.Gateway ? "да" : "нет")}" +
        $"{(a.Ipv6Gateway ? ", шлюз IPv6 да" : string.Empty)}, DNS {(a.Dns ? "да" : "нет")}, адреса {string.Join(" ", a.Addresses)})";

    /// <summary>Для environment.json: адреса через маскировщик (или как есть).</summary>
    public static JsonArray ToJson(Func<string, string> mask)
    {
        var array = new JsonArray();
        foreach (var a in Adapters())
        {
            array.Add(new JsonObject
            {
                ["name"] = a.Name,
                ["description"] = a.Description,
                ["type"] = a.Type,
                ["up"] = a.Up,
                ["index"] = a.Index,
                ["ipv4"] = a.Ipv4,
                ["ipv6"] = a.Ipv6,
                ["gateway"] = a.Gateway,
                ["ipv6Gateway"] = a.Ipv6Gateway,
                ["dns"] = a.Dns,
                ["addresses"] = new JsonArray(a.Addresses.Select(x => (JsonNode)JsonValue.Create(mask(x))!).ToArray())
            });
        }
        return array;
    }
}
