using System;
using System.Collections.Generic;
using System.Windows.Input;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.ViewModels;

/// <summary>
/// Страница «Добавить приложения» (кнопка «Добавить приложение» на главном экране): программы из меню «Пуск»
/// с галочками, как в мастере первого запуска, и «Выбрать в проводнике» для одной программы, которой нет в списке.
/// </summary>
public sealed class AddAppsViewModel : ObservableObject
{
    /// <param name="add">Добавить отмеченные программы и вернуться к плиткам.</param>
    /// <param name="browse">Выбрать одну программу в проводнике и открыть её страницу.</param>
    /// <param name="cancel">Вернуться к плиткам, ничего не добавляя.</param>
    public AddAppsViewModel(StartMenuListViewModel list, Action<IReadOnlyList<StartMenuApp>> add, Action browse, Action cancel)
    {
        List = list;
        AddCommand = new RelayCommand(() => add(List.Selected), () => List.SelectedCount > 0);
        BrowseCommand = new RelayCommand(browse);
        CancelCommand = new RelayCommand(cancel);
        List.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StartMenuListViewModel.SelectedCount))
            {
                OnPropertyChanged(nameof(AddText));
                OnPropertyChanged(nameof(SelectedText));
                CommandManager.InvalidateRequerySuggested();
            }
        };
    }

    public StartMenuListViewModel List { get; }

    public ICommand AddCommand { get; }

    /// <summary>«Выбрать в проводнике»: программа (.exe) или ярлык, ровно одна.</summary>
    public ICommand BrowseCommand { get; }

    public ICommand CancelCommand { get; }

    public string AddText => List.SelectedCount > 0 ? $"Добавить ({List.SelectedCount})" : "Добавить";

    public string SelectedText => List.SelectedCount > 0 ? $"Отмечено: {List.SelectedCount}" : "Отметьте программы, которые нужно добавить";
}
