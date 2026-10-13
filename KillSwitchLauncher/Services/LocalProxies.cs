using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>Запущенная VPN-программа с локальным прокси и его обычный адрес.</summary>
public sealed record LocalProxyHint(string Program, int Port, ProxyProtocol Protocol);

/// <summary>
/// Подсказка для «прокси другой программы»: если запущена известная VPN-программа, лаунчер предлагает её прокси
/// с портом по умолчанию. Порт можно поменять: в самой программе он мог быть изменён.
/// </summary>
public static class LocalProxies
{
    private static readonly (string Process, LocalProxyHint Hint)[] Known =
    {
        ("v2rayN", new LocalProxyHint("v2rayN", 10808, ProxyProtocol.Socks5)),
        ("clash-verge", new LocalProxyHint("Clash Verge", 7897, ProxyProtocol.Socks5)),
        ("Clash Verge", new LocalProxyHint("Clash Verge", 7897, ProxyProtocol.Socks5)),
        ("verge-mihomo", new LocalProxyHint("Clash Verge", 7897, ProxyProtocol.Socks5)),
        ("Hiddify", new LocalProxyHint("Hiddify", 12334, ProxyProtocol.Socks5)),
        ("HiddifyCli", new LocalProxyHint("Hiddify", 12334, ProxyProtocol.Socks5)),
        ("nekoray", new LocalProxyHint("NekoRay", 2080, ProxyProtocol.Socks5)),
        ("nekobox", new LocalProxyHint("NekoBox", 2080, ProxyProtocol.Socks5))
    };

    public static LocalProxyHint? Detect() => Detect(RunningProcessNames());

    internal static LocalProxyHint? Detect(IEnumerable<string> processNames)
    {
        var running = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        return Known.Where(k => running.Contains(k.Process)).Select(k => k.Hint).FirstOrDefault();
    }

    private static IEnumerable<string> RunningProcessNames()
    {
        var names = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                    // процесс уже завершился
                }
            }
        }

        return names;
    }
}
