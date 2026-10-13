using System.Windows;
using System.Windows.Controls;

namespace KillSwitchLauncher.Views;

/// <summary>Страница настроек. Вся логика — в MainViewModel.</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>Появилось поле для нового названия подключения: сразу в него, с выделенным текстом.</summary>
    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            });
    }
}
