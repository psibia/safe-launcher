using System;
using System.Globalization;
using System.Text;

namespace SafeLauncher.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

/// <summary>
/// Одна запись технического журнала. В файле это одна строка UTF-8, поля через табуляцию:
/// <c>время UTC с мс, источник, уровень, область, код события, корреляция, текст</c>.
/// Время фиксированной ширины, поэтому строки сортируются как текст; источники сливаются по времени.
/// </summary>
public sealed record LogLine(DateTime TimeUtc, string Source, LogLevel Level, string Area, string Code, string Cid, string Text)
{
    public const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>Пустое поле (нет кода или корреляции).</summary>
    public const string None = "-";

    /// <summary>Чем в тексте заменяется перевод строки (запись всегда в одну строку).</summary>
    public const string NewLineMark = " ⏎ ";

    public string Format()
    {
        var sb = new StringBuilder(64 + Text.Length);
        sb.Append(FormatTime(TimeUtc)).Append('\t')
          .Append(Field(Source)).Append('\t')
          .Append(LevelName(Level)).Append('\t')
          .Append(Field(Area)).Append('\t')
          .Append(Field(Code)).Append('\t')
          .Append(Field(Cid)).Append('\t')
          .Append(Escape(Text));
        return sb.ToString();
    }

    public override string ToString() => Format();

    public static string FormatTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static bool TryParseTime(string text, out DateTime utc) =>
        DateTime.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);

    public static bool TryParse(string? line, out LogLine result)
    {
        result = null!;
        if (string.IsNullOrEmpty(line))
            return false;

        var parts = line.TrimEnd('\r').Split('\t', 7);
        if (parts.Length != 7 || !TryParseTime(parts[0], out var time) || !TryParseLevel(parts[2], out var level))
            return false;

        result = new LogLine(time, parts[1], level, parts[3], parts[4], parts[5], parts[6]);
        return true;
    }

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO"
    };

    public static bool TryParseLevel(string text, out LogLevel level)
    {
        switch (text)
        {
            case "DEBUG": level = LogLevel.Debug; return true;
            case "INFO": level = LogLevel.Info; return true;
            case "WARN": level = LogLevel.Warn; return true;
            case "ERROR": level = LogLevel.Error; return true;
            default: level = LogLevel.Info; return false;
        }
    }

    /// <summary>Текст в одну строку: переводы строк → « ⏎ », табуляция и прочие управляющие символы → пробел.</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c >= ' ' || c == '\u001b')
            {
                if (c == '\u001b')
                {
                    sb ??= new StringBuilder(text, 0, i, text.Length + 8);
                    sb.Append(' ');
                }
                else
                {
                    sb?.Append(c);
                }
                continue;
            }

            sb ??= new StringBuilder(text, 0, i, text.Length + 8);
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                sb.Append(NewLineMark);
            }
            else if (c == '\n')
            {
                sb.Append(NewLineMark);
            }
            else
            {
                sb.Append(' ');
            }
        }

        return (sb?.ToString() ?? text).Trim();
    }

    /// <summary>Обратно к многострочному тексту (для показа падения в окне).</summary>
    public static string Unescape(string text) => text.Replace(NewLineMark, Environment.NewLine);

    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return None;
        return value.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0 ? Escape(value).Replace(' ', '_') : value;
    }
}
