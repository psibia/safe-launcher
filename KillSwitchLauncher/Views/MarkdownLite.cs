using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace KillSwitchLauncher.Views;

/// <summary>
/// Простая отрисовка описания релиза (Markdown с GitHub): заголовки, списки, абзацы, **жирный** и `код`.
/// Ссылки показываются только текстом: описание читается в лаунчере, браузер не открывается.
/// </summary>
internal static class MarkdownLite
{
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$");
    private static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+(.*)$");
    private static readonly Regex Numbered = new(@"^(\s*)(\d+)[.)]\s+(.*)$");
    private static readonly Regex Inline = new(@"(\*\*[^*]+\*\*|__[^_]+__|`[^`]+`|!?\[[^\]]*\]\([^)]*\))");
    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline);

    public static void Render(string markdown, Panel host)
    {
        host.Children.Clear();
        var text = Comment.Replace(markdown ?? string.Empty, string.Empty).Replace("\r\n", "\n").Trim();
        var pendingGap = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.Trim() is "---" or "***" or "___")
            {
                pendingGap = host.Children.Count > 0;
                continue;
            }

            FrameworkElement element;
            Match m;
            if ((m = Heading.Match(line)).Success)
            {
                var block = CreateText(m.Groups[2].Value);
                block.FontWeight = FontWeights.Bold;
                block.FontSize = m.Groups[1].Value.Length <= 2 ? 15 : 14;
                block.Margin = new Thickness(0, host.Children.Count > 0 ? 12 : 0, 0, 4);
                element = block;
                pendingGap = false;
            }
            else if ((m = Bullet.Match(line)).Success)
            {
                element = CreateListItem("•", m.Groups[2].Value, m.Groups[1].Value.Length);
            }
            else if ((m = Numbered.Match(line)).Success)
            {
                element = CreateListItem(m.Groups[2].Value + ".", m.Groups[3].Value, m.Groups[1].Value.Length);
            }
            else
            {
                element = CreateText(line.Trim());
                element.Margin = new Thickness(0, 0, 0, 3);
            }

            if (pendingGap)
            {
                element.Margin = new Thickness(element.Margin.Left, Math.Max(element.Margin.Top, 8), element.Margin.Right, element.Margin.Bottom);
                pendingGap = false;
            }

            host.Children.Add(element);
        }
    }

    private static FrameworkElement CreateListItem(string marker, string content, int indent)
    {
        var grid = new Grid { Margin = new Thickness(Math.Min(indent, 8) * 6, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new TextBlock { Text = marker, Foreground = (Brush)Application.Current.FindResource("Accent"), FontWeight = FontWeights.Bold };
        var body = CreateText(content);
        Grid.SetColumn(body, 1);
        grid.Children.Add(dot);
        grid.Children.Add(body);
        return grid;
    }

    private static TextBlock CreateText(string content)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 19 };

        foreach (var part in Inline.Split(content))
        {
            if (part.Length == 0) continue;

            if ((part.StartsWith("**") && part.EndsWith("**") || part.StartsWith("__") && part.EndsWith("__")) && part.Length > 4)
            {
                block.Inlines.Add(new Bold(new Run(part[2..^2])));
            }
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
            {
                block.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Consolas") });
            }
            else if (part.StartsWith('[') || part.StartsWith("!["))
            {
                // [текст](ссылка) → «текст»; картинки пропускаем
                var close = part.IndexOf("](", StringComparison.Ordinal);
                if (!part.StartsWith('!') && close > 1)
                    block.Inlines.Add(new Run(part[1..close]));
            }
            else
            {
                block.Inlines.Add(new Run(part));
            }
        }

        return block;
    }
}
