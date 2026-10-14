using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace KillSwitchLauncher.Views;

public enum AlertKind
{
    Info,
    Warning,
    Error,

    /// <summary>Подтверждение опасного действия.</summary>
    Danger
}

/// <summary>Модальный диалог в стиле лаунчера вместо системного MessageBox.</summary>
public partial class AlertWindow : Window
{
    private AlertWindow(AlertKind kind, string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        SetMessage(message);
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        // Длинное объяснение в узкой карточке превращается в высокую «портянку»: чем длиннее текст, тем шире карточка.
        Card.Width = message.Length switch
        {
            > 900 => 720,
            > 400 => 560,
            _ => Card.Width
        };
        ApplyKind(kind);

        // Окно без рамки: перетаскивание за любую точку
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
    }

    /// <summary>Сообщение с одной кнопкой «Понятно».</summary>
    public static void ShowMessage(Window? owner, AlertKind kind, string title, string message)
    {
        var alert = new AlertWindow(kind, title, message);
        alert.AddButton("Понятно", "PrimaryButton", result: true, isDefault: true, isCancel: true);
        alert.ShowWithOwner(owner);
    }

    /// <summary>Вопрос «Отмена / Действие». Для опасных действий кнопка красная, а Enter выбирает «Отмена».</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmText, bool destructive)
    {
        var alert = new AlertWindow(destructive ? AlertKind.Danger : AlertKind.Info, title, message);
        alert.AddButton("Отмена", "SecondaryButton", result: false, isDefault: destructive, isCancel: true);
        alert.AddButton(confirmText, destructive ? "DangerButton" : "PrimaryButton", result: true, isDefault: !destructive, isCancel: false);
        return alert.ShowWithOwner(owner) == true;
    }

    /// <summary>Ошибка с кнопкой «Выгрузить журналы» (UI-6): true — пользователь хочет выгрузить журналы.</summary>
    public static bool ShowWithLogs(Window? owner, AlertKind kind, string title, string message)
    {
        var alert = new AlertWindow(kind, title, message);
        alert.AddButton("Выгрузить журналы", "SecondaryButton", result: true, isDefault: false, isCancel: false);
        alert.AddButton("Закрыть", "PrimaryButton", result: false, isDefault: true, isCancel: true);
        return alert.ShowWithOwner(owner) == true;
    }

    /// <summary>
    /// Вопрос при нажатии «Убрать в трей»: true — свернуть в трей, false — закрыть лаунчер, null — передумали (Esc).
    /// <paramref name="remember"/> — отмечено «Больше не спрашивать».
    /// </summary>
    public static bool? AskHide(Window? owner, out bool remember)
    {
        var alert = new AlertWindow(AlertKind.Info, "Свернуть в трей или закрыть?",
            "В трее лаунчер продолжит следить за VPN и закроет приложения, если VPN пропадёт. " +
            "Открыть его можно двойным кликом по значку возле часов.");
        alert.IconGlyph.Glyph = "\uE70E";
        alert.OptionCheck.Content = "Запомнить выбор и больше не спрашивать";
        alert.OptionCheck.Visibility = Visibility.Visible;

        bool? choice = null;
        alert.AddChoice("Закрыть лаунчер", "SecondaryButton", isDefault: false, () => choice = false);
        alert.AddChoice("Свернуть в трей", "PrimaryButton", isDefault: true, () => choice = true);
        alert.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                alert.Close();
        };

        alert.ShowWithOwner(owner);
        remember = choice is not null && alert.OptionCheck.IsChecked == true;
        return choice;
    }

    /// <summary>
    /// Уход со страницы с несохранёнными изменениями: true — сохранить, false — не сохранять, null — остаться (Esc).
    /// </summary>
    public static bool? AskSave(Window? owner, string title, string message, string saveText)
    {
        var alert = new AlertWindow(AlertKind.Info, title, message);
        bool? choice = null;
        alert.AddChoice("Остаться", "SecondaryButton", isDefault: false, () => choice = null);
        alert.AddChoice("Не сохранять", "SecondaryButton", isDefault: false, () => choice = false);
        alert.AddChoice(saveText, "PrimaryButton", isDefault: true, () => choice = true);
        alert.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                alert.Close();
        };

        alert.ShowWithOwner(owner);
        return choice;
    }

    private void AddChoice(string text, string styleKey, bool isDefault, System.Action onClick)
    {
        AddButton(text, styleKey, result: true, isDefault, isCancel: false);
        ((Button)ButtonsHost.Children[^1]).Click += (_, _) => onClick();
    }

    /// <summary>
    /// Текст сообщения. В длинных объяснениях абзац может начинаться с заголовка («Зачем.», «Как работает.»):
    /// он выделяется жирным, а между абзацами остается пустая строка.
    /// </summary>
    private void SetMessage(string message)
    {
        if (message.Length <= 400)
        {
            MessageText.Text = message;
            return;
        }

        MessageText.Inlines.Clear();
        var paragraphs = message.Split("\n\n");
        for (var i = 0; i < paragraphs.Length; i++)
        {
            if (i > 0)
            {
                MessageText.Inlines.Add(new LineBreak());
                MessageText.Inlines.Add(new LineBreak());
            }
            var text = paragraphs[i];
            var heading = Regex.Match(text, @"^([^.\n:]{2,30}\.) ");
            if (heading.Success)
            {
                MessageText.Inlines.Add(new Run(heading.Groups[1].Value) { FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary") });
                text = text[heading.Groups[1].Length..];
            }
            var lines = text.Split('\n');
            for (var j = 0; j < lines.Length; j++)
            {
                if (j > 0)
                    MessageText.Inlines.Add(new LineBreak());
                MessageText.Inlines.Add(new Run(lines[j]));
            }
        }
    }

    private void ApplyKind(AlertKind kind)
    {
        var (accent, soft, glyph) = kind switch
        {
            AlertKind.Warning => ("Warn", "WarnSoft", ""),
            AlertKind.Error => ("Danger", "DangerSoft", ""),
            AlertKind.Danger => ("Danger", "DangerSoft", ""),
            _ => ("Accent", "AccentSoft", "")
        };

        IconBox.SetResourceReference(Border.BackgroundProperty, soft);
        IconGlyph.SetResourceReference(GlyphIcon.ForegroundProperty, accent);
        IconGlyph.Glyph = glyph;
    }

    private void AddButton(string text, string styleKey, bool result, bool isDefault, bool isCancel)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(20, 9, 20, 9),
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 104,
            IsDefault = isDefault,
            IsCancel = isCancel,
            Style = (Style)FindResource(styleKey)
        };
        button.Click += (_, _) => DialogResult = result;
        ButtonsHost.Children.Add(button);
    }

    private bool? ShowWithOwner(Window? owner)
    {
        if (owner is { IsLoaded: true, IsVisible: true })
            Owner = owner;
        else
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return ShowDialog();
    }
}
