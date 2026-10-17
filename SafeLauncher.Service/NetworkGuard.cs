using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using SafeLauncher.Logging;

namespace SafeLauncher.Service;

/// <summary>
/// Блокировка обхода VPN фильтрами Windows (WFP). Приложению лаунчера разрешается сеть только через его адаптеры
/// (адаптер ядра или адаптеры системного VPN) и внутри компьютера; всё остальное для его exe блокируется, даже если
/// программа сама выберет обычный адаптер (WebRTC в браузерах, привязка к адресу сетевой карты).
///
/// Фильтры обычные, не динамические: падение службы их не снимает, и до её перезапуска приложения остаются под
/// блокировкой. Windows удаляет их сама при перезагрузке. При обычной остановке их снимает служба, а при запуске после
/// падения заменяет оставшиеся одной транзакцией, без перерыва (<see cref="Restore"/>, наборы из файла состояния).
/// Фильтры, которые сохраняются после перезагрузки, не используются: ошибка оставила бы программу без сети навсегда.
/// Набор каждого лаунчера живёт, пока он подключён; что остаётся после его отключения, см. <see cref="GuardSets"/>.
/// Свой подуровень с наибольшим весом: наши запреты окончательны, а наши разрешения ничего не открывают сверх
/// разрешённого другими фильтрами (брандмауэр Windows и т. п. по-прежнему могут запретить).
/// </summary>
internal sealed class NetworkGuard : IDisposable
{
    private const int MaxRules = 64;
    private const int MaxPathsPerRule = 256;
    private const int MaxInterfaces = 32;
    private const int MaxPathLength = 1024;

    private const int MaxLoopbackPorts = 16;
    private const int MaxDnsPaths = 1024;

    // Веса внутри подуровня (0–15): побеждает совпавший фильтр с большим весом.
    private const byte WeightProxyBlock = 15;
    private const byte WeightLoopback = 14;
    private const byte WeightAppPermit = 13;
    private const byte WeightAppBlock = 12;
    private const byte WeightDnsPermit = 11;
    private const byte WeightDnsBlock = 10;

    /// <summary>Системные программы, сеть которых лаунчер не отключает: без них не работает сеть всего компьютера.</summary>
    internal static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "lsass.exe", "services.exe", "wininit.exe", "winlogon.exe", "csrss.exe", "smss.exe", "System"
    };

    /// <summary>
    /// Папки общих программ: Windows и её защитника. Фильтр действует на программу целиком, где бы она ни работала,
    /// поэтому ограничение, скажем, cmd.exe или powershell.exe затронуло бы и сценарии самой Windows.
    /// </summary>
    internal static readonly string[] SharedFolders = BuildSharedFolders();

    private static readonly Guid[] AppLayers =
    {
        Wfp.LayerConnectV4, Wfp.LayerConnectV6, Wfp.LayerRecvAcceptV4, Wfp.LayerRecvAcceptV6
    };

    private static readonly Guid[] ConnectLayers = { Wfp.LayerConnectV4, Wfp.LayerConnectV6 };

    /// <summary>Свой подуровень: ключ постоянный, чтобы служба после падения нашла в нём свои оставшиеся фильтры.</summary>
    private static readonly Guid SubLayerKey = new("3f8a8b42-3e0e-4e1c-9985-02c8bdcffa40");

    /// <summary>Как часто проверять, работают ли программы наборов, оставшихся от отключившихся лаунчеров.</summary>
    private static readonly TimeSpan OrphanCheckInterval = TimeSpan.FromSeconds(5);

    private readonly Action<string> _log;
    private readonly string? _statePath;
    private readonly object _sync = new();
    private readonly GuardSets _sets = new();
    private readonly List<ulong> _filters = new();
    private readonly string _ownDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
    private readonly System.Threading.Timer _orphanTimer;
    private IntPtr _engine;
    private IntPtr _displayName;
    private ulong? _coreLuid;

    /// <summary>В подуровне могут быть фильтры упавшей службы: следующая перестройка снимает всё, что в нём есть.</summary>
    private bool _leftovers = true;

    private bool _disposed;

    /// <param name="statePath">Файл состояния (наборы на случай падения службы); null — без него.</param>
    public NetworkGuard(Action<string> log, string? statePath = null)
    {
        _log = log;
        _statePath = statePath;
        _orphanTimer = new System.Threading.Timer(_ => CheckOrphans(), null, OrphanCheckInterval, OrphanCheckInterval);
    }

    /// <summary>
    /// Запуск службы. Если подуровень фильтров уже есть в Windows, прежняя служба упала, не сняв их: её фильтры заменяются
    /// наборами из файла состояния одной транзакцией. Правила, программы которых работают, действуют без перерыва
    /// (лаунчеры переподключатся и пришлют свои наборы), остальные снимаются. После перезагрузки и обычной остановки
    /// фильтров нет, и файл не нужен.
    /// </summary>
    public void Restore()
    {
        lock (_sync)
        {
            bool existed;
            try
            {
                existed = EnsureEngine();
            }
            catch (Exception ex) when (ex is WfpException or DllNotFoundException or EntryPointNotFoundException)
            {
                // Фильтры Windows не готовы (служба BFE ещё запускается): после перезагрузки оставшихся фильтров нет,
                // а следующая перестройка всё равно проверит подуровень.
                ServiceLog.Warn(LogAreas.Wfp, LogCodes.WfpFail, null, "Фильтры Windows при запуске службы: " + ex.Message);
                return;
            }

            if (!existed)
            {
                _leftovers = false;
                DeleteState();
                return;
            }

            var restored = LoadState();
            _sets.AddOrphans(restored);
            if (_sets.Orphans > 0)
                _sets.Prune(RunningCheck(), DnsCapableInterfaces().Contains);
            var kept = _sets.Orphans;
            var error = Rebuild();
            var text = "Служба запущена после сбоя: оставшиеся фильтры " +
                       (kept > 0 ? $"заменены наборами отключившихся лаунчеров ({kept} из {restored.Count}), пока работают их программы" : "сняты");
            if (error is null)
                ServiceLog.Info(LogAreas.Wfp, LogCodes.WfpApply, null, text);
            else
            {
                _log("Не удалось заменить фильтры, оставшиеся после сбоя службы: " + error);
                ServiceLog.Error(LogAreas.Wfp, LogCodes.WfpFail, null, "Не удалось заменить фильтры, оставшиеся после сбоя службы: " + error);
            }
        }
    }

    /// <summary>
    /// Заменить набор этого лаунчера. null — всё включено; иначе причина, почему фильтры не удалось поставить
    /// (тогда действуют прежние фильтры). Недопустимый запрос — <see cref="CoreConfigException"/>.
    /// </summary>
    /// <param name="user">SID пользователя лаунчера: его набор заменяет наборы, оставшиеся от его прежних лаунчеров.</param>
    /// <param name="unguarded">Программы, которые не удалось проверить и поэтому ограничить (сетевой диск и т. п.).</param>
    public string? Set(object owner, string? user, GuardRequest? request, out string? notice, out List<string> unguarded)
    {
        var clean = Validate(request, out notice, out unguarded);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var snapshot = _sets.Snapshot();
            _sets.Set(owner, user, clean);
            var error = Rebuild();
            if (error is not null)
                _sets.Restore(snapshot); // не вышло: прежнее состояние, как и фильтры в Windows

            return error;
        }
    }

    /// <summary>
    /// Лаунчер отключился (закрылся или упал): правила его приложений снимаются, а скрытие DNS остаётся, пока работают
    /// программы приложений, которые продолжают работать и без лаунчера (<see cref="GuardSets.Release"/>).
    /// </summary>
    public void Release(object owner)
    {
        lock (_sync)
        {
            if (_disposed || !_sets.Release(owner))
                return;
            if (Rebuild() is { } error)
            {
                _log("Не удалось обновить фильтры после отключения лаунчера: " + error);
                ServiceLog.Error(LogAreas.Wfp, LogCodes.WfpFail, null, "Не удалось обновить фильтры после отключения лаунчера: " + error);
            }
            else if (_sets.Orphans > 0)
            {
                ServiceLog.Info(LogAreas.Wfp, LogCodes.WfpApply, null,
                    "Лаунчер отключился: скрытие DNS остаётся, пока работают программы его приложений и подключён системный VPN");
            }
        }
    }

    /// <summary>
    /// Раз в <see cref="OrphanCheckInterval"/>: снять в наборах отключившихся лаунчеров правила закрывшихся программ и скрытие
    /// DNS, адаптера VPN которого больше нет (<see cref="GuardSets.Prune"/>).
    /// </summary>
    private void CheckOrphans()
    {
        try
        {
            lock (_sync)
            {
                if (_disposed || _sets.Orphans == 0)
                    return;
            }

            // списки процессов и адаптеров — без блокировки
            var running = RunningCheck();
            var dnsAdapters = DnsCapableInterfaces();
            lock (_sync)
            {
                if (_disposed || !_sets.Prune(running, dnsAdapters.Contains))
                    return;
                if (Rebuild() is { } error)
                    ServiceLog.Error(LogAreas.Wfp, LogCodes.WfpFail, null, "Не удалось снять фильтры закрывшихся программ: " + error);
                else
                    ServiceLog.Info(LogAreas.Wfp, LogCodes.WfpApply, null,
                        "Сняты фильтры, оставшиеся после отключения лаунчера: программы закрылись или отключился VPN" +
                        (_sets.Orphans > 0 ? $"; ещё действуют наборы: {_sets.Orphans}" : string.Empty));
            }
        }
        catch (Exception ex)
        {
            // таймер не должен ронять службу
            ServiceLog.Error(LogAreas.Wfp, LogCodes.WfpFail, null, "Проверка оставшихся фильтров: " + ex.Message);
        }
    }

    /// <summary>Работает ли сейчас программа с этим путём (путь — как в правиле: настоящий или путь устройства).</summary>
    private static Func<string, bool> RunningCheck()
    {
        var images = RunningImages();
        return path => images.Contains(path) || DevicePath(path) is { } device && images.Contains(device);
    }

    /// <summary>Адаптер ядра появился (LUID) или пропал (null): правила «через ядро» пересчитываются.</summary>
    public void SetCore(ulong? luid)
    {
        lock (_sync)
        {
            if (_disposed || _coreLuid == luid)
                return;
            _coreLuid = luid;
            if (_sets.IsEmpty)
                return;
            if (Rebuild() is { } error)
            {
                _log("Не удалось обновить фильтры под адаптер ядра: " + error);
                ServiceLog.Error(LogAreas.Wfp, LogCodes.WfpFail, null, "Не удалось обновить фильтры под адаптер ядра: " + error);
            }
        }
    }

    /// <summary>LUID адаптера ядра, если он есть.</summary>
    public static ulong? CoreInterfaceLuid() =>
        Wfp.ConvertInterfaceAliasToLuid(CoreConfigBuilder.InterfaceName, out var luid) == 0 ? luid : null;

    /// <summary>Проверка запроса от лаунчера (служба работает от имени системы). null — запрос пустой.</summary>
    private GuardRequest? Validate(GuardRequest? request, out string? notice, out List<string> unguarded)
    {
        notice = null;
        unguarded = new List<string>();
        if (request is null)
            return null;
        var apps = request.Apps ?? new List<GuardRule>();
        if (apps.Count > MaxRules)
            throw new CoreConfigException($"Слишком много приложений для блокировки: {apps.Count}.");
        if (request.DnsInterfaces is { Count: > MaxInterfaces })
            throw new CoreConfigException("Слишком много адаптеров для DNS.");

        var skipped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unavailable = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? servicePrograms = null;
        HashSet<string>? runningImages = null;
        var clean = new GuardRequest
        {
            // Только адаптеры, у которых есть свой DNS-сервер: иначе запрос (его может прислать любой пользователь
            // компьютера) оставил бы весь компьютер без имён.
            DnsInterfaces = request.DnsInterfaces is { Count: > 0 } dns
                ? dns.Where(i => i > 0).Distinct().Intersect(DnsCapableInterfaces()).ToList()
                : null
        };
        var unguardedPaths = unguarded;

        // Пути программ → настоящие пути, которые можно ограничить. Программы Windows и служб пропускаются (skipped),
        // непроверяемые (сетевой диск, нет доступа) — тоже (unavailable, unguarded).
        List<string> CleanPaths(List<string> paths)
        {
            var good = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxPathLength || raw.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(raw))
                    throw new CoreConfigException($"Недопустимый путь к программе: «{raw}».");
                if (!Path.GetFullPath(raw).EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new CoreConfigException($"Блокировать можно только программы (.exe): «{raw}».");
                if (!seen.Add(raw))
                    continue;

                if (raw.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    // Сетевая папка: служба её не проверит (и не должна ходить в сеть), а каким путём Windows запишет такую
                    // программу в фильтр, заранее не узнать.
                    unguardedPaths.Add(raw);
                    unavailable.Add(Path.GetFileName(raw));
                    continue;
                }

                // Проверяется настоящий файл: короткое имя или ссылка не выведут к программе Windows в обход проверки.
                var path = Native.FinalPath(raw, out var missing);
                if (path is null)
                {
                    if (!missing)
                    {
                        // Не проверить: диск лаунчера, которого служба не видит (сетевой), или нет доступа.
                        unguardedPaths.Add(raw);
                        unavailable.Add(Path.GetFileName(raw));
                        continue;
                    }

                    // Файла уже нет, но процесс, запущенный из него, может ещё работать (программу переименовало
                    // обновление): фильтр по пути, как его записала Windows. Проверка ниже — по этому же пути.
                    // Только если такой процесс есть: иначе любой пользователь мог бы заранее закрыть сеть программе,
                    // которую позже поставит установщик (например, новой версии службы обновления браузера).
                    path = Path.GetFullPath(raw);
                    runningImages ??= RunningImages();
                    if (!runningImages.Contains(path) && !(DevicePath(path) is { } device && runningImages.Contains(device)))
                        continue; // программы нет и она не работает: ограничивать нечего
                }

                if (path.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    // Ссылка на сетевую папку — как выше.
                    unguardedPaths.Add(raw);
                    unavailable.Add(Path.GetFileName(path));
                    continue;
                }

                if (good.Contains(path, StringComparer.OrdinalIgnoreCase))
                    continue;
                servicePrograms ??= ServicePrograms();
                if (IsShared(path) || servicePrograms.Contains(path))
                {
                    skipped.Add(Path.GetFileName(path));
                    continue;
                }

                good.Add(path);
            }

            return good;
        }

        foreach (var rule in apps)
        {
            if (rule is null)
                throw new CoreConfigException("Пустое правило блокировки.");
            var paths = rule.Paths ?? new List<string>();
            var interfaces = rule.Interfaces ?? new List<int>();
            if (paths.Count > MaxPathsPerRule)
                throw new CoreConfigException($"Слишком много программ в одном приложении: {paths.Count}.");
            if (interfaces.Count > MaxInterfaces)
                throw new CoreConfigException("Слишком много адаптеров в правиле блокировки.");
            if (rule.LoopbackPorts is { Count: > MaxLoopbackPorts } || rule.LoopbackPorts?.Any(p => p is < 1 or > 65535) == true)
                throw new CoreConfigException("Недопустимые порты прокси в правиле блокировки.");

            var good = CleanPaths(paths);
            if (good.Count > 0)
                clean.Apps.Add(new GuardRule
                {
                    Paths = good,
                    ViaCore = rule.ViaCore,
                    Interfaces = interfaces.Where(i => i > 0).Distinct().ToList(),
                    LoopbackPorts = rule.LoopbackPorts is { Count: > 0 } ports ? ports.Distinct().ToList() : null
                });
        }

        if (request.DnsPaths is { Count: > MaxDnsPaths })
            throw new CoreConfigException($"Слишком много программ для DNS: {request.DnsPaths.Count}.");
        if (clean.DnsInterfaces is { Count: > 0 } && request.DnsPaths is { Count: > 0 } dnsPaths)
            clean.DnsPaths = CleanPaths(dnsPaths);

        var notices = new List<string>();
        if (skipped.Count > 0)
            notices.Add("Не ограничиваются программы Windows и программы служб: ограничение затронуло бы " +
                        "весь компьютер. Их трафик идёт как обычно: " + string.Join(", ", skipped) + ".");
        if (unavailable.Count > 0)
            notices.Add("Не удалось ограничить программы с сетевого диска или недоступные службе лаунчера: " +
                        string.Join(", ", unavailable) + ".");
        notice = notices.Count > 0 ? string.Join(" ", notices) : null;
        return clean.Apps.Count == 0 && clean.DnsInterfaces is not { Count: > 0 } ? null : clean;
    }

    /// <summary>Индексы работающих адаптеров со своим DNS-сервером (fec0:0:0:ffff::1–3 Windows подставляет сама).</summary>
    private static HashSet<int> DnsCapableInterfaces()
    {
        var result = new HashSet<int>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var properties = nic.GetIPProperties();
                if (!properties.DnsAddresses.Any(a => !(a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6SiteLocal)))
                    continue;
                if (nic.Supports(NetworkInterfaceComponent.IPv4) && properties.GetIPv4Properties() is { } v4)
                    result.Add(v4.Index);
                if (nic.Supports(NetworkInterfaceComponent.IPv6) && properties.GetIPv6Properties() is { } v6)
                    result.Add(v6.Index);
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // без списка DNS не ограничиваем
        }

        return result;
    }

    /// <summary>Программа общая для всей системы: Windows, её защитник, сама служба или её ядро.</summary>
    private bool IsShared(string path) =>
        ProtectedNames.Contains(Path.GetFileName(path)) ||
        path.StartsWith(_ownDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        SharedFolders.Any(folder => path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));

    private static string[] BuildSharedFolders()
    {
        var folders = new List<string>();
        void Add(params string[] parts)
        {
            if (parts[0].Length > 0)
                folders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(parts))) + Path.DirectorySeparatorChar);
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
    /// Программы служб, их сеть не трогаем: работающие сейчас в сеансе 0 и зарегистрированные в Windows, пусть и
    /// остановленные (служба может запуститься, пока фильтр ещё стоит).
    /// </summary>
    internal static HashSet<string> ServicePrograms()
    {
        var result = new HashSet<string>(RegisteredServicePrograms(), StringComparer.OrdinalIgnoreCase);
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != 0 || process.Id <= 4 || Native.ProcessImagePath(process.Id) is not { } image)
                        continue;
                    result.Add(image);
                    if (image.Contains('~') && Native.FinalPath(image) is { } final)
                        result.Add(final); // путь службы с коротким именем (PROGRA~1)
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // процесс уже завершился
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Файлы программ, которые сейчас работают вне сеанса служб: путь, как его показывает Windows, и путь устройства,
    /// запомненный при запуске (он не меняется, если файл переименовали).
    /// </summary>
    private static HashSet<string> RunningImages()
    {
        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == 0 || process.Id <= 4)
                        continue;
                    if (Native.ProcessImagePath(process.Id) is { } image)
                        images.Add(image);
                    if (Native.ProcessImagePath(process.Id, native: true) is { } device)
                        images.Add(device);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // процесс уже завершился
                }
            }
        }

        return images;
    }

    private static readonly object ServiceListSync = new();
    private static DateTime _serviceListUtc;
    private static List<string> _serviceList = new();

    /// <summary>Программы служб из реестра (кроме папки Windows: она защищена и так). Список помнится 30 с.</summary>
    private static List<string> RegisteredServicePrograms()
    {
        lock (ServiceListSync)
        {
            if (DateTime.UtcNow - _serviceListUtc < TimeSpan.FromSeconds(30))
                return _serviceList;
        }

        var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
        var list = new List<string>();
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var name in services?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try
                {
                    using var key = services!.OpenSubKey(name);
                    // Только службы-программы (свой или общий процесс), не драйверы.
                    if (key?.GetValue("Type") is not int type || (type & 0x30) == 0 ||
                        ServiceImage(key.GetValue("ImagePath") as string) is not { } image ||
                        image.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                        continue;
                    list.Add(image);
                    if (Native.FinalPath(image) is { } final && !string.Equals(final, image, StringComparison.OrdinalIgnoreCase))
                        list.Add(final); // короткое имя (PROGRA~1) или ссылка
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // ключ недоступен — пропускаем
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // список служб недоступен: остаются работающие
        }

        lock (ServiceListSync)
        {
            _serviceList = list;
            _serviceListUtc = DateTime.UtcNow;
            return list;
        }
    }

    /// <summary>
    /// Путь к exe из ImagePath службы: «"C:\Program Files\X\x.exe" -k», «C:\X\x.exe /service», «\??\C:\X\x.exe».
    /// null — путь не полный (так пишут программы Windows: «system32\svchost.exe») или не разобрать.
    /// </summary>
    internal static string? ServiceImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;
        var text = imagePath.Trim();
        string candidate;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0)
                return null;
            candidate = text[1..end];
        }
        else
        {
            // Без кавычек путь кончается на «.exe», за которым пробел или конец: «C:\X.exe.d\svc.exe -k» → …\svc.exe.
            var exe = -1;
            for (var at = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = text.IndexOf(".exe", at + 1, StringComparison.OrdinalIgnoreCase))
            {
                if (at + 4 == text.Length || char.IsWhiteSpace(text[at + 4]))
                {
                    exe = at;
                    break;
                }
            }

            if (exe < 0)
                return null;
            candidate = text[..(exe + 4)];
        }

        if (candidate.StartsWith(@"\??\", StringComparison.Ordinal))
            candidate = candidate[4..];
        if (candidate.Length > MaxPathLength || !Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\", StringComparison.Ordinal))
            return null;
        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Снять все наши фильтры и поставить заново по всем наборам — одной транзакцией. null — получилось.
    /// Первая перестройка после запуска снимает всё, что есть в нашем подуровне: там могут быть фильтры упавшей службы.
    /// </summary>
    private string? Rebuild()
    {
        if (_sets.IsEmpty && _filters.Count == 0 && !_leftovers)
            return null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var before = _filters.Count;
        string step = "открытие фильтров Windows";
        var memory = new Unmanaged();
        try
        {
            EnsureEngine();
            List<ulong> previous = _filters;
            if (_leftovers)
            {
                step = "поиск фильтров, оставшихся после сбоя службы";
                previous = OwnFilters();
                before = previous.Count;
            }

            step = "начало изменения фильтров";
            Check(Wfp.FwpmTransactionBegin0(_engine, 0));
            var added = new List<ulong>();
            try
            {
                step = "снятие прежних фильтров";
                foreach (var id in previous)
                    Wfp.FwpmFilterDeleteById0(_engine, id); // уже нет — не важно

                if (!_sets.IsEmpty)
                {
                    step = "разрешение сети внутри компьютера";
                    var loopback = memory.Conditions(new[] { (Wfp.ConditionFlags, Wfp.MatchFlagsAllSet, Wfp.TypeUint32, (IntPtr)Wfp.ConditionFlagIsLoopback) });
                    foreach (var layer in AppLayers)
                        added.Add(AddFilter(layer, WeightLoopback, Wfp.ActionPermit, loopback, 1));
                }

                foreach (var rule in _sets.All.SelectMany(r => r.Apps))
                {
                    step = "правила приложения " + Path.GetFileName(rule.Paths[0]);
                    var appIds = rule.Paths.SelectMany(AppIds).Distinct(ByteArrayComparer.Instance).ToList();
                    if (appIds.Count == 0)
                        continue;
                    var luids = rule.ViaCore
                        ? (_coreLuid is { } core ? new List<ulong> { core } : new List<ulong>())
                        : Luids(rule.Interfaces);

                    var appConditions = appIds.Select(id => (Wfp.ConditionAppId, Wfp.MatchEqual, Wfp.TypeByteBlob, memory.Blob(id))).ToList();
                    var block = memory.Conditions(appConditions);
                    var permit = luids.Count == 0
                        ? IntPtr.Zero
                        : memory.Conditions(appConditions.Concat(luids.Select(l => (Wfp.ConditionLocalInterface, Wfp.MatchEqual, Wfp.TypeUint64, memory.UInt64(l)))).ToList());
                    foreach (var layer in AppLayers)
                    {
                        if (permit != IntPtr.Zero)
                            added.Add(AddFilter(layer, WeightAppPermit, Wfp.ActionPermit, permit, appConditions.Count + luids.Count));
                        added.Add(AddFilter(layer, WeightAppBlock, Wfp.ActionBlock, block, appConditions.Count));
                    }

                    if (rule.LoopbackPorts is { Count: > 0 } proxyPorts)
                    {
                        // Системный прокси другой VPN-программы: соединение с ним внутри компьютера, а дальше трафик
                        // уходит мимо адаптера. Закрыт только он, остальное внутри компьютера открыто.
                        var proxy = memory.Conditions(appConditions
                            .Append((Wfp.ConditionFlags, Wfp.MatchFlagsAllSet, Wfp.TypeUint32, (IntPtr)Wfp.ConditionFlagIsLoopback))
                            .Concat(proxyPorts.Select(p => (Wfp.ConditionRemotePort, Wfp.MatchEqual, Wfp.TypeUint16, (IntPtr)p)))
                            .ToList());
                        foreach (var layer in ConnectLayers)
                            added.Add(AddFilter(layer, WeightProxyBlock, Wfp.ActionBlock, proxy, appConditions.Count + 1 + proxyPorts.Count));
                    }
                }

                var dnsIndexes = _sets.All.Where(r => r.DnsInterfaces is { Count: > 0 }).SelectMany(r => r.DnsInterfaces!).Distinct().ToList();
                if (dnsIndexes.Count > 0)
                {
                    // Без адаптера VPN DNS не закрываем: иначе на компьютере пропадут все имена (и VPN не переподключится).
                    var dnsLuids = Luids(dnsIndexes);
                    if (dnsLuids.Count > 0)
                    {
                        step = "правила DNS";
                        if (_coreLuid is { } core)
                            dnsLuids.Add(core);
                        var port = (Wfp.ConditionRemotePort, Wfp.MatchEqual, Wfp.TypeUint16, (IntPtr)53);
                        // Закрывается DNS службы DNS Windows (она спрашивает имена за все программы, и по запросу не понять,
                        // чей он) и приложений лаунчера, которые спрашивают сами (DnsPaths; с «Блокировать трафик мимо VPN»
                        // это делает их фильтр). Остальные программы со своим DNS не трогаем: ядро VPN-программы (и ядро
                        // лаунчера) ищет так адрес своего сервера и без этого не переподключится.
                        var dnsClient = AppIds(Path.Combine(Environment.SystemDirectory, "svchost.exe")).ToList();
                        var dnsPrograms = dnsClient.Count == 0
                            ? new List<byte[]>() // не узнали службу — DNS всех программ, как надёжнее
                            : dnsClient.Concat(_sets.All.SelectMany(r => r.DnsPaths ?? new List<string>()).SelectMany(AppIds))
                                .Distinct(ByteArrayComparer.Instance).ToList();
                        var dnsApps = dnsPrograms.Select(id => (Wfp.ConditionAppId, Wfp.MatchEqual, Wfp.TypeByteBlob, memory.Blob(id))).ToList();
                        var dnsBlock = memory.Conditions(new[] { port }.Concat(dnsApps).ToList());
                        var dnsPermit = memory.Conditions(new[] { port }
                            .Concat(dnsLuids.Select(l => (Wfp.ConditionLocalInterface, Wfp.MatchEqual, Wfp.TypeUint64, memory.UInt64(l)))).ToList());
                        foreach (var layer in ConnectLayers)
                        {
                            added.Add(AddFilter(layer, WeightDnsPermit, Wfp.ActionPermit, dnsPermit, 1 + dnsLuids.Count));
                            added.Add(AddFilter(layer, WeightDnsBlock, Wfp.ActionBlock, dnsBlock, 1 + dnsApps.Count));
                        }
                    }
                }

                step = "применение фильтров";
                Check(Wfp.FwpmTransactionCommit0(_engine));
            }
            catch
            {
                Wfp.FwpmTransactionAbort0(_engine);
                throw;
            }

            _filters.Clear();
            _filters.AddRange(added);
            _leftovers = false;
            SaveState();
            var rules = _sets.All.SelectMany(r => r.Apps).ToList();
            ServiceLog.Info(LogAreas.Wfp, LogCodes.WfpApply, null,
                $"Фильтры Windows: лаунчеров {_sets.Owners}" + (_sets.Orphans > 0 ? $", наборов отключившихся лаунчеров {_sets.Orphans}" : string.Empty) +
                $", правил {rules.Count} (через ядро {rules.Count(r => r.ViaCore)}), " +
                $"путей {rules.Sum(r => r.Paths.Count)}, адаптер ядра {(_coreLuid is { } c ? "LUID " + c : "нет")}, " +
                $"DNS-адаптеров {_sets.All.Where(r => r.DnsInterfaces is { Count: > 0 }).SelectMany(r => r.DnsInterfaces!).Distinct().Count()}, " +
                $"фильтров {added.Count} (было {before}), {watch.ElapsedMilliseconds} мс");
            if (ServiceLog.Detailed)
            {
                foreach (var rule in rules)
                {
                    ServiceLog.Debug(LogAreas.Wfp, LogCodes.WfpApply, null,
                        $"Правило: {(rule.ViaCore ? "через ядро" : "адаптеры [" + string.Join(",", rule.Interfaces) + "]")}, " +
                        $"программы: {string.Join(", ", rule.Paths)}");
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is WfpException or DllNotFoundException or EntryPointNotFoundException)
        {
            _log($"Фильтры Windows: {step}: {ex.Message}");
            ServiceLog.Error(LogAreas.Wfp, LogCodes.WfpFail, null, $"Фильтры Windows: {step}: {ex.Message}");
            return $"фильтры Windows не приняли правила ({step}: {ex.Message})";
        }
        finally
        {
            memory.Dispose();
        }
    }

    private ulong AddFilter(Guid layer, byte weight, uint action, IntPtr conditions, int count)
    {
        var filter = new Wfp.Filter
        {
            DisplayData = new Wfp.DisplayData { Name = _displayName },
            LayerKey = layer,
            SubLayerKey = SubLayerKey,
            Weight = new Wfp.Value { Type = Wfp.TypeUint8, Data = (IntPtr)weight },
            NumFilterConditions = (uint)count,
            FilterCondition = conditions,
            ActionType = action
        };
        Check(Wfp.FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out var id));
        return id;
    }

    /// <summary>
    /// Открыть фильтры Windows (обычный сеанс: его фильтры переживают падение службы) и добавить свой подуровень.
    /// true — подуровень уже был: служба упала, не сняв свои фильтры (после перезагрузки его нет).
    /// </summary>
    private bool EnsureEngine()
    {
        if (_engine != IntPtr.Zero)
            return false;
        if (_displayName == IntPtr.Zero)
            _displayName = Marshal.StringToHGlobalUni("Безопасный лаунчер: сеть приложений только через VPN");
        var session = new Wfp.Session { DisplayData = new Wfp.DisplayData { Name = _displayName } };
        Check(Wfp.FwpmEngineOpen0(null, Wfp.RpcAuthnDefault, IntPtr.Zero, ref session, out var engine));
        var subLayer = new Wfp.SubLayer
        {
            SubLayerKey = SubLayerKey,
            DisplayData = new Wfp.DisplayData { Name = _displayName },
            Weight = ushort.MaxValue
        };
        var result = Wfp.FwpmSubLayerAdd0(engine, ref subLayer, IntPtr.Zero);
        if (result != 0 && result != Wfp.ErrorAlreadyExists)
        {
            Wfp.FwpmEngineClose0(engine);
            Check(result);
        }

        _engine = engine;
        return result == Wfp.ErrorAlreadyExists;
    }

    /// <summary>Все фильтры в нашем подуровне, в том числе оставшиеся от упавшей службы.</summary>
    private List<ulong> OwnFilters()
    {
        var ids = new List<ulong>();
        Check(Wfp.FwpmFilterCreateEnumHandle0(_engine, IntPtr.Zero, out var enumHandle));
        try
        {
            const uint batch = 256;
            while (true)
            {
                Check(Wfp.FwpmFilterEnum0(_engine, enumHandle, batch, out var entries, out var count));
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var filter = Marshal.PtrToStructure<Wfp.Filter>(Marshal.ReadIntPtr(entries, i * IntPtr.Size));
                        if (filter.SubLayerKey == SubLayerKey)
                            ids.Add(filter.FilterId);
                    }
                }
                finally
                {
                    if (entries != IntPtr.Zero)
                        Wfp.FwpmFreeMemory0(ref entries);
                }

                if (count < batch)
                    break;
            }
        }
        finally
        {
            Wfp.FwpmFilterDestroyEnumHandle0(_engine, enumHandle);
        }

        return ids;
    }

    // ---- файл состояния: наборы на случай падения службы ----

    private void SaveState()
    {
        if (_statePath is null)
            return;
        try
        {
            if (_sets.IsEmpty)
            {
                DeleteState();
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, _sets.Serialize(), new UTF8Encoding(false));
            File.Move(temp, _statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn(LogAreas.Wfp, LogCodes.WfpFail, null, "Не удалось сохранить наборы фильтров на случай сбоя: " + ex.Message);
        }
    }

    private List<GuardSets.Entry> LoadState()
    {
        try
        {
            return _statePath is not null && File.Exists(_statePath)
                ? GuardSets.Deserialize(File.ReadAllText(_statePath))
                : new List<GuardSets.Entry>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn(LogAreas.Wfp, LogCodes.WfpFail, null, "Не удалось прочитать наборы фильтров после сбоя: " + ex.Message);
            return new List<GuardSets.Entry>();
        }
    }

    private void DeleteState()
    {
        if (_statePath is null)
            return;
        try
        {
            File.Delete(_statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не страшно: при следующем запуске без оставшихся фильтров файл не читается
        }
    }

    /// <summary>
    /// Идентификаторы программы для фильтра: как их считает Windows и (на случай русских букв в пути) свой вариант —
    /// путь устройства в нижнем регистре.
    /// </summary>
    private static IEnumerable<byte[]> AppIds(string path)
    {
        var result = new List<byte[]>();
        if (Wfp.FwpmGetAppIdFromFileName0(path, out var blobPointer) == 0 && blobPointer != IntPtr.Zero)
        {
            try
            {
                var blob = Marshal.PtrToStructure<Wfp.ByteBlob>(blobPointer);
                var bytes = new byte[blob.Size];
                Marshal.Copy(blob.Data, bytes, 0, bytes.Length);
                result.Add(bytes);
            }
            finally
            {
                Wfp.FwpmFreeMemory0(ref blobPointer);
            }
        }

        if (DevicePath(path) is { } device)
            result.Add(Encoding.Unicode.GetBytes(device.ToLowerInvariant() + "\0"));
        return result;
    }

    /// <summary>C:\Путь\app.exe → \device\harddiskvolume3\путь\app.exe (без перевода регистра).</summary>
    internal static string? DevicePath(string path)
    {
        if (path.Length < 3 || path[1] != ':' || path[2] != '\\')
            return null;
        var buffer = new char[1024];
        var length = Wfp.QueryDosDeviceW(path[..2], buffer, (uint)buffer.Length);
        if (length == 0)
            return null;
        var device = new string(buffer, 0, (int)length).Split('\0')[0];
        return device.Length == 0 ? null : device + path[2..];
    }

    /// <summary>
    /// Индексы адаптеров → LUID для фильтров. Несуществующие и петля (127.0.0.1) пропускаются: внутри компьютера сеть
    /// и так разрешена, а «DNS только через петлю» оставил бы весь компьютер без имён.
    /// </summary>
    private static List<ulong> Luids(IEnumerable<int> indexes)
    {
        const ulong softwareLoopback = 24; // IF_TYPE_SOFTWARE_LOOPBACK, хранится в старших 16 битах LUID
        var luids = new List<ulong>();
        foreach (var index in indexes)
        {
            if (Wfp.ConvertInterfaceIndexToLuid((uint)index, out var luid) == 0 && luid >> 48 != softwareLoopback && !luids.Contains(luid))
                luids.Add(luid);
        }

        return luids;
    }

    private static void Check(uint result)
    {
        if (result != 0)
            throw new WfpException(result);
    }

    /// <summary>Служба останавливается: все наши фильтры и подуровень снимаются (сами они исчезли бы только при перезагрузке).</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _orphanTimer.Dispose();
            if (_engine != IntPtr.Zero)
            {
                try
                {
                    var ids = _leftovers ? OwnFilters() : _filters;
                    Check(Wfp.FwpmTransactionBegin0(_engine, 0));
                    try
                    {
                        foreach (var id in ids)
                            Wfp.FwpmFilterDeleteById0(_engine, id);
                        Check(Wfp.FwpmTransactionCommit0(_engine));
                    }
                    catch
                    {
                        Wfp.FwpmTransactionAbort0(_engine);
                        throw;
                    }

                    var key = SubLayerKey;
                    Wfp.FwpmSubLayerDeleteByKey0(_engine, ref key);
                    _sets.Clear();
                    DeleteState();
                }
                catch (Exception ex) when (ex is WfpException or DllNotFoundException or EntryPointNotFoundException)
                {
                    _log("Не удалось снять фильтры при остановке службы: " + ex.Message);
                    ServiceLog.Error(LogAreas.Wfp, LogCodes.WfpFail, null, "Не удалось снять фильтры при остановке службы: " + ex.Message);
                }

                Wfp.FwpmEngineClose0(_engine);
            }

            _filters.Clear();
            _engine = IntPtr.Zero;
            if (_displayName != IntPtr.Zero)
                Marshal.FreeHGlobal(_displayName);
            _displayName = IntPtr.Zero;
        }
    }

    private sealed class WfpException : Exception
    {
        public WfpException(uint code) : base(Describe(code))
        {
        }

        private static string Describe(uint code) => code switch
        {
            0x80320001 => "служба фильтров Windows не готова (код 0x80320001)",
            0x80070005 => "нет прав (код 0x80070005)",
            0x800706D9 => "служба «Модуль фильтрации базовой системы» не запущена (код 0x800706D9)",
            _ => $"код 0x{code:X8}"
        };
    }

    /// <summary>Память для условий фильтров: живёт до конца одной перестройки.</summary>
    private sealed class Unmanaged : IDisposable
    {
        private readonly List<IntPtr> _blocks = new();

        private IntPtr Alloc(int size)
        {
            var block = Marshal.AllocHGlobal(size);
            _blocks.Add(block);
            return block;
        }

        public IntPtr Blob(byte[] data)
        {
            var bytes = Alloc(data.Length);
            Marshal.Copy(data, 0, bytes, data.Length);
            var blob = Alloc(Marshal.SizeOf<Wfp.ByteBlob>());
            Marshal.StructureToPtr(new Wfp.ByteBlob { Size = (uint)data.Length, Data = bytes }, blob, false);
            return blob;
        }

        public IntPtr UInt64(ulong value)
        {
            var block = Alloc(sizeof(ulong));
            Marshal.WriteInt64(block, (long)value);
            return block;
        }

        public IntPtr Conditions(IReadOnlyList<(Guid Field, uint Match, uint Type, IntPtr Value)> conditions)
        {
            var size = Marshal.SizeOf<Wfp.FilterCondition>();
            var array = Alloc(size * conditions.Count);
            for (var i = 0; i < conditions.Count; i++)
            {
                var (field, match, type, value) = conditions[i];
                Marshal.StructureToPtr(new Wfp.FilterCondition
                {
                    FieldKey = field,
                    MatchType = match,
                    ConditionValue = new Wfp.Value { Type = type, Data = value }
                }, array + i * size, false);
            }

            return array;
        }

        public void Dispose()
        {
            foreach (var block in _blocks)
                Marshal.FreeHGlobal(block);
            _blocks.Clear();
        }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
