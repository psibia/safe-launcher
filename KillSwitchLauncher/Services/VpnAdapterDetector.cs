using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace KillSwitchLauncher.Services;

/// <summary>Сетевой адаптер в том виде, в котором он нужен лаунчеру.</summary>
/// <param name="Index">Номер адаптера в Windows (IfIndex) для фильтров «только через VPN»; 0 — неизвестен.</param>
/// <param name="HasDns">У адаптера указан свой DNS-сервер (тогда DNS можно пускать только через него).</param>
public sealed record VpnAdapterInfo(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool IsUp,
    bool IsVpnLike,
    string Product,
    int Index = 0,
    bool HasDns = false);

/// <summary>
/// Определяет, какие сетевые интерфейсы Windows являются VPN-туннелями.
/// У Windows нет флага «это VPN», поэтому используется эвристика:
///   * тип интерфейса Ppp (встроенный VPN Windows: PPTP/L2TP/SSTP/IKEv2 через RAS) или Tunnel;
///   * либо ключевые слова в имени/описании драйвера (WireGuard, OpenVPN TAP, Cisco AnyConnect и т.д.);
///   * служебные псевдо-туннели Windows (Teredo, ISATAP, 6to4, IP-HTTPS) и loopback исключаются.
/// Адаптер считается активным, только если он в состоянии Up И уже получил рабочий IP-адрес:
/// TAP/Wintun бывают «Up» ещё до того, как туннель реально поднялся.
/// Важно: Windows часто называет адаптер TAP от OpenVPN «Подключение по локальной сети»,
/// поэтому в интерфейсе показывается и продукт (по описанию драйвера), и системное имя.
/// </summary>
public static class VpnAdapterDetector
{
    /// <summary>
    /// Адаптер ядра лаунчера. Это не «VPN на весь компьютер»: через него идут только приложения лаунчера, поэтому
    /// для правил адаптеров его нет, иначе приложение под системным VPN запустилось бы без него.
    /// </summary>
    public const string OwnTunnelName = "SafeLauncher";

    private static readonly Regex VpnKeywords = new(
        @"\bvpn\b|wireguard|wintun|amneziawg|nordlynx|\btap\b|tap-windows|\btun\b|openvpn|anyconnect|cisco|" +
        @"fortinet|forticlient|globalprotect|pangp|juniper|pulse secure|ivanti|check ?point|sonicwall|" +
        @"zscaler|softether|outline|f5 |big-ip|array networks|kerio|watchguard|openconnect|tailscale|zerotier|netbird",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Excluded = new(
        @"teredo|isatap|6to4|ip-https|loopback|wan miniport|kernel debug",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<VpnAdapterInfo> GetAdapters()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<VpnAdapterInfo>();
        }

        return all
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && !IsOwnTunnel(n.Name))
            .Select(n => new VpnAdapterInfo(
                n.Name,
                n.Description,
                n.NetworkInterfaceType,
                IsActive(n),
                IsVpnLike(n.Name, n.Description, n.NetworkInterfaceType),
                GetProduct(n.Description, n.NetworkInterfaceType),
                IndexOf(n),
                HasOwnDns(n)))
            .OrderByDescending(a => a.IsVpnLike)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>IfIndex адаптера (у IPv4 и IPv6 он один и тот же); 0 — не узнать.</summary>
    private static int IndexOf(NetworkInterface nic)
    {
        try
        {
            var properties = nic.GetIPProperties();
            if (nic.Supports(NetworkInterfaceComponent.IPv4) && properties.GetIPv4Properties() is { } v4)
                return v4.Index;
            if (nic.Supports(NetworkInterfaceComponent.IPv6) && properties.GetIPv6Properties() is { } v6)
                return v6.Index;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
        }

        return 0;
    }

    /// <summary>Есть ли у адаптера свой DNS-сервер. Адреса fec0:0:0:ffff::1–3 Windows подставляет, когда DNS не задан.</summary>
    private static bool HasOwnDns(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().DnsAddresses.Any(a =>
                !(a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6SiteLocal));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    public static bool IsOwnTunnel(string name) => string.Equals(name, OwnTunnelName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Up и есть хотя бы один «настоящий» адрес (не APIPA 169.254.x.x и не IPv6 link-local).</summary>
    private static bool IsActive(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up)
            return false;

        try
        {
            return nic.GetIPProperties().UnicastAddresses.Any(a =>
            {
                var ip = a.Address;
                if (IPAddress.IsLoopback(ip)) return false;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    var b = ip.GetAddressBytes();
                    return !(b[0] == 169 && b[1] == 254);
                }

                return ip.AddressFamily == AddressFamily.InterNetworkV6 && !ip.IsIPv6LinkLocal;
            });
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>Понятное название VPN-продукта по описанию драйвера.</summary>
    internal static string GetProduct(string description, NetworkInterfaceType type)
    {
        var d = description;
        bool Has(string s) => d.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("TAP-Windows")) return "OpenVPN (TAP)";
        if (Has("Data Channel Offload") || Has("ovpn-dco")) return "OpenVPN (DCO)";
        if (Has("Wintun")) return "WireGuard / OpenVPN (Wintun)";
        if (Has("WireGuard")) return "WireGuard";
        if (Has("AmneziaWG")) return "AmneziaWG";
        if (Has("AnyConnect") || Has("Cisco")) return "Cisco AnyConnect";
        if (Has("Fortinet") || Has("FortiClient")) return "FortiClient";
        if (Has("PANGP") || Has("GlobalProtect")) return "GlobalProtect";
        if (Has("Juniper") || Has("Pulse") || Has("Ivanti")) return "Ivanti / Pulse Secure";
        if (Has("Check Point")) return "Check Point VPN";
        if (Has("Tailscale")) return "Tailscale";
        if (type == NetworkInterfaceType.Ppp) return "VPN Windows (PPP)";
        return d;
    }

    internal static bool IsVpnLike(string name, string description, NetworkInterfaceType type)
    {
        var text = name + " " + description;
        if (Excluded.IsMatch(text))
            return false;

        return type is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
               || VpnKeywords.IsMatch(text);
    }
}
