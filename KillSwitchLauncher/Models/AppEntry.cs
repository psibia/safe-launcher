using System;
using System.IO;
using System.Text.Json.Serialization;

namespace KillSwitchLauncher.Models;

/// <summary>Приложение, добавленное в лаунчер (сохраняется в settings.json).</summary>
public sealed class AppEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Отображаемое название плитки.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Полный путь к исполняемому .exe (для ярлыков — извлечённый из .lnk при последнем запуске).
    /// Пустой у приложений Microsoft Store: они запускаются по AppUserModelId.
    /// </summary>
    public string ExePath { get; set; } = string.Empty;

    /// <summary>Аргументы командной строки (берутся из ярлыка, если он был выбран).</summary>
    public string? Arguments { get; set; }

    /// <summary>Рабочая папка (из ярлыка или папка .exe).</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Исходный файл, который выбрал пользователь (.exe или .lnk). Если это ярлык, лаунчер перечитывает его
    /// при каждом запуске, а ExePath/Arguments/WorkingDirectory/AppUserModelId служат запасным вариантом,
    /// если ярлык удалят.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>Приложение Microsoft Store (упакованное): идентификатор вида «Publisher.App_8wekyb3d8bbwe!App».</summary>
    public string? AppUserModelId { get; set; }

    /// <summary>Записывать DNS-запросы приложения в журнал DNS (пункт «Журнал DNS» в меню плитки).</summary>
    public bool TrackDns { get; set; }

    /// <summary>Условие запуска, своя защита и исключения (страница приложения, раздел «Сеть»).</summary>
    public AppNetwork Network { get; set; } = new();

    /// <summary>Ярлык, из которого добавлено приложение, или null, если выбран сам .exe.</summary>
    [JsonIgnore]
    public string? ShortcutPath =>
        SourcePath is { } path && string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase) ? path : null;

    [JsonIgnore]
    public bool IsPackaged => !string.IsNullOrWhiteSpace(AppUserModelId);
}
