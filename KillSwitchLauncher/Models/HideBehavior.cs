namespace KillSwitchLauncher.Models;

/// <summary>Что делает кнопка «Закрыть» в заголовке окна (а также Alt+F4 и «Закрыть окно» на панели задач).</summary>
public enum HideBehavior
{
    /// <summary>Каждый раз спрашивать: свернуть в трей или закрыть.</summary>
    Ask,

    /// <summary>Сразу сворачивать в трей.</summary>
    Tray,

    /// <summary>Закрывать лаунчер.</summary>
    Exit
}
