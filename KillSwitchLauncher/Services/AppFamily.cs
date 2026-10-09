using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>
/// «Семейство» процессов приложения: как узнать его процессы, которые работают вне задания лаунчера.
/// Например, Discord, запущенный до лаунчера (автозагрузка), или копия, перезапущенная обходным путём.
///
/// Процесс считается своим, если одновременно:
///   1. имя его .exe совпадает с запускаемым (для Squirrel-ярлыков ещё и с целью --processStart);
///   2. он запущен из папки приложения (для Squirrel — из общей папки над app-x.y.z).
/// Если папка общая (Program Files, System32, Загрузки и т.п.) или программа — общий «движок»
/// (java, python, cmd…), семейства нет: тогда под контролем только то, что запустил сам лаунчер.
/// </summary>
public sealed class AppFamily
{
    private static readonly Regex SquirrelVersionDir = new(@"^app-(\d+(?:\.\d+){0,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProcessStartArg = new(@"--processStart(?:AndWait)?(?:\s+|=)""?([^""]+?\.exe)""?(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Общие программы-«движки»: по их имени нельзя понять, какое приложение в них работает.</summary>
    private static readonly HashSet<string> GenericHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "py.exe", "node.exe", "dotnet.exe",
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "rundll32.exe", "msiexec.exe", "conhost.exe", "explorer.exe", "wsl.exe", "bash.exe"
    };

    private static readonly Lazy<HashSet<string>> BroadFolders = new(CollectBroadFolders);

    private readonly HashSet<string> _names;
    private readonly string[] _roots; // с завершающим «\»

    private AppFamily(HashSet<string> names, string[] roots)
    {
        _names = names;
        _roots = roots;
    }

    /// <summary>Семейство для приложения или null, если узнавать его процессы «со стороны» небезопасно.</summary>
    public static AppFamily? For(AppEntry entry)
    {
        try
        {
            var exe = Path.GetFullPath(entry.ExePath);
            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir))
                return null;

            if (TryGetSquirrelRoot(dir, out var squirrelRoot))
                dir = squirrelRoot;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(exe) };
            if (GetProcessStartTarget(entry.Arguments) is { } target)
                names.Add(target);

            return Build(dir, names);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Семейство по уже запущенному процессу. Для приложений Microsoft Store: путь к их .exe
    /// (в C:\Program Files\WindowsApps\…) становится известен только после запуска.
    /// </summary>
    public static AppFamily? ForImage(string imagePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(imagePath);
            return string.IsNullOrEmpty(dir)
                ? null
                : Build(dir, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(imagePath) });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AppFamily? Build(string dir, HashSet<string> names)
    {
        if (IsTooBroad(dir))
            return null;

        names.ExceptWith(GenericHosts);
        return names.Count == 0 ? null : new AppFamily(names, CanonicalForms(dir));
    }

    /// <summary>Быстрая проверка по имени файла из списка процессов, до открытия самого процесса.</summary>
    public bool HasName(string fileName) => _names.Contains(fileName);

    /// <summary>Полный путь к образу процесса лежит в папке приложения.</summary>
    public bool ContainsPath(string imagePath) =>
        _roots.Any(r => imagePath.StartsWith(r, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Squirrel-программы (Discord, Slack, Teams classic…) при обновлении переезжают в новую папку app-x.y.z,
    /// а старую удаляют. Если путь вёл в такую папку и файла больше нет, возвращает ту же программу
    /// из самой новой версии; иначе null.
    /// </summary>
    public static string? FindCurrentSquirrelExe(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            if (string.IsNullOrEmpty(dir) || !TryGetSquirrelRoot(dir, out var root))
                return null;

            var fileName = Path.GetFileName(exePath);
            return Directory.EnumerateDirectories(root, "app-*")
                .Select(d => (Dir: d, Version: ParseSquirrelVersion(Path.GetFileName(d))))
                .Where(x => x.Version is not null && File.Exists(Path.Combine(x.Dir, fileName)))
                .OrderByDescending(x => x.Version)
                .Select(x => Path.Combine(x.Dir, fileName))
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- вспомогательное

    /// <summary>Папка вида ...\Discord\app-1.0.9 рядом с Update.exe: корнем приложения считаем ...\Discord.</summary>
    private static bool TryGetSquirrelRoot(string dir, out string root)
    {
        root = Path.GetDirectoryName(dir.TrimEnd('\\')) ?? string.Empty;
        return ParseSquirrelVersion(Path.GetFileName(dir.TrimEnd('\\'))) is not null
               && root.Length > 0
               && File.Exists(Path.Combine(root, "Update.exe"));
    }

    private static Version? ParseSquirrelVersion(string folderName)
    {
        var match = SquirrelVersionDir.Match(folderName);
        if (!match.Success)
            return null;

        var text = match.Groups[1].Value;
        if (!text.Contains('.'))
            text += ".0";
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>Ярлыки Squirrel: «Update.exe --processStart Discord.exe» — настоящая программа указана в аргументах.</summary>
    private static string? GetProcessStartTarget(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return null;

        var match = ProcessStartArg.Match(arguments);
        return match.Success ? Path.GetFileName(match.Groups[1].Value.Trim()) : null;
    }

    private static bool IsTooBroad(string dir)
    {
        var d = Trim(dir);

        var driveRoot = Path.GetPathRoot(d);
        if (string.IsNullOrEmpty(driveRoot) || string.Equals(Trim(driveRoot), d, StringComparison.OrdinalIgnoreCase))
            return true;

        // Всё внутри Windows — системные программы, их не трогаем.
        var windows = Trim(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (windows.Length > 0 && (string.Equals(d, windows, StringComparison.OrdinalIgnoreCase) ||
                                   d.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)))
            return true;

        return BroadFolders.Value.Contains(d);
    }

    private static HashSet<string> CollectBroadFolders()
    {
        static string Special(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

        var profile = Special(Environment.SpecialFolder.UserProfile);
        var local = Special(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Special(Environment.SpecialFolder.ProgramFiles);

        var candidates = new[]
        {
            programFiles,
            Special(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? string.Empty,
            Special(Environment.SpecialFolder.CommonProgramFiles),
            Special(Environment.SpecialFolder.CommonProgramFilesX86),
            Special(Environment.SpecialFolder.CommonApplicationData),
            Special(Environment.SpecialFolder.ApplicationData),
            Special(Environment.SpecialFolder.Desktop),
            Special(Environment.SpecialFolder.DesktopDirectory),
            Special(Environment.SpecialFolder.CommonDesktopDirectory),
            Special(Environment.SpecialFolder.MyDocuments),
            profile,
            local,
            Path.GetDirectoryName(profile) ?? string.Empty, // C:\Users
            Path.Combine(profile, "Downloads"),
            Path.Combine(local, "Programs"),
            Path.Combine(local, "Temp"),
            Path.Combine(local, "Microsoft", "WindowsApps"),
            Path.Combine(programFiles, "WindowsApps"),
            Path.GetTempPath()
        };

        return new HashSet<string>(
            candidates.Where(p => !string.IsNullOrWhiteSpace(p)).SelectMany(p => CanonicalForms(p)).Select(Trim),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Разные записи одной папки: как задана, с длинными именами вместо 8.3 и итоговый путь после ссылок/junction.
    /// Windows сообщает путь процесса в итоговом виде, поэтому сравниваем со всеми вариантами.
    /// </summary>
    private static string[] CanonicalForms(string dir)
    {
        var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                forms.Add(Trim(path) + "\\");
        }

        try
        {
            var full = Path.GetFullPath(dir);
            Add(full);
            Add(GetLongPath(full));
            Add(GetFinalPath(full));
        }
        catch (Exception)
        {
            Add(dir);
        }

        return forms.ToArray();
    }

    private static string? GetLongPath(string path)
    {
        var buffer = new StringBuilder(32768);
        var length = NativeMethods.GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
    }

    private static string? GetFinalPath(string path)
    {
        const uint fileReadAttributes = 0x0080;
        const uint shareAll = 0x00000007;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000; // нужно, чтобы открыть папку

        using var handle = NativeMethods.CreateFileW(path, fileReadAttributes, shareAll, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        var buffer = new StringBuilder(32768);
        var length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            return null;

        var result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + result[8..];
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }

    private static string Trim(string path) => path.TrimEnd('\\', '/');
}
