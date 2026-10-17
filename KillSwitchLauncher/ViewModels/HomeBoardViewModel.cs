using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.ViewModels;

/// <summary>
/// Перестановка плиток главного экрана перетаскиванием. Порядок плиток — порядок <see cref="AppSettings.Apps"/>
/// в settings.json, отдельного поля нет. Мышь и разметка — в Views/TileDragController. Работает только в UI-потоке.
/// </summary>
public sealed class HomeBoardViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<AppTileViewModel> _apps;
    private readonly Action _save;

    private AppTileViewModel? _dragged;
    private List<AppTileViewModel>? _before;
    private bool _moving;

    /// <param name="settings">Порядок приложений после перестановки пишется в settings.Apps.</param>
    /// <param name="apps">MainViewModel.Apps: плитки в порядке показа.</param>
    /// <param name="save">Сохранить настройки (MainViewModel.SaveSettings).</param>
    public HomeBoardViewModel(AppSettings settings, ObservableCollection<AppTileViewModel> apps, Action save)
    {
        _settings = settings;
        _apps = apps;
        _save = save;
        _apps.CollectionChanged += OnAppsChanged;
    }

    public bool IsDragging => _dragged is not null;

    /// <summary>Плитка, которую перетаскивают.</summary>
    public AppTileViewModel? Dragged => _dragged;

    /// <summary>Где сейчас перетаскиваемая плитка; -1 — перетаскивания нет.</summary>
    public int DraggedIndex => _dragged is null ? -1 : _apps.IndexOf(_dragged);

    /// <summary>Начать перетаскивание: запомнить порядок для отмены, на месте плитки — пустая ячейка.</summary>
    public bool BeginDrag(AppTileViewModel tile)
    {
        if (IsDragging || !_apps.Contains(tile))
            return false;

        _before = _apps.ToList();
        _dragged = tile;
        tile.IsDragSource = true;
        OnPropertyChanged(nameof(IsDragging));
        return true;
    }

    /// <summary>Переставить плитку на место <paramref name="index"/>. Не сохраняет.</summary>
    public void MoveDragged(int index)
    {
        if (_dragged is null)
            return;
        var from = _apps.IndexOf(_dragged);
        if (from < 0)
            return;
        index = Math.Clamp(index, 0, _apps.Count - 1);
        if (index != from)
            Move(from, index);
    }

    /// <summary>Отпустили: порядок остаётся и сохраняется, если изменился. Без перетаскивания ничего не делает.</summary>
    public void Drop()
    {
        if (_dragged is not { } tile || _before is not { } before)
            return;

        EndDrag(tile);
        if (!before.SequenceEqual(_apps))
        {
            WriteOrder();
            _save();
        }
    }

    /// <summary>Esc, отпустили вне области плиток, мышь перехватило другое окно: порядок как до начала, ничего не сохраняется.</summary>
    public void CancelDrag()
    {
        if (_dragged is not { } tile || _before is not { } before)
            return;

        for (var i = 0; i < before.Count; i++)
        {
            var current = _apps.IndexOf(before[i]);
            if (current >= 0 && current != i)
                Move(current, i);
        }
        EndDrag(tile);
    }

    private void Move(int from, int to)
    {
        _moving = true;
        try
        {
            _apps.Move(from, to);
        }
        finally
        {
            _moving = false;
        }
    }

    private void EndDrag(AppTileViewModel tile)
    {
        tile.IsDragSource = false;
        _dragged = null;
        _before = null;
        OnPropertyChanged(nameof(IsDragging));
    }

    /// <summary>
    /// Приложение добавили или убрали во время перетаскивания (практически невозможно): перетаскивание заканчивается
    /// без отмены — менять список внутри его же события нельзя. Порядок в настройках приводится к экрану.
    /// </summary>
    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_moving || _dragged is not { } tile)
            return;
        EndDrag(tile);
        WriteOrder();
    }

    /// <summary>settings.Apps в порядке плиток; приложения без плитки (их нет в норме) остаются в конце.</summary>
    private void WriteOrder()
    {
        var position = new Dictionary<AppEntry, int>();
        for (var i = 0; i < _apps.Count; i++)
            position.TryAdd(_apps[i].Entry, i);

        // Новый список целиком, а не перестановка на месте: кто сейчас перебирает старый, дочитает его без ошибки.
        _settings.Apps = _settings.Apps
            .Select((entry, index) => (entry, index))
            .OrderBy(x => position.TryGetValue(x.entry, out var p) ? p : _apps.Count + x.index)
            .Select(x => x.entry)
            .ToList();
    }
}
