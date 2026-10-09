using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillSwitchLauncher.Views;

/// <summary>Небольшое окно с описанием изменений релиза. Описание показывается здесь же, без браузера.</summary>
public partial class ChangelogWindow : Window
{
    private ChangelogWindow(string title, string subtitle, string notes)
    {
        InitializeComponent();
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        SubtitleText.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;

        if (string.IsNullOrWhiteSpace(notes))
            NotesHost.Children.Add(new TextBlock { Text = "Описание изменений не заполнено.", TextWrapping = TextWrapping.Wrap });
        else
            MarkdownLite.Render(notes, NotesHost);

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
    }

    /// <summary>Показывает описание. С canUpdate добавляется кнопка «Обновить»; true — пользователь её нажал.</summary>
    public static bool Show(Window? owner, string title, string subtitle, string notes, bool canUpdate)
    {
        var window = new ChangelogWindow(title, subtitle, notes);
        window.AddButton(canUpdate ? "Позже" : "Закрыть", "SecondaryButton", result: false, isDefault: !canUpdate, isCancel: true);
        if (canUpdate)
            window.AddButton("Обновить", "PrimaryButton", result: true, isDefault: true, isCancel: false);

        if (owner is { IsLoaded: true, IsVisible: true })
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return window.ShowDialog() == true;
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
}
