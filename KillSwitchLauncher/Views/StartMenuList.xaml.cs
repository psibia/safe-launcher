using System.Windows.Controls;
using System.Windows.Input;

namespace KillSwitchLauncher.Views;

/// <summary>Программы из меню «Пуск» с галочками. Вся логика — в StartMenuListViewModel.</summary>
public partial class StartMenuList : UserControl
{
    public StartMenuList()
    {
        InitializeComponent();
    }

    /// <summary>Esc очищает поиск. Пустое поле Esc не трогает: его обработает страница.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchField.Text.Length > 0)
        {
            SearchField.Clear();
            e.Handled = true;
        }
    }
}
