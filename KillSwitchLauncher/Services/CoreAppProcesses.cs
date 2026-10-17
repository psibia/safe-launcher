using System;
using System.Collections.Generic;
using System.Linq;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Процессы приложений для <see cref="LauncherNetwork"/> без приложений, запущенных разово без сети («Запустить без сети»):
/// ядро их не ведёт, даже если по настройкам они идут через подключение лаунчера. Поэтому при обрыве их не закрывают, при
/// перезапуске ядра не приостанавливают, и перезапуску ядра они не мешают. Любой поток.
/// </summary>
public sealed class CoreAppProcesses : IAppProcesses
{
    private readonly IAppProcesses _inner;
    private volatile IReadOnlySet<Guid> _offline = new HashSet<Guid>();

    public CoreAppProcesses(IAppProcesses inner) => _inner = inner;

    /// <summary>Приложения, которые сейчас запущены разово без сети.</summary>
    public void SetOffline(IEnumerable<Guid> appIds) => _offline = new HashSet<Guid>(appIds);

    public IReadOnlyList<Guid> RunningApps()
    {
        var offline = _offline;
        return _inner.RunningApps().Where(id => !offline.Contains(id)).ToList();
    }

    public IDisposable Freeze(IReadOnlyCollection<Guid> appIds) => _inner.Freeze(Online(appIds));

    public KillReport KillApps(IReadOnlyCollection<Guid> appIds, bool blockRelaunch) => _inner.KillApps(Online(appIds), blockRelaunch);

    public void StopBlocking(Guid? appId = null) => _inner.StopBlocking(appId);

    private List<Guid> Online(IEnumerable<Guid> appIds)
    {
        var offline = _offline;
        return appIds.Where(id => !offline.Contains(id)).ToList();
    }
}
