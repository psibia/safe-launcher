using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>Приложение в настройках ядра: метка его процессов, подключение и защита.</summary>
public sealed record CoreApp(Guid AppId, string Name, string Tag, Guid ConnectionId, string ConnectionName, bool CloseOnDrop);

/// <summary>
/// Что должно делать ядро при текущих настройках: все приложения, которые ходят через подключения лаунчера, и эти
/// подключения. В план входят и не запущенные приложения: тогда запуск ещё одного приложения не требует перезапуска
/// ядра (а перезапуск на секунды оставляет уже работающие приложения без сети). Неизменяемый снимок.
/// </summary>
public sealed class CorePlan
{
    private CorePlan(CoreStartRequest request, IReadOnlyDictionary<Guid, CoreApp> apps, IReadOnlyDictionary<Guid, string> connectionTags,
        IReadOnlyDictionary<Guid, string> problems, string? dnsConnectionName)
    {
        Request = request;
        Apps = apps;
        ConnectionTags = connectionTags;
        Problems = problems;
        DnsConnectionName = dnsConnectionName;
        Fingerprint = JsonSerializer.Serialize(request, CoreServiceClient.JsonOptions);
    }

    public static readonly CorePlan Empty = new(new CoreStartRequest(), new Dictionary<Guid, CoreApp>(), new Dictionary<Guid, string>(),
        new Dictionary<Guid, string>(), null);

    /// <summary>Что отправить службе.</summary>
    public CoreStartRequest Request { get; }

    /// <summary>Приложения через ядро, по Id.</summary>
    public IReadOnlyDictionary<Guid, CoreApp> Apps { get; }

    /// <summary>Подключение → его tag в ядре.</summary>
    public IReadOnlyDictionary<Guid, string> ConnectionTags { get; }

    /// <summary>Приложения, которые должны идти через ядро, но не могут: почему (по-русски, для пользователя).</summary>
    public IReadOnlyDictionary<Guid, string> Problems { get; }

    /// <summary>Имя подключения, через которое идут редкие DNS-запросы (<see cref="CoreStartRequest.DnsOutbound"/>), или null.</summary>
    public string? DnsConnectionName { get; }

    /// <summary>Одинаковый у одинаковых планов: перезапускать ядро не нужно.</summary>
    public string Fingerprint { get; }

    public static string AppTag(Guid appId) => "a" + appId.ToString("N");

    public static string ConnectionTag(Guid connectionId) => "c" + connectionId.ToString("N");

    /// <param name="programs">Программы приложения для его процессов без метки; null — <see cref="Programs"/>.</param>
    public static CorePlan Build(AppSettings settings, bool ipv6, Func<AppEntry, IReadOnlyList<CoreProgram>>? programs = null)
    {
        programs ??= Programs;
        var request = new CoreStartRequest { Ipv6 = ipv6, HideDns = settings.HideDns };
        var apps = new Dictionary<Guid, CoreApp>();
        var tags = new Dictionary<Guid, string>();
        var problems = new Dictionary<Guid, string>();
        var broken = new Dictionary<Guid, string>();

        // Копии приложений, запущенные мимо лаунчера: ядро узнаёт их по exe-файлам приложения. Программы приложений под
        // системным VPN и без сети не в счёт: их процессы ядро тоже видит без метки, но это не копии приложений через ядро.
        // «Через лаунчер» для ядра — как «Через VPN»: копия идёт через подключение, пока лаунчер не возьмёт её под контроль.
        var outside = settings.Apps.Any(a => NetworkPlan.For(a, settings) is { UsesLauncherCore: true, OutsideCopies: not OutsideCopies.Allow });
        var systemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (outside)
        {
            foreach (var app in settings.Apps.Where(a => !NetworkPlan.For(a, settings).UsesLauncherCore))
                systemNames.UnionWith(programs(app).Select(p => p.Name));
        }

        foreach (var app in settings.Apps)
        {
            var plan = NetworkPlan.For(app, settings);
            if (!plan.UsesLauncherCore)
                continue;

            if (plan.Connection is not { } connection)
            {
                problems[app.Id] = plan.Source == NetworkPlanSource.Global
                    ? "Подключение лаунчера в общих настройках не выбрано. Выберите его в настройках."
                    : $"У «{app.Name}» не выбрано подключение. Выберите его в настройках приложения.";
                continue;
            }

            if (Add(connection) is not { } connectionTag)
            {
                problems[app.Id] = broken[connection.Id];
                continue;
            }

            var tag = AppTag(app.Id);
            var own = plan.OutsideCopies != OutsideCopies.Allow
                ? programs(app).Where(p => !systemNames.Contains(p.Name)).ToList()
                : new List<CoreProgram>();
            request.Apps.Add(new CoreAppRoute
            {
                Tag = tag,
                Outbound = connectionTag,
                DirectDomains = DomainList.Normalize(plan.DirectDomains),
                Programs = own.Count > 0 ? own : null,
                BlockOutside = own.Count > 0 && plan.OutsideCopies == OutsideCopies.Block
            });
            apps[app.Id] = new CoreApp(app.Id, app.Name, tag, connection.Id, connection.Name, plan.CloseOnDrop);
        }

        // С «Защитой от утечки DNS» запросы, на которые ядро не отвечает подменным адресом (редкие типы записей: TXT, SRV…),
        // идут через подключение из общих настроек, даже если через него не ходит ни одно приложение: иначе их видел бы
        // провайдер. С системным VPN в общих настройках или без выбранного подключения — к DNS компьютера.
        VpnConnection? dnsConnection = null;
        if (settings.HideDns && request.Apps.Count > 0 && settings.NetworkMode == GlobalNetworkMode.LauncherConnection &&
            settings.Connections.FirstOrDefault(c => c.Id == settings.ConnectionId) is { } main && Add(main) is not null)
            dnsConnection = main;

        // Порядок по меткам, а не по списку приложений: перестановка плиток не меняет отпечаток и не перезапускает ядро.
        request.Apps.Sort((x, y) => string.CompareOrdinal(x.Tag, y.Tag));
        request.Outbounds.Sort((x, y) => string.CompareOrdinal((string?)x["tag"], (string?)y["tag"]));
        MergeSameWireGuard(request, tags);
        if (request.OutboundDns is { } outboundDns)
            request.OutboundDns = outboundDns.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
        if (dnsConnection is not null)
            request.DnsOutbound = tags[dnsConnection.Id];

        return new CorePlan(request, apps, tags, problems, dnsConnection?.Name);

        // Подключение в ядре: его tag или null, если его не разобрать (причина — в broken).
        string? Add(VpnConnection connection)
        {
            if (tags.TryGetValue(connection.Id, out var connectionTag))
                return connectionTag;
            if (broken.ContainsKey(connection.Id))
                return null;

            JsonObject outbound;
            string? dns;
            try
            {
                (outbound, dns) = ConnectionOutbound.BuildWithDns(connection, ConnectionTag(connection.Id));
            }
            catch (ConnectionParseException ex)
            {
                broken[connection.Id] = ex.Message;
                return null;
            }

            connectionTag = ConnectionTag(connection.Id);
            tags[connection.Id] = connectionTag;
            request.Outbounds.Add(outbound);
            if (dns is not null)
                (request.OutboundDns ??= new Dictionary<string, string>())[connectionTag] = dns;
            return connectionTag;
        }
    }

    /// <summary>
    /// Программы приложения для его процессов без метки (<see cref="CoreAppRoute.Programs"/>): его exe в папке приложения и
    /// во вложенных папках, как процессы приложения узнаёт <see cref="AppFamily"/>. Папка общая (Загрузки, Program Files) —
    /// только сам exe. У приложения Microsoft Store — его программа в папке пакета. Без программ: общие «движки» (java,
    /// cmd…), программы с сетевых папок и приложения Store, программу которых не удалось найти. Программы Windows и её
    /// защитника служба ядру не передаёт (весь компьютер пошёл бы через VPN), поэтому их нет и здесь: копии таких программ
    /// идут напрямую, и лаунчер так и пишет.
    /// </summary>
    public static IReadOnlyList<CoreProgram> Programs(AppEntry entry)
    {
        var result = new List<CoreProgram>();
        var packaged = entry.IsPackaged ? PackagedApps.ExecutablePath(entry.AppUserModelId) : null;
        if (entry.IsPackaged ? packaged is null : string.IsNullOrWhiteSpace(entry.ExePath))
            return result;
        try
        {
            if ((packaged is not null ? AppFamily.ForImage(packaged) : AppFamily.For(entry)) is { } family)
            {
                foreach (var root in family.Roots)
                {
                    foreach (var name in family.Names)
                        Add(root, name, subfolders: true);
                }
            }
            else if (!AppFamily.IsGenericHost(Path.GetFileName(packaged ?? entry.ExePath)))
            {
                var exe = Path.GetFullPath(packaged ?? entry.ExePath);
                Add(Path.GetDirectoryName(exe), Path.GetFileName(exe), subfolders: false);
                if (AppFamily.GetFinalPath(exe) is { } final)
                    Add(Path.GetDirectoryName(final), Path.GetFileName(final), subfolders: false);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // путь не разобрать: без программ, копии идут напрямую
        }

        result.Sort((x, y) => string.Compare(x.Folder + "\\" + x.Name, y.Folder + "\\" + y.Name, StringComparison.OrdinalIgnoreCase));
        return result;

        void Add(string? folder, string name, bool subfolders)
        {
            folder = folder?.TrimEnd('\\');
            // Только папки на дисках (C:\…): процессы с сетевых папок ядро видит другими путями.
            if (folder is not { Length: >= 2 and <= 1000 } || !char.IsAsciiLetter(folder[0]) || folder[1] != ':' || folder.Length > 2 && folder[2] != '\\')
                return;
            if (SystemFolders.Any(system => (folder + "\\").StartsWith(system, StringComparison.OrdinalIgnoreCase)))
                return;
            if (!result.Any(p => string.Equals(p.Folder, folder, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                result.Add(new CoreProgram { Folder = folder, Name = name, Subfolders = subfolders });
        }
    }

    /// <summary>Папки Windows и её защитника, как у службы (NetworkGuard.SharedFolders), с «\\» в конце.</summary>
    private static readonly string[] SystemFolders = BuildSystemFolders();

    private static string[] BuildSystemFolders()
    {
        var folders = new List<string>();
        void Add(params string[] parts)
        {
            if (parts[0].Length > 0)
                folders.Add(Path.Combine(parts).TrimEnd('\\') + "\\");
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender Advanced Threat Protection");
        foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            Add(programFiles, "Windows Defender");
            Add(programFiles, "Windows Defender Advanced Threat Protection");
        }

        return folders.ToArray();
    }

    /// <summary>
    /// Одно и то же подключение WireGuard, добавленное дважды (тот же файл или ключ vpn://, в том числе с правками вроде MTU
    /// или DNS), идёт одним туннелем: для сервера это один клиент, и два туннеля с одним ключом отбивали бы друг у друга связь
    /// с ним, а соединения обрывались бы у приложений обоих подключений. Остаётся подключение с меньшей меткой, чтобы итог не
    /// зависел от порядка в настройках.
    /// </summary>
    private static void MergeSameWireGuard(CoreStartRequest request, Dictionary<Guid, string> tags)
    {
        var first = new Dictionary<string, string>(StringComparer.Ordinal);
        var replaced = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var outbound in request.Outbounds.ToList())
        {
            if (WireGuardIdentity(outbound) is not { } settings)
                continue;
            var tag = (string)outbound["tag"]!;
            if (first.TryGetValue(settings, out var kept))
            {
                replaced[tag] = kept;
                request.Outbounds.Remove(outbound);
                request.OutboundDns?.Remove(tag);
            }
            else
            {
                first[settings] = tag;
            }
        }

        if (replaced.Count == 0)
            return;
        foreach (var route in request.Apps)
        {
            if (replaced.TryGetValue(route.Outbound, out var kept))
                route.Outbound = kept;
        }
        foreach (var (connectionId, tag) in tags.ToList())
        {
            if (replaced.TryGetValue(tag, out var kept))
                tags[connectionId] = kept;
        }
        if (request.OutboundDns is { Count: 0 })
            request.OutboundDns = null;
    }

    /// <summary>Метка в ядре подключения WireGuard (и AmneziaWG) с теми же ключами, что у <paramref name="outbound"/>, или null.</summary>
    public string? WireGuardTag(JsonObject outbound)
    {
        if (WireGuardIdentity(outbound) is not { } identity)
            return null;
        return Request.Outbounds.Where(o => WireGuardIdentity(o) == identity).Select(o => (string?)o["tag"]).FirstOrDefault();
    }

    /// <summary>
    /// Клиент WireGuard для сервера: свой закрытый ключ, открытые ключи и адреса серверов. Туннели с одними ключами к одному
    /// адресу сервер считает одним клиентом. Остальные настройки (MTU, DNS, адрес в туннеле) на это не влияют. null — не
    /// WireGuard.
    /// </summary>
    private static string? WireGuardIdentity(JsonObject outbound)
    {
        if ((string?)outbound["type"] != "wireguard")
            return null;
        var peers = (outbound["peers"] as JsonArray ?? new JsonArray())
            .Select(p => $"{(string?)p?["public_key"]}@{p?["address"]?.ToString().ToLowerInvariant()}:{p?["port"]}")
            .OrderBy(k => k, StringComparer.Ordinal);
        return (string?)outbound["private_key"] + "|" + string.Join(",", peers);
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
