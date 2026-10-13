using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Браузеры. Когда приложение открывает ссылку, Windows запускает браузер его дочерним процессом, и браузер оказывается
/// в задании приложения. Такой браузер — не часть приложения: он ходит в интернет напрямую, не считается запущенным
/// приложением и не закрывается вместе с ним.
/// </summary>
public static class Browsers
{
    private static readonly HashSet<string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "browser.exe", "brave.exe", "vivaldi.exe",
        "iexplore.exe", "chromium.exe", "thorium.exe", "waterfox.exe", "librewolf.exe", "floorp.exe", "zen.exe",
        "palemoon.exe", "seamonkey.exe", "maxthon.exe", "arc.exe", "atom.exe", "msedge_proxy.exe"
    };

    private static readonly Lazy<string?> DefaultBrowser = new(FindDefaultBrowser);

    /// <summary>Похож ли .exe на браузер: известное имя или браузер по умолчанию в Windows.</summary>
    public static bool IsBrowser(string exeName)
    {
        if (string.IsNullOrEmpty(exeName))
            return false;
        var name = FileName(exeName);
        return KnownNames.Contains(name) ||
               DefaultBrowser.Value is { } browser && string.Equals(browser, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Имя .exe браузера, который открывает ссылки https (выбор пользователя в «Приложениях по умолчанию»).</summary>
    private static string? FindDefaultBrowser()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (choice?.GetValue("ProgId") is not string progId || progId.Length == 0)
                return null;
            using var command = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
            return command?.GetValue(null) is string line ? ExeFromCommand(line) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>«"C:\…\chrome.exe" --single-argument %1» → chrome.exe.</summary>
    internal static string? ExeFromCommand(string command)
    {
        var text = command.Trim();
        string path;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0)
                return null;
            path = text[1..end];
        }
        else
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe < 0)
                return null;
            path = text[..(exe + 4)];
        }

        var name = FileName(path);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    /// <summary>Имя файла из пути Windows (тесты идут и не на Windows, там «\» не разделитель).</summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
}
