using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Домены-исключения (общие и приложения): приводит введённое к виду «example.com» (без схемы, пути, «*.» и с IDN
/// в punycode), отбрасывает то, что доменом не является. Домен действует вместе с поддоменами.
/// </summary>
public static class DomainList
{
    /// <summary>Больше исключений у одного приложения служба не примет (CoreConfigBuilder.MaxDomainsPerApp).</summary>
    public const int MaxPerApp = 1000;

    private static readonly IdnMapping Idn = new();

    /// <summary>Строки поля ввода: по одному домену в строке, запятые, точки с запятой и пробелы тоже разделяют.</summary>
    public static List<string> Split(string? text) =>
        (text ?? string.Empty).Split(new[] { '\n', '\r', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>Что не так в поле ввода: null — всё можно сохранить.</summary>
    public static string? Problem(string? text)
    {
        var wrong = Split(text).Where(d => !TryNormalize(d, out _)).ToList();
        return wrong.Count > 0 ? $"Это не похоже на домен: {string.Join(", ", wrong.Take(3))}. Пишите так: kaspi.kz." : null;
    }

    /// <summary>
    /// Исключения приложения с учётом общих: свои дополняют общие или заменяют их. Не больше <see cref="MaxPerApp"/>:
    /// лишнее (только при ручной правке настроек) отбрасывается и идёт через подключение, а не ломает ядро всем.
    /// </summary>
    public static List<string> Combine(IEnumerable<string>? common, IEnumerable<string>? own, DomainsMode mode)
    {
        var all = mode == DomainsMode.OwnOnly
            ? own ?? Enumerable.Empty<string>()
            : (common ?? Enumerable.Empty<string>()).Concat(own ?? Enumerable.Empty<string>());
        return Normalize(all).Take(MaxPerApp).ToList();
    }

    public static List<string> Normalize(IEnumerable<string> domains)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var domain in domains)
        {
            if (TryNormalize(domain, out var normalized) && seen.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }

    /// <summary>«https://Discord.com/app», «*.discord.com», «.discord.com.» → «discord.com»; «пример.рф» → «xn--e1afmkfd.xn--p1ai».</summary>
    public static bool TryNormalize(string? text, out string domain)
    {
        domain = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            value = value[(scheme + 3)..];
        var end = value.IndexOfAny(new[] { '/', '?', '#' });
        if (end >= 0)
            value = value[..end];
        var at = value.LastIndexOf('@');
        if (at >= 0)
            value = value[(at + 1)..];
        var colon = value.IndexOf(':');
        if (colon >= 0)
            value = value[..colon];
        if (value.StartsWith("*.", StringComparison.Ordinal))
            value = value[2..];
        value = value.Trim('.');

        if (value.Length == 0 || !value.Contains('.'))
            return false;

        try
        {
            value = Idn.GetAscii(value).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (value.Length > 253 || value.Split('.').Any(label => !IsLabel(label)))
            return false;
        // IP-адрес — не домен: исключения по адресам здесь не поддерживаются.
        if (value.Split('.').All(label => label.All(char.IsAsciiDigit)))
            return false;

        domain = value;
        return true;
    }

    private static bool IsLabel(string label) =>
        label.Length is > 0 and <= 63 &&
        label[0] != '-' && label[^1] != '-' &&
        label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
}
