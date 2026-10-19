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
    private bool _isShown = true;
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

    /// <summary>Подходит под поиск. Скрытая поиском галочка не снимается.</summary>
    public bool IsShown
    {
        get => _isShown;
        set => SetField(ref _isShown, value);
    }
}

/// <summary>
/// Программы из меню «Пуск» с галочками: шаг «Приложения» мастера первого запуска и страница «Добавить приложения».
/// Поиск по названию только скрывает строки: отмеченные раньше программы остаются отмеченными и добавятся.
/// </summary>
public sealed class StartMenuListViewModel : ObservableObject
{
    private readonly Func<Task<IReadOnlyList<StartMenuApp>>> _findApps;
    private readonly Func<string, ImageSource?> _icon;
    private readonly Func<StartMenuApp, bool> _isAdded;
    private bool _isLoading;
    private bool _loaded;
    private string _searchText = "";

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

    /// <summary>Строка поиска над списком: что искать в названиях программ.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value ?? ""))
                return;
            foreach (var choice in Apps)
                choice.IsShown = StartMenuApps.Matches(choice.App, _searchText);
            OnPropertyChanged(nameof(NothingFound));
        }
    }

    /// <summary>Программы в меню «Пуск» есть, но под поиск не подошла ни одна.</summary>
    public bool NothingFound => Apps.Count > 0 && !Apps.Any(a => a.IsShown);

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
                var choice = new StartMenuChoiceViewModel(app, _isAdded(app)) { IsShown = StartMenuApps.Matches(app, _searchText) };
                choice.PropertyChanged += OnChoiceChanged;
                Apps.Add(choice);
            }
            _loaded = true;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(NothingFound));

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
