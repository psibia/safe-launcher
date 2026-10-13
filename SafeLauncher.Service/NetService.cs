using System;
using System.Diagnostics;
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
        _core = new CoreRunner(AppContext.BaseDirectory);
        _core.KillLeftovers();
        _guard = new NetworkGuard(log);
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
        _guard?.Dispose(); // фильтры Windows удаляются вместе с сеансом
        _server = null;
        _core = null;
        _guard = null;
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
