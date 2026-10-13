using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace KillSwitchLauncher.Services;

/// <summary>Системный прокси Windows, который ведёт на этот же компьютер (обычно его включает другая VPN-программа).</summary>
/// <param name="Display">Как показать пользователю: «127.0.0.1:10808».</param>
/// <param name="Ports">Порты на 127.0.0.1, через которые программы пошли бы мимо подключения лаунчера.</param>
public sealed record LocalProxy(string Display, IReadOnlyList<int> Ports);

/// <summary>
/// Системный прокси (Параметры → Сеть → Прокси, он же «Свойства браузера»). Браузеры и многие программы сами отправляют
/// трафик в него. Если он на этом же компьютере, это другая VPN-программа: трафик приложения уйдёт через неё, а не через
/// подключение лаунчера, и фильтр по адаптеру этого не видит — соединение с 127.0.0.1 внутри компьютера.
/// </summary>
public static class SystemProxy
{
    private const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>Как часто перечитывать PAC-файл с этого компьютера.</summary>
    private static readonly TimeSpan PacFresh = TimeSpan.FromSeconds(30);

    /// <summary>«PROXY 127.0.0.1:10809», «SOCKS5 localhost:10808» и т. п. в тексте PAC-файла.</summary>
    private static readonly Regex PacProxy = new(@"\b(?:PROXY|HTTPS?|SOCKS[45]?)\s+([^\s;""'()]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly object PacSync = new();
    private static string? _pacUrl;
    private static DateTime _pacReadUtc;
    private static IReadOnlyList<int> _pacPorts = Array.Empty<int>();

    /// <summary>
    /// Прокси на этом компьютере, если он включён (null — нет или не на этом компьютере). PAC-файл с этого компьютера
    /// читается по сети (до 2 с), поэтому не из потока окна.
    /// </summary>
    public static LocalProxy? ReadLocal()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        bool enabled;
        string? server, pac;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (key is null)
                return null;
            enabled = key.GetValue("ProxyEnable") is int flag && flag != 0;
            server = key.GetValue("ProxyServer") as string;
            pac = key.GetValue("AutoConfigURL") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }

        return Parse(enabled, server, pac, PacPortsFor(pac));
    }

    /// <summary>
    /// «127.0.0.1:10808», «http=127.0.0.1:10809;https=…;socks=…», «http://localhost:8080» и адрес PAC-файла
    /// «http://127.0.0.1:10810/pac» → порты на этом компьютере.
    /// </summary>
    /// <param name="pacPorts">Порты прокси на этом компьютере, которые называет сам PAC-файл (<see cref="PacPorts"/>).</param>
    internal static LocalProxy? Parse(bool enabled, string? server, string? autoConfigUrl, IReadOnlyList<int>? pacPorts = null)
    {
        var shown = new List<string>();
        var ports = new SortedSet<int>();
        if (enabled && !string.IsNullOrWhiteSpace(server))
        {
            foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var address = part.Contains('=') ? part[(part.IndexOf('=') + 1)..].Trim() : part;
                if (LoopbackPort(address) is { } port && ports.Add(port))
                    shown.Add(Strip(address));
            }
        }

        if (!string.IsNullOrWhiteSpace(autoConfigUrl) && LoopbackPort(autoConfigUrl.Trim()) is { } pac)
        {
            if (ports.Add(pac))
                shown.Add(Strip(autoConfigUrl.Trim()));

            // Сам PAC-файл отправляет программы на другие порты (у v2rayN — 10809 и 10808): закрыть нужно и их.
            foreach (var port in pacPorts ?? Array.Empty<int>())
            {
                if (port is > 0 and <= 65535 && ports.Add(port))
                    shown.Add("127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            }
        }

        return ports.Count == 0 ? null : new LocalProxy(string.Join(", ", shown), ports.ToList());
    }

    /// <summary>Порты прокси на этом компьютере, которые называет текст PAC-файла.</summary>
    internal static IReadOnlyList<int> PacPorts(string script) =>
        PacProxy.Matches(script).Select(m => LoopbackPort(m.Groups[1].Value)).OfType<int>().Distinct().ToList();

    /// <summary>Порты из PAC-файла, если он на этом компьютере. Прочитанное помнится <see cref="PacFresh"/>.</summary>
    private static IReadOnlyList<int> PacPortsFor(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || LoopbackPort(url.Trim()) is null)
            return Array.Empty<int>();
        url = url.Trim();
        lock (PacSync)
        {
            if (url == _pacUrl && DateTime.UtcNow - _pacReadUtc < PacFresh)
                return _pacPorts;
        }

        var ports = FetchPacPorts(url);
        lock (PacSync)
        {
            if (ports is null)
                return url == _pacUrl ? _pacPorts : Array.Empty<int>(); // не прочитался — что знали раньше
            _pacUrl = url;
            _pacReadUtc = DateTime.UtcNow;
            _pacPorts = ports;
            return ports;
        }
    }

    /// <summary>Прочитать PAC-файл с этого компьютера (без прокси, до 2 с, до 1 МБ). null — не вышло.</summary>
    private static IReadOnlyList<int>? FetchPacPorts(string url)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 1024 * 1024 };
            return PacPorts(client.GetStringAsync(url).GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Порт, если адрес ведёт на этот компьютер. Без порта у прокси — 80 (так считает Windows).</summary>
    private static int? LoopbackPort(string address)
    {
        var text = address.Contains("://", StringComparison.Ordinal) ? address : "http://" + address;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return null;
        var host = uri.Host.Trim('[', ']');
        var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    IPAddress.TryParse(host, out var ip) && (IPAddress.IsLoopback(ip) || IsOwnAddress(ip));
        return local && uri.Port is > 0 and <= 65535 ? uri.Port : null;
    }

    /// <summary>Адрес одного из адаптеров этого компьютера: соединение с ним Windows тоже ведёт внутри компьютера.</summary>
    private static bool IsOwnAddress(IPAddress ip)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(a => a.Address.Equals(ip));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static string Strip(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        var rest = scheme >= 0 ? address[(scheme + 3)..] : address;
        var slash = rest.IndexOf('/');
        return slash >= 0 ? rest[..slash] : rest;
    }

    /// <summary>Ключ для сравнения: порты через запятую.</summary>
    internal static string Key(LocalProxy? proxy) =>
        proxy is null ? string.Empty : string.Join(",", proxy.Ports.Select(p => p.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>Следит за системным прокси: Windows не сообщает об изменении, поэтому раз в несколько секунд перечитывает.</summary>
public sealed class SystemProxyWatcher : IDisposable
{
    private readonly Func<LocalProxy?> _read;
    private readonly Timer _timer;
    private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _key;
    private LocalProxy? _current;
    private int _checking;

    public SystemProxyWatcher(Func<LocalProxy?>? read = null, TimeSpan? interval = null)
    {
        _read = read ?? SystemProxy.ReadLocal;
        _key = SystemProxy.Key(null);
        var period = interval ?? TimeSpan.FromSeconds(3);
        // Первое чтение — сразу, но в фоне: PAC-файл читается по сети.
        _timer = new Timer(_ => Check(), null, TimeSpan.Zero, period);
    }

    /// <summary>Сейчас включённый прокси на этом компьютере или null.</summary>
    public LocalProxy? Current => Volatile.Read(ref _current);

    /// <summary>Прокси включили, выключили или сменили порт. Поток таймера.</summary>
    public event Action? Changed;

    /// <summary>Дождаться первого чтения (не дольше <paramref name="timeout"/>): до него <see cref="Current"/> ещё пуст.</summary>
    public Task WaitFirstReadAsync(TimeSpan timeout) =>
        _firstRead.Task.IsCompleted ? Task.CompletedTask : Task.WhenAny(_firstRead.Task, Task.Delay(timeout));

    private void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0)
            return; // прошлое чтение ещё идёт
        try
        {
            Update();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Поток таймера: исключение здесь закрыло бы лаунчер (а с ним приложения). Прочитаем в следующий раз.
            System.Diagnostics.Debug.WriteLine("SystemProxyWatcher: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
            _firstRead.TrySetResult();
        }
    }

    private void Update()
    {
        var proxy = _read();
        var key = SystemProxy.Key(proxy);
        Volatile.Write(ref _current, proxy);
        if (Interlocked.Exchange(ref _key, key) != key)
            Changed?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
