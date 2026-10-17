namespace KillSwitchLauncher.Models;

/// <summary>Что делать, если ядро VPN лаунчера упало, пока работают приложения через него.</summary>
public enum CoreCrashAction
{
    /// <summary>Приостановить приложения и перезапустить ядро (только с «Блокировать трафик мимо VPN»).</summary>
    Restart,

    /// <summary>Закрыть приложения через ядро.</summary>
    CloseApps
}
