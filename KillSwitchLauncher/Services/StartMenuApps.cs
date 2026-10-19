using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KillSwitchLauncher.Services;

/// <summary>Программа из меню «Пуск»: ярлык и то, что он запускает.</summary>
public sealed record StartMenuApp(string Name, string ShortcutPath, string ExePath);

/// <summary>
/// Программы из меню «Пуск» для мастера первого запуска: ярлыки обоих меню (всех пользователей и текущего),
/// которые запускают .exe. Удаление, справка, сайты и служебные программы Windows отбрасываются.
/// </summary>
public static class StartMenuApps
{
    private static readonly Regex Noise = new(
        @"uninstall|удал|deinstall|readme|help|справк|documentation|документац|license|лиценз|release notes|website|сайт|" +
        @"setup|install|update|обновл|repair|восстанов|debug|safe mode|безопасн.. режим",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Долго (сотни ярлыков): вызывать в фоновом потоке.</summary>
    public static IReadOnlyList<StartMenuApp> Find()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        };
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var found = new List<StartMenuApp>();
        foreach (var folder in folders.Where(f => f.Length > 0 && Directory.Exists(f)))
        {
            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var link in links)
            {
                ShortcutTarget target;
                try
                {
                    target = ShortcutResolver.Resolve(link);
                }
                catch (Exception)
                {
                    continue;
                }

                if (Pick(Path.GetFileNameWithoutExtension(link), link, target.TargetPath, windows) is { } app)
                    found.Add(app);
            }
        }

        return Distinct(found);
    }

    /// <summary>Подходит ли ярлык: ведёт на существующий .exe, не служебный и не из папки Windows.</summary>
    internal static StartMenuApp? Pick(string name, string link, string target, string windowsFolder, Func<string, bool>? exists = null)
    {
        if (string.IsNullOrWhiteSpace(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        // Только по названию ярлыка: Discord и Slack запускаются через свой Update.exe.
        if (Noise.IsMatch(name))
            return null;
        if (windowsFolder.Length > 0 && target.StartsWith(windowsFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!(exists ?? File.Exists)(target))
            return null;
        return new StartMenuApp(name.Trim(), link, target);
    }

    /// <summary>Одна строка на программу (ярлык бывает в обоих меню), по алфавиту.</summary>
    internal static IReadOnlyList<StartMenuApp> Distinct(IEnumerable<StartMenuApp> apps) =>
        apps.GroupBy(a => a.ExePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(a => a.Name.Length).First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // Клавиши русской раскладки на тех же местах, что и английской: «еуд» набрано вместо «tel».
    private const string EnKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    private const string RuKeys = "йцукенгшщзхъфывапролджэячсмитьбюё";

    /// <summary>
    /// Поиск в списке: каждое слово запроса есть в названии ярлыка или в имени .exe, без учёта регистра, «ё» и «е»
    /// не различаются. Запрос, набранный не в той раскладке, тоже находит программу.
    /// </summary>
    public static bool Matches(StartMenuApp app, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var exe = app.ExePath[(app.ExePath.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        var text = Normalize(app.Name + " " + exe);
        var words = Normalize(query);
        return HasAll(text, words) || HasAll(text, Normalize(SwitchLayout(words)));
    }

    private static bool HasAll(string text, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => text.Contains(w, StringComparison.Ordinal));

    private static string Normalize(string s) => s.ToLowerInvariant().Replace('ё', 'е');

    private static string SwitchLayout(string s) => new(s.Select(c =>
    {
        var i = EnKeys.IndexOf(c);
        if (i >= 0)
            return RuKeys[i];
        i = RuKeys.IndexOf(c);
        return i >= 0 ? EnKeys[i] : c;
    }).ToArray());
}
