namespace KillSwitchLauncher.ViewModels;

/// <summary>Что показывает окно под шапкой.</summary>
public enum MainPage
{
    /// <summary>Плитки приложений.</summary>
    Apps,

    /// <summary>Общие настройки во всё окно.</summary>
    Settings,

    /// <summary>Настройки одного приложения (и добавление нового).</summary>
    App,

    /// <summary>Мастер первого запуска.</summary>
    Welcome,

    /// <summary>«Добавить приложение»: программы из меню «Пуск» с галочками и выбор в проводнике.</summary>
    AddApps
}

/// <summary>Разделы страницы настроек (список слева).</summary>
public enum SettingsSection
{
    Connection,
    Connections,
    Adapters,
    Protection,
    Exclusions,
    Window,
    Log,
    Updates
}

/// <summary>Цвет сводки в шапке.</summary>
public enum SummaryTone
{
    /// <summary>Ничего не запущено и VPN пока не нужен.</summary>
    Idle,

    /// <summary>Всё запущенное защищено (или системный VPN подключён).</summary>
    Ok,

    /// <summary>Сервер не отвечает: приложения ждут сеть.</summary>
    Warn,

    /// <summary>Есть приложения без защиты или системный VPN отключён.</summary>
    Danger
}
