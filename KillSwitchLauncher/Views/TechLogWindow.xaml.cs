using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.ViewModels;
using Microsoft.Win32;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.Views;

/// <summary>
/// Технический журнал (UI-3): строки лаунчера, службы и ядра вместе, вкладки по источнику, период, типы событий,
/// «только ошибки», поиск, живое обновление, отметка проблемы, экспорт показанного в .log, выделение и копирование строк,
/// разворот на весь экран.
/// Окно одно на лаунчер, не модальное.
/// </summary>
public partial class TechLogWindow : Window
{
    private const string AllPreset = "all";
    private const int MaxLines = 200_000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private static TechLogWindow? _open;

    private readonly ILogActions _logs;
    private readonly DispatcherTimer _searchDelay;
    private readonly DispatcherTimer _poll;
    private readonly IReadOnlyDictionary<string, string> _names;

    private List<LogLine> _all = new();
    private ObservableCollection<LineRow> _rows = new();
    private Func<LogLine, bool> _filter = _ => true;
    private string _preset = ExportPeriods.LastHour;
    private ExportPeriod? _period;
    private HashSet<string>? _groups;
    private CancellationTokenSource? _loading;
    private string? _serviceProblem;
    private DateTime _serviceSeenUtc;
    private HashSet<string> _serviceEdge = new();
    private bool _polling;
    private int _pendingNew;
    private ScrollViewer? _scroll;
    private bool _expanded;
    private (double Left, double Top, double CardWidth, double ListHeight) _normal;
    private Effect? _cardShadow;

    private TechLogWindow(ILogActions logs)
    {
        InitializeComponent();
        _logs = logs;
        _names = logs.TagNames();
        LinesList.ItemsSource = _rows;

        _searchDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _searchDelay!.Stop();
            ApplyFilter();
        }, Dispatcher);
        _searchDelay.Stop();
        _poll = new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => _ = PollServiceAsync(), Dispatcher);

        foreach (var tab in new[] { TabAll, TabLauncher, TabService, TabCore })
            tab.Checked += (_, _) => ApplyFilter();
        ErrorsOnlyBox.Checked += (_, _) => ApplyFilter();
        ErrorsOnlyBox.Unchecked += (_, _) => ApplyFilter();
        SearchBox.TextChanged += (_, _) =>
        {
            _searchDelay.Stop();
            _searchDelay.Start();
        };
        PeriodButton.Click += (_, _) => ShowPeriodMenu();
        TypesButton.Click += (_, _) => ShowTypesMenu();
        NewLinesButton.Click += (_, _) => ScrollToEnd();
        MarkButton.Click += (_, _) => ShowMarkPanel(true);
        MarkCancel.Click += (_, _) => ShowMarkPanel(false);
        MarkConfirm.Click += (_, _) => PlaceMark();
        // Esc в поле отметки закрывает только поле, а не окно (у «Закрыть» IsCancel).
        MarkText.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                PlaceMark();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ShowMarkPanel(false);
            }
        };
        ExportButton.Click += (_, _) => ExportView();
        CloseButton.Click += (_, _) => Close();
        CloseCornerButton.Click += (_, _) => Close();
        ExpandButton.Click += (_, _) => ToggleExpanded();
        MouseLeftButtonDown += (_, e) =>
        {
            // Двойной щелчок по заголовку — развернуть или вернуть, как у окон Windows.
            if (e.ClickCount == 2 && Header.IsMouseOver)
            {
                ToggleExpanded();
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed && !_expanded)
                DragMove();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11)
            {
                e.Handled = true;
                ToggleExpanded();
            }
        };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelected()));
        LinesList.SelectionChanged += (_, _) => UpdateCount();
        LinesList.ContextMenu = RowsMenu();

        TechLog.Added += OnLauncherLine;
        Closed += (_, _) =>
        {
            TechLog.Added -= OnLauncherLine;
            _poll.Stop();
            _loading?.Cancel();
            _open = null;
        };
        Loaded += (_, _) =>
        {
            _scroll = FindChild<ScrollViewer>(LinesList);
            if (_scroll is not null)
                _scroll.ScrollChanged += (_, e) =>
                {
                    if (AtBottom())
                    {
                        _pendingNew = 0;
                        NewLinesButton.Visibility = Visibility.Collapsed;
                    }
                };
        };

        _ = LoadAsync();
    }

    public static void ShowFor(Window? owner, ILogActions logs)
    {
        if (_open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new TechLogWindow(logs);
        window.FitTo(SystemParameters.WorkArea);
        window.PlaceOver(owner);
        _open = window;
        window.Show();
    }

    /// <summary>Карточка не шире экрана, список строк по высоте экрана (окно подстраивается под содержимое).</summary>
    private void FitTo(Rect area)
    {
        const double outside = 36 * 2 + 8;   // поле для тени и запас
        const double chrome = 380;            // заголовок, фильтры, поиск, нижние кнопки, отступы карточки
        Card.Width = Math.Max(860, Math.Min(Card.Width, area.Width - outside));
        ListHost.Height = Math.Clamp(area.Height - chrome, 220, 560);
    }

    /// <summary>По центру главного окна, если оно видно, иначе по центру экрана (как у журнала DNS).</summary>
    private void PlaceOver(Window? owner)
    {
        if (owner is not { IsVisible: true } || owner.WindowState == WindowState.Minimized)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        // Размер окна известен только после разметки (SizeToContent), поэтому ставим его на место в Loaded.
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Clamp(owner.Left + (owner.ActualWidth - ActualWidth) / 2, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
            Top = Math.Clamp(owner.Top + (owner.ActualHeight - ActualHeight) / 2, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        };
    }

    // ---------------------------------------------------------------- загрузка

    private bool IsLive => _preset is ExportPeriods.Last5 or ExportPeriods.Last15 or ExportPeriods.LastHour or ExportPeriods.Last3Hours or ExportPeriods.Today
        or ExportPeriods.Session or AllPreset;

    private ExportPeriod ResolvePeriod()
    {
        var now = DateTime.UtcNow;
        if (_preset == AllPreset)
            return new ExportPeriod(AllPreset, "Все хранимые", now.AddDays(-(TechLog.Settings.RetentionDays + 1)), now);
        var periods = _logs.GetExportChoices(null).Periods;
        return periods.FirstOrDefault(p => p.Preset == _preset) ?? periods[0];
    }

    private async Task LoadAsync()
    {
        _loading?.Cancel();
        var cancel = _loading = new CancellationTokenSource();
        _poll.Stop();
        var period = _period = ResolvePeriod();
        PeriodText.Text = "Период: " + LowerFirst(period.Title);
        PeriodButton.ToolTip = period.Title;
        ShowCenter("Загружаю…");

        try
        {
            var launcher = await Task.Run(() =>
            {
                TechLog.Store?.Flush();
                return TechLog.Read(period.FromUtc, period.ToUtc);
            }, cancel.Token);

            IReadOnlyList<LogLine> service = Array.Empty<LogLine>();
            _serviceProblem = null;
            try
            {
                service = await ServiceLogs.ReadAsync(period.FromUtc, period.ToUtc, null, null, cancel.Token);
            }
            catch (Exception ex) when (ex is CoreServiceException or TimeoutException or IOException)
            {
                _serviceProblem = ServiceLogs.Explain(ex);
            }
            if (cancel.IsCancellationRequested)
                return;

            var all = new List<LogLine>(launcher.Count + service.Count);
            all.AddRange(launcher);
            all.AddRange(service);
            LogStore.Sort(all);
            if (all.Count > MaxLines)
                all = all.GetRange(all.Count - MaxLines, MaxLines);
            _all = all;
            RememberServiceEdge(service.Count > 0 ? service : Array.Empty<LogLine>(), period.ToUtc);

            UnavailableHeader.Text = _serviceProblem is null ? string.Empty : "Журналы службы недоступны";
            UnavailableHeader.ToolTip = _serviceProblem;
            ApplyFilter();
            if (IsLive && _serviceProblem is null)
                _poll.Start();
        }
        catch (OperationCanceledException)
        {
            // выбран другой период
        }
        catch (Exception ex)
        {
            ShowCenter("Журнал не загрузился: " + ex.Message);
        }
    }

    private void RememberServiceEdge(IReadOnlyList<LogLine> service, DateTime fallback)
    {
        if (service.Count == 0)
        {
            _serviceSeenUtc = fallback;
            _serviceEdge = new HashSet<string>();
            return;
        }
        _serviceSeenUtc = service[^1].TimeUtc;
        _serviceEdge = service.Where(l => l.TimeUtc == _serviceSeenUtc).Select(l => l.Format()).ToHashSet();
    }

    /// <summary>Новые строки службы и ядра (журнал лаунчера приходит сам, через событие).</summary>
    private async Task PollServiceAsync()
    {
        if (_polling || !IsVisible || TabLauncher.IsChecked == true || _serviceProblem is not null)
            return;
        _polling = true;
        try
        {
            var lines = await ServiceLogs.ReadAsync(_serviceSeenUtc, DateTime.UtcNow.AddSeconds(1));
            // Строки той же миллисекунды, что последняя виденная, сверяются целиком: без повторов и без пропусков.
            var fresh = lines.Where(l => l.TimeUtc > _serviceSeenUtc || !_serviceEdge.Contains(l.Format())).ToList();
            if (fresh.Count == 0)
                return;
            var last = lines[^1].TimeUtc;
            if (last == _serviceSeenUtc)
                _serviceEdge.UnionWith(fresh.Select(l => l.Format()));
            else
                RememberServiceEdge(lines, last);
            foreach (var line in fresh)
                AddLive(line);
        }
        catch (Exception)
        {
            // служба занята или перезапускается: попробуем в следующий раз
        }
        finally
        {
            _polling = false;
        }
    }

    private void OnLauncherLine(LogLine line) => Dispatcher.BeginInvoke(() =>
    {
        if (IsLive && _period is not null)
            AddLive(line);
    });

    private void AddLive(LogLine line)
    {
        _all.Add(line);
        if (!_filter(line))
            return;
        var atBottom = AtBottom();
        _rows.Add(new LineRow(line, _names));
        UpdateCount();
        if (CenterText.Visibility == Visibility.Visible && LinesList.Visibility == Visibility.Visible)
            CenterText.Visibility = Visibility.Collapsed;
        if (atBottom)
            ScrollToEnd();
        else
        {
            _pendingNew++;
            NewLinesButton.Content = $"Новых строк: {_pendingNew:N0} ↓";
            NewLinesButton.Visibility = Visibility.Visible;
        }
    }

    // ---------------------------------------------------------------- фильтр

    private string? SelectedSource =>
        TabLauncher.IsChecked == true ? LogSources.Launcher
        : TabService.IsChecked == true ? LogSources.Service
        : TabCore.IsChecked == true ? LogSources.Core
        : null;

    private void ApplyFilter()
    {
        var source = SelectedSource;
        _filter = new TechLogFilter
        {
            Source = source,
            ErrorsOnly = ErrorsOnlyBox.IsChecked == true,
            Groups = _groups,
            Search = SearchBox.Text
        }.Compile();

        _pendingNew = 0;
        NewLinesButton.Visibility = Visibility.Collapsed;
        if (_period is null)
            return;

        // Вкладки службы и ядра без службы: объяснение в центре (J8), экспорт вкладки неактивен.
        if (source is LogSources.Service or LogSources.Core && _serviceProblem is { } problem)
        {
            _rows = new ObservableCollection<LineRow>();
            LinesList.ItemsSource = _rows;
            ShowCenter("Журналы службы недоступны: " + problem);
            ExportButton.IsEnabled = false;
            CountText.Text = string.Empty;
            return;
        }

        _rows = new ObservableCollection<LineRow>(_all.Where(_filter).Select(l => new LineRow(l, _names)));
        LinesList.ItemsSource = _rows;
        LinesList.Visibility = Visibility.Visible;
        UpdateCount();
        ExportButton.IsEnabled = _rows.Count > 0;
        if (_rows.Count == 0)
            ShowCenter(_all.Count == 0 ? "За этот период записей нет" : "Под фильтр ничего не подходит");
        else
        {
            CenterText.Visibility = Visibility.Collapsed;
            ScrollToEnd();
        }
    }

    /// <summary>«Строк: 120 · выделено 3, Ctrl+C — скопировать».</summary>
    private void UpdateCount()
    {
        var text = $"Строк: {_rows.Count:N0}" + (_all.Count >= MaxLines ? $" (последние {MaxLines:N0})" : string.Empty);
        var selected = LinesList.SelectedItems.Count;
        if (selected > 0)
            text += $" · выделено {selected:N0}, Ctrl+C — скопировать";
        CountText.Text = text;
    }

    private void ShowCenter(string text)
    {
        CenterText.Text = text;
        CenterText.Visibility = Visibility.Visible;
    }

    private void ShowPeriodMenu()
    {
        var menu = NewMenu();
        var periods = _logs.GetExportChoices(null).Periods.ToList();
        periods.Add(new ExportPeriod(AllPreset, "Все хранимые", DateTime.UtcNow, DateTime.UtcNow));
        foreach (var period in periods)
        {
            var item = NewItem(period.Title);
            item.Icon = period.Preset == _preset ? CheckIcon(true) : null;
            var preset = period.Preset;
            item.Click += (_, _) =>
            {
                _preset = preset;
                _ = LoadAsync();
            };
            menu.Items.Add(item);
        }
        Open(menu, PeriodButton);
    }

    private void ShowTypesMenu()
    {
        var menu = NewMenu();
        var all = NewItem("Все типы");
        all.Icon = _groups is null ? CheckIcon(true) : null;
        all.Click += (_, _) =>
        {
            _groups = null;
            UpdateTypes();
        };
        menu.Items.Add(all);
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        foreach (var group in LogAreaGroups.All)
        {
            var item = NewItem(group.Title);
            item.StaysOpenOnClick = true;
            item.Icon = CheckBoxIcon(_groups?.Contains(group.Key) == true);
            var key = group.Key;
            item.Click += (_, _) =>
            {
                _groups ??= new HashSet<string>(StringComparer.Ordinal);
                var on = !_groups.Remove(key);
                if (on)
                    _groups.Add(key);
                if (_groups.Count == 0)
                    _groups = null;
                item.Icon = CheckBoxIcon(on);
                all.Icon = _groups is null ? CheckIcon(true) : null;
                UpdateTypes();
            };
            menu.Items.Add(item);
        }
        Open(menu, TypesButton);
    }

    private void UpdateTypes()
    {
        // Выбранные типы видны прямо на кнопке (по порядку меню); не влезли — полный список в подсказке.
        var names = _groups is null
            ? new List<string>()
            : LogAreaGroups.All.Where(g => _groups.Contains(g.Key)).Select(g => g.Title).ToList();
        TypesText.Text = names.Count == 0 ? "Типы: все" : "Типы: " + string.Join(", ", names);
        TypesButton.ToolTip = names.Count == 0 ? "Показаны все типы событий" : string.Join("\n", names);
        ApplyFilter();
    }

    /// <summary>Меню по правой кнопке на строках: скопировать выделенные, выделить все.</summary>
    private ContextMenu RowsMenu()
    {
        var menu = NewMenu();
        var copy = NewItem("Скопировать выделенные строки");
        copy.Icon = MenuGlyph("\uE8C8");
        copy.Click += (_, _) => CopySelected();
        var all = NewItem("Выделить все строки");
        all.Icon = MenuGlyph("\uE8B3");
        all.Click += (_, _) => LinesList.SelectAll();
        menu.Items.Add(copy);
        menu.Items.Add(all);
        menu.Opened += (_, _) => copy.IsEnabled = LinesList.SelectedItems.Count > 0;
        return menu;
    }

    private TextBlock MenuGlyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe MDL2 Assets"),
        FontSize = 13,
        VerticalAlignment = VerticalAlignment.Center
    };

    private ContextMenu NewMenu() => new() { Style = TryStyle("AppContextMenu") };

    private MenuItem NewItem(string header) => new() { Header = header, Style = TryStyle("AppMenuItem") };

    private Style? TryStyle(string key) => TryFindResource(key) as Style;

    /// <summary>Галочка выбранного варианта (у пунктов меню приложения нет своей отметки).</summary>
    private TextBlock? CheckIcon(bool on) => on
        ? new TextBlock
        {
            Text = "\uE73E",
            FontFamily = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Foreground = TryFindResource("Accent") as Brush ?? Brushes.SlateBlue,
            VerticalAlignment = VerticalAlignment.Center
        }
        : null;

    /// <summary>Квадратик для выбора нескольких: пустой или отмеченный, как галочки в окнах приложения.</summary>
    private Border CheckBoxIcon(bool on) => new()
    {
        Width = 16,
        Height = 16,
        CornerRadius = new CornerRadius(5),
        BorderThickness = new Thickness(on ? 0 : 1.5),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xDE)),
        Background = on ? TryFindResource("AccentGradient") as Brush ?? Brushes.SlateBlue : Brushes.Transparent,
        VerticalAlignment = VerticalAlignment.Center,
        Child = on
            ? new TextBlock
            {
                Text = "\uE73E",
                FontFamily = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
            : null
    };

    private static void Open(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- развернуть на весь экран

    /// <summary>
    /// Развернуть на рабочую область экрана, где стоит окно (без тени и скруглений, как развернутое окно Windows), или вернуть
    /// прежние размер и место. Список строк забирает всю добавленную высоту.
    /// </summary>
    private void ToggleExpanded()
    {
        if (!_expanded)
        {
            var area = CurrentWorkArea();
            var chrome = Card.ActualHeight - ListHost.ActualHeight;
            _normal = (Left, Top, Card.Width, ListHost.Height);
            _cardShadow = Card.Effect;
            Card.Effect = null;
            Card.Margin = new Thickness(0);
            Card.CornerRadius = new CornerRadius(0);
            Card.Width = area.Width;
            ListHost.Height = Math.Max(220, area.Height - chrome);
            Left = area.Left;
            Top = area.Top;
            _expanded = true;
        }
        else
        {
            Card.Effect = _cardShadow;
            Card.Margin = new Thickness(36);
            Card.CornerRadius = new CornerRadius(24);
            Card.Width = _normal.CardWidth;
            ListHost.Height = _normal.ListHeight;
            Left = _normal.Left;
            Top = _normal.Top;
            _expanded = false;
        }
        ExpandGlyph.Text = _expanded ? "\uE73F" : "\uE740";
        ExpandButton.ToolTip = _expanded ? "Вернуть прежний размер (F11)" : "Развернуть на весь экран (F11)";
    }

    /// <summary>Рабочая область (без панели задач) того монитора, на котором окно; не вышло — основного.</summary>
    private Rect CurrentWorkArea()
    {
        try
        {
            var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) && PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                var fromDevice = target.TransformFromDevice;
                return new Rect(fromDevice.Transform(new Point(info.Work.Left, info.Work.Top)),
                    fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom)));
            }
        }
        catch (Exception)
        {
            // ниже — основной экран
        }
        return SystemParameters.WorkArea;
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    // ---------------------------------------------------------------- действия

    private void ShowMarkPanel(bool show)
    {
        MarkButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        MarkPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            MarkText.Text = string.Empty;
            MarkText.Focus();
        }
    }

    private void PlaceMark()
    {
        _logs.Mark(string.IsNullOrWhiteSpace(MarkText.Text) ? null : MarkText.Text.Trim());
        ShowMarkPanel(false);
    }

    /// <summary>Сохранить показанное в .log (формат файла, с тегами; первой строкой — чьи теги).</summary>
    private void ExportView()
    {
        var name = SelectedSource switch
        {
            LogSources.Launcher => "лаунчер",
            LogSources.Service => "служба",
            LogSources.Core => "ядро",
            _ => "все"
        };
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить журнал",
            Filter = "Журнал (*.log)|*.log|Текстовые файлы (*.txt)|*.txt",
            FileName = $"{name}-{DateTime.Now:yyyyMMdd-HHmm}.log",
            InitialDirectory = ExportWindow.DownloadsFolder()
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var lines = _rows.Select(r => r.Line).ToList();
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(false));
            var tags = TechLogFilter.TagsComment(lines, _names);
            if (tags.Length > 0)
                writer.Write(tags + "\n");
            foreach (var line in lines)
            {
                writer.Write(line.Format());
                writer.Write('\n');
            }
        }
        catch (Exception ex)
        {
            AlertWindow.ShowMessage(this, AlertKind.Error, "Не удалось сохранить файл", ex.Message);
        }
    }

    private void CopySelected()
    {
        var lines = LinesList.SelectedItems.OfType<LineRow>().OrderBy(r => _rows.IndexOf(r)).Select(r => r.Line.Format()).ToList();
        if (lines.Count == 0)
            return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // буфер обмена занят другой программой
        }
    }

    private bool AtBottom() => _scroll is null || _scroll.VerticalOffset >= _scroll.ScrollableHeight - 2;

    private void ScrollToEnd()
    {
        _pendingNew = 0;
        NewLinesButton.Visibility = Visibility.Collapsed;
        if (_rows.Count > 0)
            LinesList.ScrollIntoView(_rows[^1]);
    }

    private static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            if (FindChild<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    /// <summary>Строка для показа: местное время, источник по-русски, теги именами.</summary>
    private sealed class LineRow
    {
        private static Brush? _danger, _warn, _muted, _primary, _secondary;

        public LineRow(LogLine line, IReadOnlyDictionary<string, string> names)
        {
            Line = line;
            var local = line.TimeUtc.ToLocalTime();
            Time = local.Date == DateTime.Today
                ? local.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                : local.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture);
            Source = LogSources.DisplayName(line.Source);
            Level = LogLine.LevelName(line.Level);
            var text = TechLogFilter.WithNames(line.Text, names);
            // Строка ядра «[3025028384 219ms] текст»: номер соединения — в колонку типа, длительность — в свою колонку,
            // иначе текст начинается с разной позиции (разная длина номера и времени).
            if (line.Source == LogSources.Core && CoreConnection.Match(text) is { Success: true } m)
            {
                Area = m.Groups[1].Value;
                Duration = m.Groups[2].Value;
                text = m.Groups[3].Value;
            }
            else
            {
                Area = line.Area;
            }
            Text = text;
            IsMarker = line.Code == LogCodes.Marker;
        }

        private static readonly System.Text.RegularExpressions.Regex CoreConnection =
            new(@"^\[(\d+) ([^\]\s]+)\] (.*)$", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        public LogLine Line { get; }
        public string Time { get; }
        public string Source { get; }
        public string Level { get; }
        /// <summary>Область, у строк ядра — номер соединения.</summary>
        public string Area { get; }
        public string Duration { get; } = string.Empty;
        public string Code => Line.Code + (Line.Cid == LogLine.None ? string.Empty : " · " + Line.Cid);
        public string Text { get; }
        public bool IsMarker { get; }

        public Brush LevelBrush => Line.Level switch
        {
            LogLevel.Error => _danger ??= Find("Danger"),
            LogLevel.Warn => _warn ??= Find("Warn"),
            LogLevel.Debug => _muted ??= Find("TextMuted"),
            _ => _secondary ??= Find("TextSecondary")
        };

        public Brush TextBrush => Line.Level switch
        {
            LogLevel.Error => _danger ??= Find("Danger"),
            LogLevel.Debug => _muted ??= Find("TextMuted"),
            _ => _primary ??= Find("TextPrimary")
        };

        private static Brush Find(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Black;
    }
}
