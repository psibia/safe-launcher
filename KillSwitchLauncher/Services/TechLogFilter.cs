using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Фильтр окна технического журнала (UI-3): вкладка (источник), «только ошибки», «Типы событий» (группы областей) и поиск.
/// Поиск: слова через пробел, все должны найтись в тексте, области или коде; <c>code:core.start</c> — точный код (или его начало).
/// </summary>
public sealed class TechLogFilter
{
    /// <summary>launcher, service или core; null — все.</summary>
    public string? Source { get; init; }

    /// <summary>Только WARN и ERROR (и отметки пользователя: по ним ищут место проблемы).</summary>
    public bool ErrorsOnly { get; init; }

    /// <summary>Ключи групп <see cref="LogAreaGroups"/>; null — все типы.</summary>
    public IReadOnlySet<string>? Groups { get; init; }

    public string? Search { get; init; }

    public Func<LogLine, bool> Compile()
    {
        var areas = Groups is null
            ? null
            : new HashSet<string>(LogAreaGroups.All.Where(g => Groups.Contains(g.Key)).SelectMany(g => g.Areas), StringComparer.Ordinal);
        var words = new List<string>();
        var codes = new List<string>();
        foreach (var token in (Search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("code:", StringComparison.OrdinalIgnoreCase) && token.Length > 5)
                codes.Add(token[5..]);
            else
                words.Add(token);
        }

        var source = Source;
        var errorsOnly = ErrorsOnly;
        return line =>
        {
            if (source is not null && line.Source != source)
                return false;
            var marker = line.Code == LogCodes.Marker;
            if (errorsOnly && line.Level < LogLevel.Warn && !marker)
                return false;
            // строки ядра (main) не входят ни в один тип: при выбранных типах их видно только на вкладке «Ядро»
            if (areas is not null && !marker && !(source == LogSources.Core && line.Area == LogAreas.Main) && !areas.Contains(line.Area))
                return false;
            foreach (var code in codes)
            {
                if (!line.Code.StartsWith(code, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            foreach (var word in words)
            {
                if (line.Text.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0 &&
                    !line.Area.Equals(word, StringComparison.OrdinalIgnoreCase) &&
                    line.Code.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0 &&
                    !line.Cid.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        };
    }

    /// <summary>Теги подключений и приложений в тексте → «имя» (для показа; в файлы идет как есть).</summary>
    public static string WithNames(string text, IReadOnlyDictionary<string, string> names)
    {
        if (names.Count == 0 || text.Length < 33)
            return text;
        return TagPattern.Replace(text, m => names.TryGetValue(m.Value, out var name) ? "«" + name + "»" : m.Value);
    }

    /// <summary>Комментарий первой строкой экспорта одного журнала: какие теги чьи.</summary>
    public static string TagsComment(IEnumerable<LogLine> lines, IReadOnlyDictionary<string, string> names)
    {
        var used = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            foreach (Match m in TagPattern.Matches(line.Text))
            {
                if (names.TryGetValue(m.Value, out var name))
                    used[m.Value] = name;
            }
        }
        return used.Count == 0 ? string.Empty : "# теги: " + string.Join("; ", used.Select(p => p.Key + "=" + p.Value));
    }

    // CorePlan.ConnectionTag / AppTag: буква и Guid без дефисов
    private static readonly Regex TagPattern = new("(?<![0-9a-f])[ac][0-9a-f]{32}(?![0-9a-f])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
