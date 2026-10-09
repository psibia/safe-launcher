using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using KillSwitchLauncher.Converters;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.Views;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>
/// Части интерфейса, которые можно проверить без показа окон: время в журнале, подсказка значка в трее,
/// переключатели настроек, «Что нового» и сам значок в трее.
/// </summary>
public class UiPartsTests
{
    // ---------------------------------------------------------------- журнал: время

    private static readonly DateTime Today = new(2026, 10, 9);

    [Fact]
    public void LogTime_Today_IsTimeOnly() =>
        Assert.Equal("14:05:09", ActivityLogWindow.FormatTime(Today.AddHours(14).AddMinutes(5).AddSeconds(9), Today));

    [Fact]
    public void LogTime_Yesterday() =>
        Assert.Equal("вчера 23:10", ActivityLogWindow.FormatTime(Today.AddDays(-1).AddHours(23).AddMinutes(10), Today));

    [Fact]
    public void LogTime_Earlier_HasDate() =>
        Assert.Equal("07.10 14:03", ActivityLogWindow.FormatTime(Today.AddDays(-2).AddHours(14).AddMinutes(3), Today));

    // ---------------------------------------------------------------- трей

    [Theory]
    [InlineData("Безопасный лаунчер", 127, "Безопасный лаунчер")]
    [InlineData("abcdef", 6, "abcdef")]
    [InlineData("abcdefg", 6, "abcde…")]
    public void TrayText_IsCutToWindowsLimit(string text, int max, string expected)
    {
        var result = TrayIcon.Truncate(text, max);

        Assert.Equal(expected, result);
        Assert.True(result.Length <= max);
    }

    [Fact]
    public void TrayIcon_AppearsAndDisappears()
    {
        Sta.Run(() =>
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack; // регистрирует адреса pack:// без запуска приложения
            var tray = new TrayIcon("Безопасный лаунчер · тест");
            try
            {
                Assert.True(tray.IsShown, "Значок должен появиться в области уведомлений");
                tray.Update("VPN отключён", alert: true);
                tray.Update("VPN подключён", alert: false);
            }
            finally
            {
                tray.Dispose();
            }

            Assert.False(tray.IsShown);
        });
    }

    // ---------------------------------------------------------------- переключатели в настройках

    [Fact]
    public void SegmentSwitch_ShowsSelectedValue()
    {
        var converter = new EnumToBooleanConverter();

        Assert.Equal(true, converter.Convert(HideBehavior.Tray, typeof(bool), "Tray", CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert(HideBehavior.Tray, typeof(bool), "Exit", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SegmentSwitch_ClickSelectsValue()
    {
        var converter = new EnumToBooleanConverter();

        Assert.Equal(HideBehavior.Exit, converter.ConvertBack(true, typeof(HideBehavior), "Exit", CultureInfo.InvariantCulture));
        Assert.Equal(VpnRuleMode.AllOfSelected, converter.ConvertBack(true, typeof(VpnRuleMode), "AllOfSelected", CultureInfo.InvariantCulture));
        Assert.Same(Binding.DoNothing, converter.ConvertBack(false, typeof(HideBehavior), "Exit", CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------- «Что нового»

    private static string TextOf(InlineCollection inlines)
    {
        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline is Run run)
                text.Append(run.Text);
            else if (inline is Span span)
                text.Append(TextOf(span.Inlines));
        }

        return text.ToString();
    }

    [Fact]
    public void ReleaseNotes_HeadingsBoldCodeAndLinks()
    {
        Sta.Run(() =>
        {
            var host = new StackPanel();

            MarkdownLite.Render(
                "<!-- служебный комментарий -->\r\n## Что нового\r\n\r\nТекст с **жирным**, `кодом` и [ссылкой](https://example.com) ![картинка](a.png)\r\n---\r\n",
                host);

            Assert.Equal(2, host.Children.Count);

            var heading = Assert.IsType<TextBlock>(host.Children[0]);
            Assert.Equal("Что нового", TextOf(heading.Inlines));
            Assert.Equal(FontWeights.Bold, heading.FontWeight);

            var paragraph = Assert.IsType<TextBlock>(host.Children[1]);
            Assert.Equal("Текст с жирным, кодом и ссылкой ", TextOf(paragraph.Inlines));
            Assert.Contains(paragraph.Inlines, i => i is Bold);
            Assert.Contains(paragraph.Inlines.OfType<Run>(), r => r.Text == "кодом" && r.FontFamily.Source == "Consolas");
        });
    }

    [Fact]
    public void ReleaseNotes_Empty_RendersNothing()
    {
        Sta.Run(() =>
        {
            var host = new StackPanel();
            host.Children.Add(new TextBlock());

            MarkdownLite.Render("", host);

            Assert.Empty(host.Children);
        });
    }
}
