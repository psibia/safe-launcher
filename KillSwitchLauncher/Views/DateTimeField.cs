using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace KillSwitchLauncher.Views;

/// <summary>
/// Поле «дата и время» для своего промежутка выгрузки: показывает «Сегодня, 10:15», по щелчку открывает карточку
/// с тремя колонками (день, час, минуты через 5). Неверную дату не ввести: она выбирается из списка, будущее недоступно.
/// Выбор применяется сразу, «Готово» только закрывает карточку.
/// </summary>
public sealed class DateTimeField : UserControl
{
    private const int MinuteStep = 5;
    private const double ItemHeight = 32;
    private const int VisibleItems = 6;

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly Border _box;
    private readonly TextBlock _text;
    private readonly Popup _popup;
    private readonly List<(Border Item, DateTime Day)> _days = new();
    private readonly List<(Border Item, int Hour)> _hours = new();
    private readonly List<(Border Item, int Minute)> _minutes = new();
    private ScrollViewer? _daysScroll, _hoursScroll, _minutesScroll;
    private Border? _nowButton;
    private DateTime? _value;
    private long _closedAt;

    public DateTimeField()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;

        _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var icon = new TextBlock
        {
            Text = "", // календарь
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 1);
        grid.Children.Add(_text);
        grid.Children.Add(icon);
        _box = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 0, 12, 0),
            MinHeight = 34,
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Transparent,
            Child = grid
        };
        Content = _box;

        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -14,
            VerticalOffset = -8,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade
        };
        _popup.Closed += (_, _) =>
        {
            _closedAt = Environment.TickCount64;
            UpdateLook();
        };

        Loaded += (_, _) => UpdateLook();
        MouseEnter += (_, _) => UpdateLook();
        MouseLeave += (_, _) => UpdateLook();
        IsEnabledChanged += (_, _) => UpdateLook();
        GotKeyboardFocus += (_, _) => UpdateLook();
        LostKeyboardFocus += (_, _) => UpdateLook();
        UpdateText();
    }

    /// <summary>Выбор поменялся (щелчком в карточке или «Сейчас»).</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Карточка открылась: окно выбирает «свой промежуток».</summary>
    public event EventHandler? Opened;

    /// <summary>Можно выбрать «Сейчас» (конец промежутка): тогда <see cref="Value"/> равно null.</summary>
    public bool AllowNow { get; set; }

    /// <summary>Самый ранний день в списке (журналы старше уже удалены).</summary>
    public DateTime FirstDay { get; set; } = DateTime.Today.AddDays(-7);

    /// <summary>Местное время с точностью до минуты; null — «Сейчас».</summary>
    public DateTime? Value
    {
        get => _value;
        set
        {
            DateTime? next = value is { } v ? Clamp(v) : AllowNow ? null : Clamp(DateTime.Now);
            if (next == _value)
                return;
            _value = next;
            UpdateText();
            if (_popup.IsOpen)
                UpdateItems(scroll: false);
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Время с округлением вниз до шага минут: 10:17 → 10:15.</summary>
    public static DateTime RoundDown(DateTime local) =>
        new(local.Year, local.Month, local.Day, local.Hour, local.Minute / MinuteStep * MinuteStep, 0, local.Kind);

    /// <summary>«Сегодня, 10:15», «Вчера, 23:40», «пн, 12.10, 09:00»; null — «Сейчас».</summary>
    public static string Format(DateTime? value, DateTime today)
    {
        if (value is not { } v)
            return "Сейчас";
        return DayTitle(v.Date, today) + ", " + v.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static string DayTitle(DateTime day, DateTime today) =>
        day == today ? "Сегодня"
        : day == today.AddDays(-1) ? "Вчера"
        : day.ToString("ddd, dd.MM", Ru);

    /// <summary>Будущее и дни раньше списка недоступны.</summary>
    private DateTime Clamp(DateTime local)
    {
        var minute = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Local);
        var now = RoundDown(DateTime.Now);
        if (minute > now)
            return now;
        return minute < FirstDay.Date ? FirstDay.Date : minute;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // Иначе окно начнет перетаскиваться за поле.
        e.Handled = true;
        Focus();
        // Щелчок по полю при открытой карточке сначала закрывает ее — второй раз не открываем.
        if (Environment.TickCount64 - _closedAt > 250)
            Open();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Esc и Enter при открытой карточке закрывают ее, а не окно выгрузки.
        if (_popup.IsOpen && e.Key is Key.Escape or Key.Enter)
        {
            e.Handled = true;
            Close();
        }
        else if (!_popup.IsOpen && e.Key is Key.Space or Key.F4 or Key.Down)
        {
            e.Handled = true;
            Open();
        }
    }

    private void Open()
    {
        if (_popup.IsOpen || !IsEnabled)
            return;
        _popup.Child = BuildCard();
        _popup.IsOpen = true;
        UpdateLook();
        UpdateItems(scroll: true);
        Opened?.Invoke(this, EventArgs.Empty);
    }

    private void Close() => _popup.IsOpen = false;

    private Brush Res(string key) => (Brush)FindResource(key);

    private void UpdateText()
    {
        _text.Text = Format(_value, DateTime.Today);
    }

    private void UpdateLook()
    {
        if (!IsLoaded)
            return;
        var active = _popup.IsOpen || IsKeyboardFocused;
        _box.Background = active ? Res("CardBg") : Res("RowBg");
        _box.BorderBrush = active ? Res("Accent") : IsMouseOver && IsEnabled ? new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE8)) : Brushes.Transparent;
        Foreground = IsEnabled ? Res("TextPrimary") : Res("TextMuted");
        Cursor = IsEnabled ? Cursors.Hand : Cursors.Arrow;
    }

    // ---------------- Карточка выбора ----------------

    private FrameworkElement BuildCard()
    {
        _days.Clear();
        _hours.Clear();
        _minutes.Clear();
        var today = DateTime.Today;
        for (var day = today; day >= FirstDay.Date; day = day.AddDays(-1))
            _days.Add((NewItem(DayTitle(day, today), HorizontalAlignment.Left), day));
        for (var hour = 0; hour < 24; hour++)
            _hours.Add((NewItem(hour.ToString("00", CultureInfo.InvariantCulture), HorizontalAlignment.Center), hour));
        for (var minute = 0; minute < 60; minute += MinuteStep)
            _minutes.Add((NewItem(minute.ToString("00", CultureInfo.InvariantCulture), HorizontalAlignment.Center), minute));

        foreach (var (item, day) in _days)
            item.MouseLeftButtonUp += (_, _) => Pick(Draft().Date == day ? Draft() : day.Add(Draft().TimeOfDay));
        foreach (var (item, hour) in _hours)
            item.MouseLeftButtonUp += (_, _) => Pick(Draft().Date.AddHours(hour).AddMinutes(Draft().Minute));
        foreach (var (item, minute) in _minutes)
            item.MouseLeftButtonUp += (_, _) => Pick(Draft().Date.AddHours(Draft().Hour).AddMinutes(minute));

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddColumn(columns, 0, "День", _days.ConvertAll(d => d.Item), out _daysScroll);
        AddColumn(columns, 1, "Час", _hours.ConvertAll(h => h.Item), out _hoursScroll);
        AddColumn(columns, 2, "Мин", _minutes.ConvertAll(m => m.Item), out _minutesScroll);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _nowButton = null;
        if (AllowNow)
        {
            _nowButton = NewItem("Сейчас", HorizontalAlignment.Center);
            _nowButton.Padding = new Thickness(14, 0, 14, 0);
            _nowButton.HorizontalAlignment = HorizontalAlignment.Left;
            _nowButton.MouseLeftButtonUp += (_, _) =>
            {
                Value = null;
                Close();
            };
            footer.Children.Add(_nowButton);
        }
        var done = new Button
        {
            Content = "Готово",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(18, 0, 18, 0),
            MinHeight = 34
        };
        done.Click += (_, _) => Close();
        Grid.SetColumn(done, 1);
        footer.Children.Add(done);

        var panel = new StackPanel();
        panel.Children.Add(columns);
        panel.Children.Add(footer);

        var card = new Border
        {
            Background = Res("CardBg"),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(10),
            Effect = (System.Windows.Media.Effects.Effect)FindResource("PopupShadow"),
            Child = panel
        };
        // Внешний отступ — место под тень, иначе ее обрежет край всплывающего окна.
        var root = new Border
        {
            Padding = new Thickness(14),
            Child = card,
            Focusable = false
        };
        TextElement.SetFontFamily(root, (FontFamily)FindResource("UiFont"));
        TextElement.SetFontSize(root, 13);
        TextElement.SetForeground(root, Res("TextPrimary"));
        root.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter)
            {
                e.Handled = true;
                Close();
                Focus();
            }
        };
        return root;
    }

    private void AddColumn(Grid grid, int column, string title, List<Border> items, out ScrollViewer scroll)
    {
        var header = new TextBlock
        {
            Text = title,
            FontSize = 11.5,
            Foreground = Res("TextMuted"),
            Margin = new Thickness(column == 0 ? 10 : 0, 2, 0, 6),
            HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center
        };
        Grid.SetColumn(header, column);
        grid.Children.Add(header);

        var list = new StackPanel();
        foreach (var item in items)
            list.Children.Add(item);
        scroll = new ScrollViewer
        {
            Content = list,
            Height = VisibleItems * (ItemHeight + 2),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(column == 0 ? 0 : 4, 0, 0, 0),
            Focusable = false
        };
        Grid.SetColumn(scroll, column);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
    }

    private Border NewItem(string text, HorizontalAlignment align)
    {
        var item = new Border
        {
            Height = ItemHeight,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(10, 0, 10, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = align }
        };
        item.MouseEnter += (_, _) => Highlight(item, true);
        item.MouseLeave += (_, _) => Highlight(item, false);
        return item;
    }

    private void Highlight(Border item, bool over)
    {
        if (item.Tag is true || !item.IsEnabled)
            return;
        item.Background = over ? Res("RowBg") : Brushes.Transparent;
    }

    /// <summary>То, что отмечено в карточке: значение или текущее время для «Сейчас».</summary>
    private DateTime Draft() => _value ?? RoundDown(DateTime.Now);

    private void Pick(DateTime local) => Value = Clamp(local);

    private void UpdateItems(bool scroll)
    {
        var now = RoundDown(DateTime.Now);
        var draft = Draft();
        var isNow = _value is null;
        foreach (var (item, day) in _days)
            Mark(item, !isNow && day == draft.Date, enabled: true);
        foreach (var (item, hour) in _hours)
            Mark(item, !isNow && hour == draft.Hour, enabled: draft.Date < now.Date || hour <= now.Hour);
        foreach (var (item, minute) in _minutes)
            Mark(item, !isNow && minute == draft.Minute,
                enabled: draft.Date < now.Date || draft.Hour < now.Hour || minute <= now.Minute);
        if (_nowButton is not null)
            Mark(_nowButton, isNow, enabled: true);

        if (!scroll)
            return;
        // Отмеченное — в середину колонки.
        ScrollTo(_daysScroll, _days.FindIndex(d => d.Day == draft.Date));
        ScrollTo(_hoursScroll, draft.Hour);
        ScrollTo(_minutesScroll, draft.Minute / MinuteStep);
    }

    private static void ScrollTo(ScrollViewer? scroll, int index)
    {
        if (scroll is null || index < 0)
            return;
        var offset = Math.Max(0, (index - (VisibleItems - 1) / 2.0) * (ItemHeight + 2));
        scroll.Dispatcher.BeginInvoke(() => scroll.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Mark(Border item, bool selected, bool enabled)
    {
        item.Tag = selected;
        item.IsEnabled = enabled;
        item.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
        item.Background = selected ? Res("AccentSoft") : item.IsMouseOver && enabled ? Res("RowBg") : Brushes.Transparent;
        if (item.Child is TextBlock text)
        {
            text.Foreground = selected ? Res("Accent") : enabled ? Res("TextPrimary") : Res("TextMuted");
            text.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}
