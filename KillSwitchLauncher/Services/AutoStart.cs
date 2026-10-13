using System;
using Microsoft.Win32;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Запуск лаунчера при входе в Windows: запись в HKCU\…\Run текущего пользователя (прав администратора не нужно).
/// Лаунчер, запущенный так, сразу уходит в трей (<see cref="Argument"/>).
/// </summary>
public static class AutoStart
{
    public const string Argument = "--autostart";
    public const string ValueName = "SafeLauncher";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Строка запуска для этой копии лаунчера.</summary>
    public static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    /// <summary>Включить (указать на эту копию лаунчера) или выключить. Ошибки реестра — исключением.</summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            if (!string.Equals(key.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
