using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.ViewModels;

/// <summary>
/// Программа из меню «Пуск» с галочкой. Уже добавленная в лаунчер отмечена и недоступна: убрать её можно только
/// в меню её плитки.
/// </summary>
public sealed class StartMenuChoiceViewModel : ObservableObject
{
    private bool _isSelected;
    private ImageSource? _icon;

    public StartMenuChoiceViewModel(StartMenuApp app, bool isAdded = false)
    {
        App = app;
        IsAdded = isAdded;
        _isSelected = isAdded;
    }

    public StartMenuApp App { get; }
    public string Name => App.Name;
    public string Path => App.ExePath;

    /// <summary>Программа уже есть в лаунчере.</summary>
    public bool IsAdded { get; }

    public bool CanSelect => !IsAdded;

    public string ToolTip => IsAdded ? "Уже в лаунчере. Убрать можно в меню плитки." : Path;

    public ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsAdded)
                SetField(ref _isSelected, value);
        }
    }
}

/// <summary>
/// Программы из меню «Пуск» с галочками: шаг «Приложения» мастера первого запуска и страница «Добавить приложения».
/// </summary>
public sealed class StartMenuListViewModel : ObservableObject
{
    private readonly Func<Task<IReadOnlyList<StartMenuApp>>> _findApps;
    private readonly Func<string, ImageSource?> _icon;
    private readonly Func<StartMenuApp, bool> _isAdded;
    private bool _isLoading;
    private bool _loaded;

    /// <param name="findApps">Программы из меню «Пуск» (долго: в фоне).</param>
    /// <param name="icon">Иконка программы по пути к её .exe.</param>
    /// <param name="isAdded">Программа уже есть в лаунчере.</param>
    public StartMenuListViewModel(Func<Task<IReadOnlyList<StartMenuApp>>> findApps, Func<string, ImageSource?> icon, Func<StartMenuApp, bool> isAdded)
    {
        _findApps = findApps;
        _icon = icon;
        _isAdded = isAdded;
    }

    public ObservableCollection<StartMenuChoiceViewModel> Apps { get; } = new();

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    /// <summary>Поиск закончен, а подходящих программ в меню «Пуск» нет.</summary>
    public bool IsEmpty => _loaded && Apps.Count == 0;

    /// <summary>Отмеченные программы, которых ещё нет в лаунчере.</summary>
    public IReadOnlyList<StartMenuApp> Selected => Apps.Where(a => a.IsSelected && !a.IsAdded).Select(a => a.App).ToList();

    public int SelectedCount => Apps.Count(a => a.IsSelected && !a.IsAdded);

    /// <summary>Найти программы (один раз).</summary>
    public async Task LoadAsync()
    {
        if (_loaded || IsLoading)
            return;

        IsLoading = true;
        try
        {
            var apps = await _findApps();
            foreach (var app in apps)
            {
                var choice = new StartMenuChoiceViewModel(app, _isAdded(app));
                choice.PropertyChanged += OnChoiceChanged;
                Apps.Add(choice);
            }
            _loaded = true;
            OnPropertyChanged(nameof(IsEmpty));

            // Иконки — после списка, чтобы он появился сразу.
            foreach (var choice in Apps.ToList())
            {
                choice.Icon = _icon(choice.Path);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartMenuChoiceViewModel.IsSelected))
            OnPropertyChanged(nameof(SelectedCount));
    }
}
