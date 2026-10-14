using System;
using System.Text.RegularExpressions;

namespace SafeLauncher.Logging;

/// <summary>
/// Разбор строк sing-box: уровень и текст без собственной метки времени ядра (время ставит тот, кто принял строку).
/// Форматы: <c>INFO[0012] текст</c> (вывод в консоль без времени), <c>INFO текст</c>,
/// <c>+0300 2026-10-14 11:41:02 INFO текст</c> (с временем). Паника Go (<c>panic:</c>, <c>fatal error:</c> и стек после нее) — ERROR.
/// Экземпляр помнит, что идет стек паники, поэтому на каждый поток строк свой.
/// </summary>
public sealed class CoreLogParser
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    private static readonly Regex Leveled = new(
        @"^(?:[+-]\d{4} \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)? )?(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL|PANIC)(?:\[\d+\])? (.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private bool _panic;

    /// <summary>Результат разбора. <c>null</c> — строку не писать (пустая или trace).</summary>
    public (LogLevel Level, string Text)? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var line = Ansi.Replace(raw, string.Empty).TrimEnd();
        var m = Leveled.Match(line);
        if (m.Success)
        {
            _panic = false;
            var level = Level(m.Groups[1].Value);
            if (level is null)
                return null;
            return (level.Value, m.Groups[2].Value);
        }

        if (line.StartsWith("panic:", StringComparison.Ordinal) || line.StartsWith("fatal error:", StringComparison.Ordinal) ||
            line.StartsWith("goroutine ", StringComparison.Ordinal) && line.EndsWith(':'))
        {
            _panic = true;
        }

        return (_panic ? LogLevel.Error : LogLevel.Info, line);
    }

    /// <summary>Уровень записи из потока clash API (<c>type</c>). <c>null</c> — trace, не пишем.</summary>
    public static LogLevel? Level(string type) => type.ToUpperInvariant() switch
    {
        "TRACE" => null,
        "DEBUG" => LogLevel.Debug,
        "INFO" => LogLevel.Info,
        "WARN" or "WARNING" => LogLevel.Warn,
        "ERROR" or "FATAL" or "PANIC" => LogLevel.Error,
        _ => LogLevel.Info
    };
}
