using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace SafeLauncher.Service;

/// <summary>Служба Windows «Безопасный лаунчер — сеть».</summary>
internal sealed class NetService : ServiceBase
{
    private CoreRunner? _core;
    private NetworkGuard? _guard;
    private ControlServer? _server;

    public NetService()
    {
        ServiceName = ControlProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args) => StartAll(Log);

    /// <summary>Запуск и в службе, и в консоли (--console).</summary>
    public void StartAll(Action<string> log)
    {
        ServiceLog.Initialize(log);
        _core = new CoreRunner(AppContext.BaseDirectory);
        _core.KillLeftovers();
        _guard = new NetworkGuard(log, Path.Combine(AppContext.BaseDirectory, "run", "guard.json"));
        // Фильтры, оставшиеся после сбоя прежней службы, — до того, как к ней подключатся лаунчеры.
        _guard.Restore();
        _server = new ControlServer(_core, _guard, log);
        _server.Start();
        log("Служба запущена");
    }

    protected override void OnStop() => StopAll();

    // Выключение Windows: ядро останавливается, как при выходе из лаунчера.
    protected override void OnShutdown() => StopAll();

    public void StopAll()
    {
        _server?.Dispose();
        _core?.Dispose();
        _guard?.Dispose(); // снимает фильтры Windows: остановка службы — не сбой
        _server = null;
        _core = null;
        _guard = null;
        ServiceLog.Shutdown();
    }

    private void Log(string message)
    {
        try
        {
            EventLog.WriteEntry(ControlProtocol.ServiceName, message, EventLogEntryType.Information);
        }
        catch (Exception)
        {
            // источник журнала не зарегистрирован — не страшно
        }
    }
}
