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
///   2. он запущен из папки приложения (для Squirrel — из общей папки над app-x.y.z) или, в любой папке, кроме папки
///      Windows, его программа подписана тем же издателем, что и программа приложения (<see cref="Publisher"/>).
/// Если программа — общий «движок» (java, python, cmd…), семейства нет: тогда под контролем только то, что запустил сам
/// лаунчер. Если папка общая (Program Files, System32, Загрузки и т.п.), процессы узнаются только по подписи издателя.
/// У приложения Microsoft Store папка приложения — папка его пакета в любой версии (<see cref="StorePackage"/>).
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
    private readonly StorePackage? _package;
    private readonly string? _publisher;

    private AppFamily(HashSet<string> names, string[] roots, StorePackage? package, string? publisher)
    {
        _names = names;
        _roots = roots;
        _package = package;
        _publisher = publisher;
    }

    /// <summary>
    /// Семейство для приложения или null, если узнавать его процессы «со стороны» небезопасно.
    /// </summary>
    /// <param name="publisher">Издатель из проверенной подписи программы приложения (<see cref="Signatures"/>); null — нет подписи.</param>
    public static AppFamily? For(AppEntry entry, string? publisher = null)
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

            return Build(dir, names, publisher);
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
    public static AppFamily? ForImage(string imagePath, string? publisher = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(imagePath);
            return string.IsNullOrEmpty(dir)
                ? null
                : Build(dir, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(imagePath) }, publisher);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AppFamily? Build(string dir, HashSet<string> names, string? publisher)
    {
        names.ExceptWith(GenericHosts);
        if (names.Count == 0)
            return null;

        publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim();
        // По общей папке копию не узнать: в ней лежат и чужие программы. Остаётся подпись издателя, кроме программ Windows.
        if (IsTooBroad(dir))
            return publisher is null || IsWindowsPath(Trim(dir) + "\\") ? null : new AppFamily(names, Array.Empty<string>(), null, publisher);
        return new AppFamily(names, CanonicalForms(dir), StorePackage.Find(dir), publisher);
    }

    /// <summary>То же семейство с другим издателем (подпись программы приложения проверена позже, в фоне).</summary>
    public AppFamily WithPublisher(string? publisher) =>
        new(_names, _roots, _package, string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim());

    /// <summary>Издатель из проверенной подписи программы приложения; null — подписи нет или она ещё не проверена.</summary>
    public string? Publisher => _publisher;

    /// <summary>
    /// Программа того же издателя с тем же именем exe в любой папке, кроме папки Windows: приложение переехало или
    /// обновилось в другую папку. <paramref name="publisher"/> — издатель из проверенной подписи этой программы.
    /// </summary>
    public bool SignedBySame(string imagePath, string? publisher) =>
        _publisher is not null && publisher is not null &&
        string.Equals(_publisher, publisher.Trim(), StringComparison.OrdinalIgnoreCase) &&
        HasName(Path.GetFileName(imagePath)) && !IsWindowsPath(imagePath);

    /// <summary>Путь в папке Windows: её программы лаунчер своими не считает, даже с той же подписью.</summary>
    public static bool IsWindowsPath(string path)
    {
        var windows = Trim(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return windows.Length > 0 && path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Быстрая проверка по имени файла из списка процессов, до открытия самого процесса.</summary>
    public bool HasName(string fileName) => _names.Contains(fileName);

    /// <summary>Имена exe-файлов приложения.</summary>
    public IReadOnlyCollection<string> Names => _names;

    /// <summary>Папка приложения в разных записях (как задана, с длинными именами, итоговый путь), с завершающим «\».</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Общая программа-«движок» (java, cmd…): по её имени не понять, какое приложение в ней работает.</summary>
    public static bool IsGenericHost(string fileName) => GenericHosts.Contains(fileName);

    /// <summary>
    /// Та же программа: общая папка приложения и общее имя exe-файла (например, Discord через Update.exe --processStart
    /// и сам Discord.exe из папки app-x.y.z).
    /// </summary>
    public bool Overlaps(AppFamily other) =>
        _names.Overlaps(other._names) &&
        (_roots.Any(r => other._roots.Contains(r, StringComparer.OrdinalIgnoreCase)) ||
         _package is not null && other._package is not null && _package.SameAs(other._package));

    /// <summary>Полный путь к образу процесса лежит в папке приложения (у приложения Store — в папке его пакета любой версии).</summary>
    public bool ContainsPath(string imagePath) =>
        _roots.Any(r => imagePath.StartsWith(r, StringComparison.OrdinalIgnoreCase)) || _package?.Contains(imagePath) == true;

    /// <summary>
    /// Программа, которую запускает Squirrel-ярлык (Update.exe --processStart Discord.exe), в самой новой папке app-x.y.z
    /// рядом с Update.exe. null — ярлык не такой или программы нет.
    /// </summary>
    public static string? SquirrelTarget(AppEntry entry)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entry.ExePath) || GetProcessStartTarget(entry.Arguments) is not { } target)
                return null;
            var dir = Path.GetDirectoryName(Path.GetFullPath(entry.ExePath));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;
            return Directory.EnumerateDirectories(dir, "app-*")
                .Select(d => (Dir: d, Version: ParseSquirrelVersion(Path.GetFileName(d))))
                .Where(x => x.Version is not null && File.Exists(Path.Combine(x.Dir, target)))
                .OrderByDescending(x => x.Version)
                .Select(x => Path.Combine(x.Dir, target))
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

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
        if (IsWindowsPath(d + "\\"))
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

    /// <summary>
    /// Настоящий путь: без коротких имён, ссылок и дисков subst; сетевой диск — путём \\сервер\папка. null — не открыть.
    /// </summary>
    internal static string? GetFinalPath(string path)
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

/// <summary>
/// Папка пакета Microsoft Store: …\WindowsApps\Имя_Версия_Архитектура_Ресурс_Издатель\…. Обновляясь, приложение Store
/// переезжает в папку новой версии и само перезапускается оттуда, а прежнюю папку Windows удаляет. Поэтому его процессы
/// узнаются по имени пакета и издателю в любой версии, а не по папке, найденной при запуске. То же правило у службы для
/// ядра (CoreConfigBuilder, копии мимо лаунчера).
/// </summary>
/// <param name="Parent">Папка WindowsApps с «\» в конце.</param>
/// <param name="Rest">Путь от папки пакета до папки программы, с «\» в конце («\app\» или «\»).</param>
internal sealed record StorePackage(string Parent, string Name, string Publisher, string Rest)
{
    private static readonly Regex FullName = new(@"^(?<name>[^_\\]+)_(?<version>\d+(?:\.\d+){3})_[^_\\]*_[^_\\]*_(?<publisher>[a-z0-9]{13})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Пакет, в папке которого лежит <paramref name="dir"/>, или null.</summary>
    public static StorePackage? Find(string dir)
    {
        var parts = dir.TrimEnd('\\').Split('\\');
        for (var i = 1; i < parts.Length; i++)
        {
            if (!string.Equals(parts[i - 1], "WindowsApps", StringComparison.OrdinalIgnoreCase) || FullName.Match(parts[i]) is not { Success: true } match)
                continue;
            var rest = "\\" + string.Concat(parts.Skip(i + 1).Select(p => p + "\\"));
            return new StorePackage(string.Join("\\", parts.Take(i)) + "\\", match.Groups["name"].Value, match.Groups["publisher"].Value, rest);
        }

        return null;
    }

    /// <summary>Программа лежит в той же папке этого пакета, в любой его версии.</summary>
    public bool Contains(string imagePath)
    {
        if (!imagePath.StartsWith(Parent, StringComparison.OrdinalIgnoreCase))
            return false;
        var tail = imagePath[Parent.Length..];
        var slash = tail.IndexOf('\\');
        return slash > 0 && FullName.Match(tail[..slash]) is { Success: true } match && Same(match) &&
               tail[slash..].StartsWith(Rest, StringComparison.OrdinalIgnoreCase);
    }

    public bool SameAs(StorePackage other) =>
        string.Equals(Parent, other.Parent, StringComparison.OrdinalIgnoreCase) && string.Equals(Rest, other.Rest, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(Publisher, other.Publisher, StringComparison.OrdinalIgnoreCase);

    /// <summary>Путь без версии пакета (для сравнения программ приложения): «…\WindowsApps\Claude_*_pzs8sxrjxfjjc\app».</summary>
    public static string WithoutVersion(string path)
    {
        var parts = path.Split('\\');
        for (var i = 1; i < parts.Length; i++)
        {
            if (string.Equals(parts[i - 1], "WindowsApps", StringComparison.OrdinalIgnoreCase) && FullName.Match(parts[i]) is { Success: true } match)
                parts[i] = match.Groups["name"].Value + "_*_" + match.Groups["publisher"].Value;
        }

        return string.Join("\\", parts);
    }

    private bool Same(Match match) =>
        string.Equals(match.Groups["name"].Value, Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(match.Groups["publisher"].Value, Publisher, StringComparison.OrdinalIgnoreCase);
}
