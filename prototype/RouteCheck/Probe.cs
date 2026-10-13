using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RouteCheck;

/// <summary>
/// «Подопытное приложение»: та же программа с ключом --probe. Пробует выйти в сеть пятью способами
/// (HTTP и TLS поверх TCP, DNS и NTP по UDP, системный DNS Windows) и печатает строку с итогом. Связь засчитывается только
/// по полученному ответу: одного установленного TCP-соединения мало, его может принять само ядро VPN. Кроме системного DNS,
/// адреса заданы IP, чтобы результат не зависел от разрешения имён. С --alt ходит на другие адреса тех же служб: так по журналу
/// тестового VPN-сервера видно, чьи соединения через него прошли. С --spawn-child запускает свою копию и передаёт её итог:
/// так проверяется, что под правило попадают и дочерние процессы.
/// </summary>
internal static class Probe
{
    public const string Argument = "--probe";
    public const string SpawnChild = "--spawn-child";
    public const string NoWait = "--no-wait";
    public const string Alt = "--alt";
    private const string ResultPrefix = "RESULT ";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>Способы выйти в сеть: ключ в строке RESULT и название для отчёта.</summary>
    public static readonly (string Key, string Name)[] Channels =
    {
        ("http", "HTTP"),
        ("tls", "TLS"),
        ("udp53", "DNS по UDP"),
        ("ntp", "время (NTP) по UDP"),
        ("sysdns", "системный DNS")
    };

    /// <summary>Куда ходит проба. Main — «чужая программа», Alt — «программа лаунчера».</summary>
    public sealed record Targets(IPEndPoint Http, IPEndPoint Tls, IPEndPoint Dns, IPEndPoint Ntp)
    {
        /// <summary>Имя сервера в TLS. Его же лаунчер указывает в исключениях (напрямую, мимо VPN).</summary>
        public const string TlsName = "dns.google";

        public IPEndPoint?[] ByChannel => new IPEndPoint?[] { Http, Tls, Dns, Ntp, null };
    }

    public static readonly Targets Main = new(
        new IPEndPoint(IPAddress.Parse("1.1.1.1"), 80),
        new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443),
        new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53),
        new IPEndPoint(IPAddress.Parse("162.159.200.123"), 123));

    public static readonly Targets Alternate = new(
        new IPEndPoint(IPAddress.Parse("1.0.0.1"), 80),
        new IPEndPoint(IPAddress.Parse("8.8.4.4"), 443),
        new IPEndPoint(IPAddress.Parse("1.0.0.1"), 53),
        new IPEndPoint(IPAddress.Parse("162.159.200.1"), 123));

    public static int Run(string[] args)
    {
        // Ждём команды: проверка успевает поместить процесс в задание до первого соединения.
        if (Array.IndexOf(args, NoWait) < 0)
            Console.In.ReadLine();

        var targets = Array.IndexOf(args, Alt) >= 0 ? Alternate : Main;
        var results = new[]
        {
            TryHttp(targets.Http),
            TryTls(targets.Tls, Targets.TlsName),
            TryDns(targets.Dns),
            TryNtp(targets.Ntp),
            TrySystemDns()
        };
        var parts = new StringBuilder($"{ResultPrefix}pid={Environment.ProcessId}");
        for (var i = 0; i < Channels.Length; i++)
            parts.Append($" {Channels[i].Key}={Mark(results[i])}");
        Console.WriteLine(parts);

        if (Array.IndexOf(args, SpawnChild) >= 0)
        {
            var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{Argument} {NoWait}" + (targets == Alternate ? " " + Alt : ""))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            })!;
            var line = child.StandardOutput.ReadLine();
            child.WaitForExit(20_000);
            Console.WriteLine("CHILD " + line);
        }

        return 0;
    }

    /// <summary>Итог одной проверки из строки RESULT: есть ли связь по каждому способу.</summary>
    public sealed record Outcome(int Pid, bool[] Ok)
    {
        public bool Has(int channel) => Ok[channel];

        public override string ToString() =>
            $"PID {Pid}: " + string.Join(", ", Channels.Select((c, i) => $"{c.Name} — {(Ok[i] ? "есть связь" : "нет связи")}"));
    }

    public static Outcome? Parse(string? line)
    {
        if (line is null)
            return null;
        var start = line.IndexOf(ResultPrefix, StringComparison.Ordinal);
        if (start < 0)
            return null;

        int pid = 0;
        var ok = new bool[Channels.Length];
        foreach (var part in line[(start + ResultPrefix.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "pid")
            {
                int.TryParse(kv[1], out pid);
                continue;
            }

            var index = Array.FindIndex(Channels, c => c.Key == kv[0]);
            if (index >= 0)
                ok[index] = kv[1] == "ok";
        }

        return new Outcome(pid, ok);
    }

    private static string Mark(bool ok) => ok ? "ok" : "fail";

    /// <summary>HTTP-запрос по IP; любой ответ сервера («HTTP/1.1 …») — связь есть.</summary>
    private static bool TryHttp(IPEndPoint target)
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            using var client = new TcpClient(target.AddressFamily);
            client.ConnectAsync(target, cts.Token).AsTask().GetAwaiter().GetResult();
            using var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {target.Address}\r\nConnection: close\r\n\r\n");
            stream.WriteAsync(request, cts.Token).AsTask().GetAwaiter().GetResult();
            var buffer = new byte[16];
            var read = stream.ReadAsync(buffer, cts.Token).AsTask().GetAwaiter().GetResult();
            return read >= 5 && Encoding.ASCII.GetString(buffer, 0, read).StartsWith("HTTP/", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>TLS-рукопожатие с сервером (сертификат не проверяется: важно только, что сервер ответил).</summary>
    private static bool TryTls(IPEndPoint target, string serverName)
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            using var client = new TcpClient(target.AddressFamily);
            client.ConnectAsync(target, cts.Token).AsTask().GetAwaiter().GetResult();
            using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = serverName }, cts.Token)
                .GetAwaiter().GetResult();
            return tls.IsAuthenticated;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Запрос A-записи example.com прямо к DNS-серверу; ответ с тем же номером запроса — связь есть.</summary>
    private static bool TryDns(IPEndPoint server)
    {
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            byte[] query =
            {
                (byte)(id >> 8), (byte)id, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
                3, (byte)'c', (byte)'o', (byte)'m', 0, 0x00, 0x01, 0x00, 0x01
            };
            udp.Send(query, query.Length, server);

            var receive = udp.ReceiveAsync();
            if (!receive.Wait(Timeout))
                return false;
            var answer = receive.Result.Buffer;
            return answer.Length >= 12 && answer[0] == query[0] && answer[1] == query[1];
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Запрос времени по NTP (UDP 123): другой UDP, не DNS. Ответ сервера — связь есть.</summary>
    private static bool TryNtp(IPEndPoint server)
    {
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            var query = new byte[48];
            query[0] = 0x23; // версия 4, режим «клиент»
            udp.Send(query, query.Length, server);

            var receive = udp.ReceiveAsync();
            if (!receive.Wait(Timeout))
                return false;
            var answer = receive.Result.Buffer;
            return answer.Length >= 48 && (answer[0] & 0x07) == 4;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Имя через системный DNS Windows, как ищут адреса обычные программы. Имя каждый раз новое, поэтому ответ
    /// не может взяться из кэша. Связь есть, если пришёл любой ответ сервера, в том числе «такого имени нет»;
    /// при недоступном DNS Windows сообщает о временной ошибке или не отвечает вовсе.
    /// </summary>
    private static bool TrySystemDns()
    {
        try
        {
            var lookup = Dns.GetHostAddressesAsync($"rc{Guid.NewGuid():N}.1.1.1.1.sslip.io");
            if (!lookup.Wait(TimeSpan.FromSeconds(6)))
                return false;
            return lookup.Result.Length > 0;
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData })
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
