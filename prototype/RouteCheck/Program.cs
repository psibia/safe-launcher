using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using KillSwitchLauncher.Services;
using Microsoft.Win32.SafeHandles;

namespace RouteCheck;

/// <summary>
/// Проверка службы и ядра так, как ими будет пользоваться лаунчер: без прав администратора, через службу
/// «Безопасный лаунчер — сеть». Запускается двойным щелчком, сама проходит этапы и пишет вердикт на экран
/// и в RouteCheck-report.txt. VPN-сервер не нужен: его изображает вторая копия ядра на этом компьютере (<see cref="TestServer"/>).
///
/// A. Без ядра: связь есть у всех, по каким способам — дальше учитываются только они.
/// B. Ядро запущено службой: программа лаунчера и её дочерний процесс идут через тестовый сервер, кроме исключения
///    (dns.google — напрямую), чужая программа — напрямую.
/// C. Сервер «выключен»: у программы лаунчера нет сети (кроме исключения), у чужой программы всё работает.
/// D. Вторая копия лаунчера не может перехватить ядро.
/// E. Выход из лаунчера: ядро закрылось само, адаптер исчез.
/// F. Падение лаунчера: служба сама останавливает ядро.
/// </summary>
internal static class Program
{
    private const string Tag = "routecheck";
    private const string ConnectionTag = "test-vpn";
    private const string HoldArgument = "--hold";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Системный DNS выполняет служба Windows (svchost), а не сама программа, поэтому по процессу такие запросы
    /// к приложению не отнести. Для программы лаунчера этот способ только показываем.
    /// </summary>
    private static readonly int SystemDns = Array.FindIndex(Probe.Channels, c => c.Key == "sysdns");

    private static readonly int Tls = Array.FindIndex(Probe.Channels, c => c.Key == "tls");

    private static int Main(string[] args)
    {
        if (Array.IndexOf(args, Probe.Argument) >= 0)
            return Probe.Run(args);
        if (Array.IndexOf(args, HoldArgument) >= 0)
            return Hold();
        if (Array.IndexOf(args, ServiceInstall.InstallArgument) >= 0)
            return IsAdmin() ? ServiceInstall.Install() : Elevate(ServiceInstall.InstallArgument);
        if (Array.IndexOf(args, ServiceInstall.UninstallArgument) >= 0)
            return IsAdmin() ? ServiceInstall.Uninstall() : Elevate(ServiceInstall.UninstallArgument);

        Console.Title = "Безопасный лаунчер — проверка службы и маршрута";
        var report = new Report();
        try
        {
            Run(report);
        }
        catch (Exception ex)
        {
            report.Check(false, "Проверка прервалась из-за ошибки", ex.ToString());
        }

        var path = Path.Combine(AppContext.BaseDirectory, "RouteCheck-report.txt");
        try
        {
            report.Save(path);
            Console.WriteLine();
            Console.WriteLine("Отчёт сохранён: " + path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Не удалось сохранить отчёт: " + ex.Message);
        }

        Console.WriteLine();
        Console.WriteLine("Нажмите Enter, чтобы закрыть окно.");
        Console.ReadLine();
        return report.Passed ? 0 : 1;
    }

    /// <summary>Установка или удаление службы вручную: RouteCheck.exe --install-service / --uninstall-service.</summary>
    private static int Elevate(string argument)
    {
        var (ok, text) = ServiceInstall.RunElevated(argument);
        Console.WriteLine(text);
        return ok ? 0 : 1;
    }

    private static void Run(Report report)
    {
        report.Info($"RouteCheck {typeof(Program).Assembly.GetName().Version}, Windows {Environment.OSVersion.Version}, " +
                    $"{(Environment.Is64BitOperatingSystem ? "64" : "32")} бит, запуск {DateTime.Now:yyyy-MM-dd HH:mm:ss}" +
                    (IsAdmin() ? ", с правами администратора" : ", с обычными правами (как лаунчер)"));
        report.Info("Сетевые адаптеры:" + Environment.NewLine + string.Join(Environment.NewLine, Adapters()));

        // Служба
        report.Stage("Служба «Безопасный лаунчер — сеть»");
        var why = ServiceInstall.WhyInstall();
        if (why is null)
        {
            report.Check(true, "Служба установлена и работает", "Установлена именно эта версия службы и ядра.");
        }
        else
        {
            Console.WriteLine($"Нужно установить службу ({why}). Windows спросит разрешение — согласитесь.");
            var (ok, text) = ServiceInstall.RunElevated(ServiceInstall.InstallArgument);
            var running = ok && WaitFor(() => CoreServiceClient.QueryService().Running, TimeSpan.FromSeconds(20));
            report.Check(running, "Служба установлена и запущена", $"Причина установки: {why}." + Environment.NewLine + "       " + text.Replace(Environment.NewLine, Environment.NewLine + "       "));
            if (!running)
                return;
        }

        if (InterfaceExists())
            report.Info($"Адаптер «{InterfaceName}» уже был в системе до начала проверки (остался от прошлого запуска).");

        var work = Path.Combine(Path.GetTempPath(), "SafeLauncher-RouteCheck");
        using var appJob = Native.CreateKillOnCloseJob();

        // A. Без ядра
        report.Stage("Этап A. Без ядра VPN");
        var baseline = RunTogether(appJob, new ProbeSpec(false, false, false), new ProbeSpec(true, false, true));
        var outsideA = baseline[0].Self;
        var appA = baseline[1].Self;
        report.Detail("Чужая программа", outsideA);
        report.Detail("Программа лаунчера", appA);

        // Способы, которые работают в этой сети без ядра: только по ним что-то можно доказать.
        var channels = Enumerable.Range(0, Probe.Channels.Length)
            .Select(i => outsideA is not null && outsideA.Has(i) && appA is not null && appA.Has(i)).ToArray();
        var appChannels = channels.Select((c, i) => c && i != SystemDns).ToArray();
        if (!appChannels.Any(c => c))
        {
            report.Check(false, "Без ядра нет связи с интернетом",
                "Ни один способ выхода в сеть не сработал ещё до запуска ядра. Проверьте интернет; если включён другой VPN или брандмауэр режет соединения, выключите его на время проверки.");
            return;
        }

        var skipped = Names(channels.Select(c => !c));
        report.Check(true, "Без ядра связь есть",
            skipped.Length == 0 ? "Все способы работают." : $"Не работают уже без ядра, дальше не учитываются: {skipped}.");
        var viaServer = appChannels.Select((c, i) => c && i != Tls).ToArray(); // TLS идёт к домену из исключений

        // Тестовый VPN-сервер
        using var server = TestServer.Start(TestServer.Locate(AppContext.BaseDirectory), work);
        if (!server.WaitReady(TimeSpan.FromSeconds(20)))
        {
            report.Check(false, "Тестовый VPN-сервер не запустился", "Вторая копия ядра не начала принимать соединения.");
            report.CoreLog("Журнал тестового сервера", server.LogLines);
            return;
        }

        report.Info($"Тестовый VPN-сервер: прокси SOCKS5 на 127.0.0.1:{server.Port}.");

        using var owner = new OwnerPipeServer(pid => Native.IsInJob(pid, appJob) ? Tag : null);
        CoreServiceClient client;
        try
        {
            client = CoreServiceClient.ConnectAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch (CoreServiceException ex)
        {
            report.Check(false, "Лаунчер не связался со службой", ex.Message);
            return;
        }

        IReadOnlyList<string> coreLog = Array.Empty<string>();
        using (client)
        {
            report.Check(true, "Лаунчер связался со службой", "Без прав администратора, служба проверена по номеру процесса.");
            var coreExits = 0;
            client.CoreExited += (_, _) => Interlocked.Increment(ref coreExits);

            // B. Подключение работает
            report.Stage("Этап B. Ядро запущено службой, подключение работает");
            if (!StartCore(client, owner, server.Port, report, "Служба запустила ядро"))
                return;

            var mark = server.Mark();
            var together = RunTogether(appJob, new ProbeSpec(true, true, true), new ProbeSpec(false, false, false));
            var app = together[0];
            var outside = together[1].Self;
            report.Detail("Программа лаунчера", app.Self);
            report.Detail("Её дочерний процесс", app.Child);
            report.Detail("Чужая программа одновременно", outside);

            var appOk = OkOn(app.Self, appChannels) && OkOn(app.Child, appChannels);
            report.Check(appOk, "У программы лаунчера и её дочернего процесса есть связь через подключение",
                appOk ? null : $"Не работает: у программы — {Failed(app.Self, appChannels)}; у дочернего процесса — {Failed(app.Child, appChannels)}. " + Explain(app.Self, owner));

            var tcpThroughServer = (!viaServer[0] || server.Saw(Probe.Alternate.Http, mark));
            var udpThroughServer = !(viaServer[2] || viaServer[3]) || server.SawPackets(mark);
            report.Check(tcpThroughServer && udpThroughServer, "Соединения программы лаунчера прошли через VPN-сервер",
                tcpThroughServer && udpThroughServer
                    ? "В журнале тестового сервера есть её HTTP" + (viaServer[2] || viaServer[3] ? " и UDP." : ".")
                    : (tcpThroughServer ? "" : $"Сервер не видел HTTP к {Probe.Alternate.Http}. ") + (udpThroughServer ? "" : "Сервер не видел её UDP. ") + Explain(app.Self, owner));

            if (appChannels[Tls])
            {
                var direct = !server.Saw(Probe.Alternate.Tls, mark);
                report.Check(direct, $"Исключение «{Probe.Targets.TlsName}» идёт напрямую, мимо VPN-сервера",
                    direct ? null : "Соединение к домену из исключений прошло через VPN-сервер: ядро не распознало имя сайта.");
            }

            var outsideOk = OkOn(outside, channels);
            report.Check(outsideOk, "Чужая программа идёт напрямую, связь есть",
                outsideOk ? null : $"Пока работало ядро, у программы, запущенной не из лаунчера, не работало: {Failed(outside, channels)}.");
            var outsideBypassed = !server.Saw(Probe.Main.Http, mark) && !server.Saw(Probe.Main.Tls, mark);
            report.Check(outsideBypassed, "Чужая программа не попала на VPN-сервер",
                outsideBypassed ? null : "Соединения программы, запущенной не из лаунчера, прошли через подключение лаунчера.");

            if (channels[SystemDns])
                report.Info("Системный DNS у программы лаунчера: " + (app.Self?.Has(SystemDns) == true ? "работает" : "не работает") +
                            ". Имена для всех программ ищет служба Windows, поэтому эти запросы идут напрямую; это учтём в настройках DNS.");
            report.Info(OwnerSummary(owner, app.Self, outside));

            // C. Подключение не работает
            report.Stage("Этап C. Подключение не работает (VPN-сервер «выключен»)");
            if (!StartCore(client, owner, TestServer.FreePort(), report, "Служба перезапустила ядро с новыми настройками"))
                return;

            together = RunTogether(appJob, new ProbeSpec(true, true, true), new ProbeSpec(false, false, false));
            app = together[0];
            outside = together[1].Self;
            report.Detail("Программа лаунчера", app.Self);
            report.Detail("Её дочерний процесс", app.Child);
            report.Detail("Чужая программа одновременно", outside);

            var blocked = BlockedOn(app.Self, viaServer) && BlockedOn(app.Child, viaServer);
            report.Check(blocked, "Без подключения у программы лаунчера и её дочернего процесса нет сети",
                blocked ? "Её трафик не ушёл в обход VPN." : $"Прошло мимо VPN: у программы — {Passed(app.Self, viaServer)}; у дочернего процесса — {Passed(app.Child, viaServer)}.");
            if (appChannels[Tls])
            {
                var exceptionOk = app.Self?.Has(Tls) == true;
                report.Check(exceptionOk, $"Исключение «{Probe.Targets.TlsName}» работает и без подключения",
                    exceptionOk ? null : "Соединение к домену из исключений не прошло, хотя должно идти напрямую.");
            }

            outsideOk = OkOn(outside, channels);
            report.Check(outsideOk, "Чужая программа по-прежнему выходит в сеть",
                outsideOk ? null : $"Не работало: {Failed(outside, channels)}.");

            // D. Вторая копия лаунчера
            report.Stage("Этап D. Вторая копия лаунчера");
            try
            {
                using var second = CoreServiceClient.ConnectAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                using var secondOwner = new OwnerPipeServer(_ => null);
                second.StartAsync(secondOwner.Name, Request(server.Port)).GetAwaiter().GetResult();
                report.Check(false, "Вторая копия не может забрать ядро", "Служба перезапустила ядро по команде второй копии, хотя им уже пользуется первая.");
            }
            catch (CoreServiceException ex)
            {
                report.Check(true, "Вторая копия не может забрать ядро", "Служба ответила: " + ex.Message);
            }

            var status = client.StatusAsync().GetAwaiter().GetResult();
            report.Check(status.Running && Volatile.Read(ref coreExits) == 0, "Ядро первой копии продолжает работать",
                status.Running ? null : "После попытки второй копии ядро остановилось.");
            coreLog = status.Log;
            report.Info($"Версия службы: {status.ServiceVersion}.");

            // E. Выход из лаунчера
            report.Stage("Этап E. Выход из лаунчера");
            var graceful = client.StopAsync().GetAwaiter().GetResult();
            report.Check(graceful, "Ядро закрылось само по команде службы",
                graceful ? null : "Ядро не закрылось за отведённое время, служба завершила его принудительно.");
        }

        var gone = WaitFor(() => !InterfaceExists(), TimeSpan.FromSeconds(10));
        report.Check(gone, "Адаптер ядра убран из системы", gone ? null : $"Адаптер «{InterfaceName}» остался после остановки ядра.");
        var after = RunTogether(appJob, new ProbeSpec(true, false, true), new ProbeSpec(false, false, false));
        report.Detail("Программа лаунчера", after[0].Self);
        report.Detail("Чужая программа", after[1].Self);
        var restored = OkOn(after[0].Self, appChannels) && OkOn(after[1].Self, channels);
        report.Check(restored, "После остановки связь у всех напрямую",
            restored ? "От ядра в системе ничего не осталось." : "После остановки ядра связи нет — в системе остались его маршруты или правила.");

        // F. Падение лаунчера
        report.Stage("Этап F. Лаунчер упал");
        CrashTest(report, channels);

        report.CoreLog("Журнал ядра (этапы B–D)", coreLog);
        report.CoreLog("Журнал тестового VPN-сервера", server.LogLines);

        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (Exception)
        {
            // не страшно: это папка во временных файлах
        }
    }

    private static bool StartCore(CoreServiceClient client, OwnerPipeServer owner, int serverPort, Report report, string title)
    {
        Console.WriteLine("Жду запуска ядра…");
        try
        {
            owner.CorePid = 0;
            owner.CorePid = client.StartAsync(owner.Name, Request(serverPort)).GetAwaiter().GetResult();
        }
        catch (CoreServiceException ex)
        {
            report.Check(false, "Ядро не запустилось", ex.Message);
            report.CoreLog("Журнал ядра", ex.Log);
            return false;
        }

        Thread.Sleep(1500); // маршруты и правила брандмауэра применяются чуть позже адаптера
        report.Check(true, title, $"Адаптер «{InterfaceName}» появился, ядро — процесс {owner.CorePid}.");
        return true;
    }

    /// <summary>Что лаунчер просит у службы: одно подключение (тестовый сервер) для программы «routecheck» с исключением.</summary>
    private static CoreStartRequest Request(int serverPort) => new()
    {
        Outbounds =
        {
            new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = ConnectionTag,
                ["server"] = "127.0.0.1",
                ["server_port"] = serverPort,
                ["version"] = "5"
            }
        },
        Apps = { new CoreAppRoute { Tag = Tag, Outbound = ConnectionTag, DirectDomains = { Probe.Targets.TlsName } } }
    };

    /// <summary>
    /// Отдельная копия RouteCheck изображает лаунчер: связывается со службой, запускает ядро и ждёт. Основная копия
    /// завершает её принудительно, как при падении, и проверяет, что служба сама остановила ядро.
    /// </summary>
    private static void CrashTest(Report report, bool[] channels)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!, HoldArgument)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        using var holder = Process.Start(info) ?? throw new InvalidOperationException("Не запустилась копия-«лаунчер».");
        try
        {
            var line = holder.StandardOutput.ReadLineAsync();
            if (!line.Wait(TimeSpan.FromSeconds(90)) || line.Result is null || !line.Result.StartsWith("READY", StringComparison.Ordinal))
            {
                report.Check(false, "Копия-«лаунчер» запустила ядро", line.IsCompleted ? line.Result ?? "копия закрылась" : "не ответила за 90 секунд");
                return;
            }

            report.Check(true, "Копия-«лаунчер» запустила ядро", null);
        }
        finally
        {
            if (!holder.HasExited)
                holder.Kill();
        }

        var gone = WaitFor(() => !InterfaceExists(), TimeSpan.FromSeconds(20));
        report.Check(gone, "После падения лаунчера служба остановила ядро",
            gone ? "Адаптер ядра исчез." : $"Через 20 секунд после падения адаптер «{InterfaceName}» всё ещё в системе.");
        var outside = RunTogether(null, new ProbeSpec(false, false, false))[0].Self;
        report.Detail("Чужая программа", outside);
        report.Check(OkOn(outside, channels), "После падения лаунчера связь в порядке", OkOn(outside, channels) ? null : $"Не работает: {Failed(outside, channels)}.");
    }

    /// <summary>Режим копии-«лаунчера» для этапа F.</summary>
    private static int Hold()
    {
        try
        {
            using var owner = new OwnerPipeServer(_ => null);
            var client = CoreServiceClient.ConnectAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var pid = client.StartAsync(owner.Name, Request(TestServer.FreePort())).GetAwaiter().GetResult();
            Console.WriteLine("READY " + pid);
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR " + ex.Message);
            return 1;
        }
    }

    /// <summary>Почему соединения программы лаунчера прошли мимо подключения.</summary>
    private static string Explain(Probe.Outcome? outcome, OwnerPipeServer owner)
    {
        if (outcome is null)
            return "Процесс не сообщил результат.";
        if (owner.Queries == 0)
            return owner.Rejected > 0
                ? $"Ядро ни разу не получило ответ от лаунчера: {owner.Rejected} подключений к каналу лаунчера отклонено."
                : "Ядро ни разу не спросило лаунчер, чей процесс.";
        if (!owner.Answers.TryGetValue((uint)outcome.Pid, out var answer))
            return "Об этом процессе ядро не спрашивало: его соединения шли мимо адаптера ядра или ядро не определило процесс.";
        return answer.Length == 0
            ? "Ядро спросило, но лаунчер не узнал свой процесс."
            : "Лаунчер узнал процесс, но ядро не применило правило (возможно, процесс из другого сеанса Windows).";
    }

    private static string OwnerSummary(OwnerPipeServer owner, Probe.Outcome? app, Probe.Outcome? outside)
    {
        var ours = owner.Answers.Where(a => a.Value.Length > 0).Select(a => a.Key).OrderBy(p => p).ToArray();
        var outsideAsked = outside is not null && owner.Answers.ContainsKey((uint)outside.Pid);
        return $"Ядро спросило лаунчер {owner.Queries} раз, наших процессов среди них — {ours.Length} (PID {string.Join(", ", ours)}). " +
               $"О чужой программе ядро {(outsideAsked ? "спрашивало и получило ответ «не наша»" : "не спрашивало")}. " +
               $"Отклонённых подключений к каналу лаунчера: {owner.Rejected}.";
    }

    private const string InterfaceName = "SafeLauncher";

    private static bool InterfaceExists() =>
        NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name.Equals(InterfaceName, StringComparison.OrdinalIgnoreCase));

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(250);
        }

        return condition();
    }

    private static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static bool OkOn(Probe.Outcome? o, bool[] channels) =>
        o is not null && Enumerable.Range(0, channels.Length).All(i => !channels[i] || o.Has(i));

    private static bool BlockedOn(Probe.Outcome? o, bool[] channels) =>
        o is not null && Enumerable.Range(0, channels.Length).All(i => !channels[i] || !o.Has(i));

    private static string Failed(Probe.Outcome? o, bool[] channels) =>
        o is null ? "программа не сообщила результат" : Names(Enumerable.Range(0, channels.Length).Select(i => channels[i] && !o.Has(i)));

    private static string Passed(Probe.Outcome? o, bool[] channels) =>
        o is null ? "программа не сообщила результат" : Names(Enumerable.Range(0, channels.Length).Select(i => channels[i] && o.Has(i)));

    private static string Names(IEnumerable<bool> selected) =>
        string.Join(", ", selected.Select((s, i) => s ? Probe.Channels[i].Name : null).Where(n => n is not null));

    /// <summary>Подопытная программа: в задании лаунчера ли, с дочерним процессом ли, на какие адреса ходит.</summary>
    private sealed record ProbeSpec(bool InJob, bool SpawnChild, bool Alternate);

    private sealed record ProbeResult(Probe.Outcome? Self, Probe.Outcome? Child);

    /// <summary>Запускает несколько «подопытных» программ и даёт им команду выходить в сеть одновременно.</summary>
    private static ProbeResult[] RunTogether(SafeFileHandle? appJob, params ProbeSpec[] probes)
    {
        Console.WriteLine("Проверяю связь…");
        var processes = new List<Process>();
        try
        {
            foreach (var spec in probes)
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true
                };
                info.ArgumentList.Add(Probe.Argument);
                if (spec.SpawnChild)
                    info.ArgumentList.Add(Probe.SpawnChild);
                if (spec.Alternate)
                    info.ArgumentList.Add(Probe.Alt);

                var process = Process.Start(info) ?? throw new InvalidOperationException("Проверочный процесс не запустился.");
                processes.Add(process);
                if (spec.InJob && appJob is not null)
                    Native.Assign(appJob, process.SafeHandle);
            }

            var outputs = processes.Select(p => p.StandardOutput.ReadToEndAsync()).ToArray();
            foreach (var process in processes)
                process.StandardInput.WriteLine("go");

            if (!Task.WaitAll(outputs, ProbeTimeout))
                throw new TimeoutException($"Проверочные процессы не ответили за {ProbeTimeout.TotalSeconds:0} секунд.");

            return outputs.Select(o => Parse(o.Result)).ToArray();
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
    }

    private static ProbeResult Parse(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var self = Probe.Parse(lines.FirstOrDefault(l => l.StartsWith("RESULT ", StringComparison.Ordinal)));
        var child = Probe.Parse(lines.FirstOrDefault(l => l.StartsWith("CHILD ", StringComparison.Ordinal)));
        return new ProbeResult(self, child);
    }

    private static IEnumerable<string> Adapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => $"  {n.Name} — {n.Description} ({n.NetworkInterfaceType})");

    /// <summary>Итог проверки: на экран по ходу дела и в файл в конце.</summary>
    private sealed class Report
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _logs = new();
        private readonly List<string> _failures = new();
        private int _checks;

        public bool Passed => _checks > 0 && _failures.Count == 0;

        public void Stage(string title) => Write(Environment.NewLine + "== " + title + " ==");

        public void Info(string text) => Write(text);

        public void Detail(string who, Probe.Outcome? outcome) =>
            Write($"   {who}: {(outcome is null ? "не сообщила результат" : outcome.ToString())}");

        public void Check(bool ok, string title, string? details)
        {
            _checks++;
            if (!ok)
                _failures.Add(title);
            Write($"[{(ok ? " ОК " : "СБОЙ")}] {title}" + (string.IsNullOrEmpty(details) ? "" : Environment.NewLine + "       " + details));
        }

        public void CoreLog(string title, IReadOnlyList<string> lines)
        {
            _logs.AppendLine().AppendLine("== " + title + " ==");
            foreach (var line in lines.TakeLast(400))
                _logs.AppendLine(line);
        }

        public void Save(string path)
        {
            var verdict = Verdict();
            Console.WriteLine();
            Console.WriteLine(verdict);
            File.WriteAllText(path, verdict + Environment.NewLine + Environment.NewLine + _text + _logs, new UTF8Encoding(true));
        }

        private string Verdict() => Passed
            ? "ИТОГ: ВСЁ РАБОТАЕТ. Служба запускает ядро по команде лаунчера без прав администратора. Программы лаунчера " +
              "вместе с дочерними процессами идут через подключение, исключения — напрямую, остальной компьютер не затронут. " +
              "Если подключение не работает, программы лаунчера остаются без сети, а не идут в обход. Ядро останавливается " +
              "и при выходе из лаунчера, и при его падении."
            : "ИТОГ: ЕСТЬ ПРОБЛЕМЫ: " + (_failures.Count == 0 ? "проверка не дошла до конца" : string.Join("; ", _failures)) +
              ". Пришлите файл RouteCheck-report.txt.";

        private void Write(string line)
        {
            Console.WriteLine(line);
            _text.AppendLine(line);
        }
    }
}
