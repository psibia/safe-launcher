using System;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;

namespace SafeLauncher.Service;

internal static class Program
{
    // Ctrl+C, которым служба мягко останавливает ядро, приходит и ей самой: его нужно пропускать.
    private static PosixSignalRegistration? _ignoreCtrlC;

    private static int Main(string[] args)
    {
        // Падение службы: стек в журнал службы (и в Журнал Windows), до того как процесс завершится.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ServiceLog.Crash(e.ExceptionObject as Exception, "AppDomain", terminating: true);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => ServiceLog.Crash(e.Exception, "Task", terminating: false);

        var command = args.Length > 0 ? args[0] : string.Empty;
        if (command.StartsWith("--", StringComparison.Ordinal))
        {
            // Вывод читают установщик и RouteCheck: UTF-8 при любой кодировке консоли.
            if (Console.IsOutputRedirected)
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            if (Console.IsErrorRedirected)
                Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        }

        try
        {
            switch (command)
            {
                case "--install":
                    ServiceSetup.Install();
                    Console.WriteLine("Служба установлена и запущена.");
                    return 0;
                case "--uninstall":
                    ServiceSetup.Uninstall();
                    Console.WriteLine("Служба удалена.");
                    return 0;
                case "--console":
                    return RunInConsole();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        // Служба: своей консоли нет, создаём невидимую (в сеансе служб окон не видно) для общего с ядром Ctrl+C.
        if (Native.GetConsoleWindow() == IntPtr.Zero)
            Native.AllocConsole();
        // Если Windows запустила службу с отключённым Ctrl+C, ядро унаследовало бы это и не смогло бы закрыться мягко.
        Native.SetConsoleCtrlHandler(IntPtr.Zero, false);
        _ignoreCtrlC = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        ServiceBase.Run(new NetService());
        return 0;
    }

    /// <summary>Для отладки: служба в окне консоли (от имени администратора), остановка — Enter.</summary>
    private static int RunInConsole()
    {
        _ignoreCtrlC = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        var service = new NetService();
        service.StartAll(message => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}"));
        Console.WriteLine("Служба работает в консоли. Enter — остановить.");
        Console.ReadLine();
        service.StopAll();
        return 0;
    }
}
