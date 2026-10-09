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

        var window = new MainWindow();
        MainWindow = window;
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(window.ShowFromTray));
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
