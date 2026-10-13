using System;
using System.Windows;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Помощник журнала DNS: та же программа с правами администратора, без окон (см. DnsCollector).
        if (e.Args.Length > 0 && e.Args[0] == DnsCollector.Argument)
        {
            Shutdown(DnsCollector.Run(e.Args));
            return;
        }

        // После установки обновления старая копия может ещё закрываться: даём ей время.
        var wait = UpdateService.IsUpdatePending ? TimeSpan.FromSeconds(15) : TimeSpan.Zero;
        _instance = SingleInstance.TryAcquire(wait);
        if (_instance is null)
        {
            // Лаунчер уже работает (возможно, в трее): показываем его окно и выходим.
            SingleInstance.ActivateExisting();
            Shutdown();
            return;
        }

        // При выходе из Windows лаунчер закрывается последним из программ: приложения под его защитой успевают
        // закрыться сами, пока подключение ещё работает.
        NativeMethods.SetProcessShutdownParameters(0x100, 0);

        var window = new MainWindow();
        MainWindow = window;
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(window.ShowFromTray));

        // Автозапуск при входе в Windows: сразу в трей, окно откроется по значку.
        if (Array.IndexOf(e.Args, AutoStart.Argument) >= 0)
            window.StartInTray();
        else
            window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Выход из Windows или установщик закрывает программы: закрываемся, а не уходим в трей.
        (MainWindow as MainWindow)?.PrepareForExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
