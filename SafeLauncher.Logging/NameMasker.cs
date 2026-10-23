using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SafeLauncher.Logging;

/// <summary>
/// Обезличивание названий для выгрузки (галочка «Скрыть названия программ и подключений»): известные названия приложений,
/// подключений, адаптеров и параметры запуска → их метки, папки и файлы в путях → <c>{dir-xxxxxx}</c> и <c>{file-xxxxxx}</c>,
/// имена программ (*.exe) → <c>{exe-xxxxxx}</c>, приложения Microsoft Store → <c>{pkg-xxxxxx}</c>.
/// Остаются пути и программы Windows, самого лаунчера и ядра: по ним ничего не узнать, а для разбора они нужны.
/// Метки одинаковые для одного и того же названия, обратно не переводятся (HMAC с ключом <see cref="LogMasker"/>).
/// </summary>
public sealed class NameMasker
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    // C:\…, \\сервер\…, %USERPROFILE%\…: папки до «\» (в именах бывают пробелы), последний кусок — файл с расширением или слово
    private static readonly Regex WinPath = new(
        @"(?<![\p{L}\p{N}_\\])(?<root>[A-Za-z]:|%USERPROFILE%|\\{2,4}(?<host>[\p{L}\p{N}][^\\/:*?""<>|\s]*))(?<rest>(?:\\{1,2}[^\\/:*?""<>|\r\n\t]+?(?=\\))*(?:\\{1,2}(?:[^\\/:*?""<>|\r\n\t]*?\.[A-Za-z0-9]{1,8}(?![\p{L}\p{N}_])|[^\\/:*?""<>|\s]*))?)",
        Opts | RegexOptions.Compiled);

    // «chrome\.exe» — так программа записана в правилах ядра (регулярное выражение)
    private static readonly Regex ExeName = new(@"(?<![\p{L}\p{N}_\-.\\/{}])(?<name>[\p{L}\p{N}_\-.]+?)(?<dot>\\?\.)exe(?![\p{L}\p{N}_])", Opts | RegexOptions.Compiled);

    // Publisher.App_8wekyb3d8bbwe и Publisher.App_8wekyb3d8bbwe!App
    private static readonly Regex Package = new(@"(?<![\p{L}\p{N}_\-.{])[A-Za-z0-9][A-Za-z0-9.\-]*_[a-z0-9]{13}(?:![A-Za-z0-9._\-]+)?(?![\p{L}\p{N}_])", Opts | RegexOptions.Compiled);

    /// <summary>Папки, по которым ничего не узнать: остаются как есть.</summary>
    private static readonly HashSet<string> KeepFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)", "ProgramData", "Users", "Public", "AppData", "Local", "LocalLow", "Roaming", "Temp",
        "Microsoft", "Windows", "WindowsApps", "Packages", "Desktop", "Downloads", "Documents", "Рабочий стол", "Загрузки", "Документы",
        "Start Menu", "Programs", "Startup", "%USERPROFILE%", "Common Files"
    };

    /// <summary>Пути с такой папкой остаются целиком: Windows, сам лаунчер и его служба с ядром.</summary>
    private static readonly HashSet<string> KeepTrees = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "SafeLauncher", "KillSwitchLauncher"
    };

    /// <summary>Программы Windows, лаунчера и ядра: их имена ничего не говорят о работе человека.</summary>
    private static readonly HashSet<string> KeepPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "KillSwitchLauncher", "SafeLauncher.Service", "SafeLauncher.Updater", "sing-box", "svchost", "explorer", "cmd", "conhost",
        "powershell", "pwsh", "rundll32", "dllhost", "taskhostw", "sihost", "runtimebroker", "applicationframehost", "wermgr",
        "werfault", "msiexec", "services", "lsass", "csrss", "wininit", "winlogon", "smss", "system", "searchhost", "searchindexer",
        "backgroundtaskhost", "audiodg", "spoolsv", "ctfmon", "dwm", "fontdrvhost", "msedgewebview2", "curl", "ping", "nslookup", "netsh"
    };

    private readonly LogMasker _masker;
    private readonly Dictionary<string, string> _names;
    private readonly Regex? _known;

    /// <param name="names">
    /// Название → метка: приложения, подключения, адаптеры, параметры запуска. Названия с «.exe» — программы приложений:
    /// они заменяются только как имена программ (в путях, «Discord.exe», процессах журнала DNS).
    /// </param>
    public NameMasker(LogMasker masker, IEnumerable<KeyValuePair<string, string>> names)
    {
        _masker = masker;
        _names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, label) in names)
        {
            var key = (name ?? string.Empty).Trim();
            if (key.Length >= 2 && !string.IsNullOrEmpty(label))
                _names.TryAdd(key, label);
        }

        if (_names.Keys.Any(k => !k.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            // «Update.exe» и подобные ищутся только как программы: само слово «update» в тексте не название
            var alternatives = string.Join("|", _names.Keys.Where(k => !k.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(k => k.Length).Select(Regex.Escape));
            _known = new Regex(@"(?<![\p{L}\p{N}_{])(?:" + alternatives + @")(?![\p{L}\p{N}_}])", Opts | RegexOptions.Compiled);
        }
    }

    public string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Сначала пути и программы: так имя приложения внутри пути тоже станет меткой папки или программы.
        var result = WinPath.Replace(text, MaskPath);
        result = Package.Replace(result, m => _masker.NameLabel("pkg", m.Value));
        result = ExeName.Replace(result, m => Program(m.Groups["name"].Value) + m.Groups["dot"].Value + "exe");
        if (_known is not null)
            result = _known.Replace(result, m => _names.TryGetValue(m.Value, out var label) ? label : m.Value);
        return result;
    }

    /// <summary>Имя процесса без пути (журнал DNS): «Telegram» или «Telegram.exe».</summary>
    public string MaskProcess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Program(trimmed[..^4]) + ".exe"
            : Program(trimmed);
    }

    private string Program(string name)
    {
        // программа приложения скрывается, даже если это программа Windows (mstsc, cmd): по ней узнается приложение
        if (_names.TryGetValue(name + ".exe", out var label) || _names.TryGetValue(name, out label))
            return label;
        if (KeepPrograms.Contains(name))
            return name;
        return _masker.NameLabel("exe", name);
    }

    private string MaskPath(Match m)
    {
        var root = m.Groups["root"].Value;
        var rest = m.Groups["rest"].Value;
        var parts = rest.Split('\\');
        if (parts.Any(p => KeepTrees.Contains(p)))
        {
            // путь Windows или лаунчера остается, кроме программы приложения (C:\Windows\System32\mstsc.exe)
            var last = parts[^1];
            if (last.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && _names.ContainsKey(last))
                parts[^1] = Program(last[..^4]) + last[^4..];
            return root + string.Join("\\", parts);
        }

        // \\сервер\папка: имя сервера тоже название (адрес к этому времени уже скрыт или оставлен по галочке адресов)
        if (m.Groups["host"].Success && !m.Groups["host"].Value.StartsWith('{'))
            root = root[..^m.Groups["host"].Length] + _masker.NameLabel("host", m.Groups["host"].Value);

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || KeepFolders.Contains(part))
                continue;
            if (i == parts.Length - 1)
                parts[i] = File(part);
            else
                parts[i] = _names.TryGetValue(part, out var label) ? label : _masker.NameLabel("dir", part);
        }
        return root + string.Join("\\", parts);
    }

    private string File(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0)
            return _names.TryGetValue(name, out var folder) ? folder : _masker.NameLabel("dir", name);
        var stem = name[..dot];
        var ext = name[dot..];
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return Program(stem) + ext;
        return (_names.TryGetValue(stem, out var label) ? label : _masker.NameLabel("file", stem)) + ext;
    }
}
