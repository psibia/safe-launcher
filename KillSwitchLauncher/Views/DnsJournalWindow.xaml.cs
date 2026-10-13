using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.ViewModels;

namespace KillSwitchLauncher.Views;

/// <summary>
/// Окно журнала DNS одного приложения. Не модальное: можно держать открытым и пользоваться лаунчером.
/// Новые запросы забираются из журнала пачкой раз в 200 мс, поэтому окно не тормозит, даже когда
/// приложение делает сотни запросов в минуту.
/// </summary>
public partial class DnsJournalWindow : Window
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);
    private static readonly Dictionary<Guid, DnsJournalWindow> OpenWindows = new();

    private readonly AppTileViewModel _tile;
    private readonly DnsJournal _journal;
    private readonly ObservableCollection<DnsQueryRow> _queries = new();
    private readonly ObservableCollection<DnsDomainRow> _domains = new();
    private readonly Dictionary<string, DnsDomainRow> _domainIndex = new(StringComparer.Ordinal);
    private readonly ICollectionView _queriesView;
    private readonly ICollectionView _domainsView;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _searchDelay;
    private readonly DispatcherTimer _copiedReset;
    private long _lastSeq;
    private string _filter = string.Empty;
    private ScrollViewer? _scroll;

    private DnsJournalWindow(AppTileViewModel tile, DnsJournal journal)
    {
        InitializeComponent();
        _tile = tile;
        _journal = journal;

        _queriesView = CollectionViewSource.GetDefaultView(_queries);
        _queriesView.Filter = o => o is DnsQueryRow row && (_filter.Length == 0 ? true : row.IsQuery && row.Matches(_filter));
        _domainsView = CollectionViewSource.GetDefaultView(_domains);
        _domainsView.Filter = o => o is DnsDomainRow row && (_filter.Length == 0 || row.Matches(_filter));

        RowsList.ItemsSource = _queriesView;
        Reload();

        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _refresh.Tick += (_, _) => PullNew();
        _refresh.Start();

        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            ApplyFilter();
        };
        SearchBox.TextChanged += (_, _) =>
        {
            _searchDelay.Stop();
            _searchDelay.Start();
        };

        _copiedReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _copiedReset.Tick += (_, _) =>
        {
            _copiedReset.Stop();
            CopyText.Text = "Копировать";
        };

        UniqueToggle.Checked += (_, _) => SwitchMode();
        UniqueToggle.Unchecked += (_, _) => SwitchMode();
        CopyButton.Click += (_, _) => CopySelected();
        RowsList.SelectionChanged += (_, _) => UpdateCopyButton();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelected(), (_, e) => e.CanExecute = HasSelectedDomains()));
        CloseButton.Click += (_, _) => Close();
        CloseCornerButton.Click += (_, _) => Close();

        _tile.PropertyChanged += OnTileChanged;
        Loaded += (_, _) =>
        {
            _scroll = FindChild<ScrollViewer>(RowsList);
            ScrollToEnd();
        };
        Closed += (_, _) =>
        {
            _refresh.Stop();
            _tile.PropertyChanged -= OnTileChanged;
            OpenWindows.Remove(_tile.Entry.Id);
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };

        UpdateHeader();
        UpdateCopyButton();
    }

    /// <summary>Открывает журнал приложения; если он уже открыт — выводит окно на передний план.</summary>
    public static void ShowFor(Window? owner, AppTileViewModel tile, DnsJournal journal)
    {
        if (OpenWindows.TryGetValue(tile.Entry.Id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new DnsJournalWindow(tile, journal);
        OpenWindows[tile.Entry.Id] = window;
        window.PlaceOver(owner);
        window.Show();
        window.Activate();
    }

    /// <summary>Закрывает окно журнала приложения, если оно открыто (приложение убрали из лаунчера).</summary>
    public static void CloseFor(Guid appId)
    {
        if (OpenWindows.TryGetValue(appId, out var window))
            window.Close();
    }

    /// <summary>По центру главного окна, если оно видно, иначе по центру экрана.</summary>
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

    // ---------------------------------------------------------------- данные

    /// <summary>Перечитать журнал целиком (при открытии или если окно отстало больше, чем на весь буфер).</summary>
    private void Reload()
    {
        var snapshot = _journal.Snapshot();
        _queries.Clear();
        foreach (var entry in snapshot.Entries)
            _queries.Add(new DnsQueryRow(entry));

        _domains.Clear();
        _domainIndex.Clear();
        foreach (var summary in snapshot.Domains)
        {
            var row = new DnsDomainRow(summary);
            _domains.Add(row);
            _domainIndex[row.Domain] = row;
        }

        _lastSeq = snapshot.LastSeq;
        UpdateHeader();
    }

    private void PullNew()
    {
        var read = _journal.ReadSince(_lastSeq);
        if (read.Reset)
        {
            Reload();
            ScrollToEnd();
            return;
        }

        if (read.Entries.Count == 0)
        {
            UpdateHeader(); // состояние записи могло поменяться
            return;
        }

        var follow = IsAtEnd();
        foreach (var entry in read.Entries)
        {
            _queries.Add(new DnsQueryRow(entry));
            _lastSeq = entry.Seq;
            if (entry.IsMarker)
                continue;

            if (_domainIndex.TryGetValue(entry.Domain, out var domain))
            {
                domain.Add(entry);
            }
            else
            {
                domain = new DnsDomainRow(entry);
                _domains.Add(domain);
                _domainIndex[domain.Domain] = domain;
            }
        }

        // Список хранит столько же, сколько журнал: старые строки уходят сверху.
        while (_queries.Count > _journal.Capacity)
            _queries.RemoveAt(0);

        UpdateHeader();
        if (follow)
            ScrollToEnd();
    }

    private void OnTileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppTileViewModel.IsDnsTracking) or nameof(AppTileViewModel.IsDnsActive)
            or nameof(AppTileViewModel.IsRunning) or nameof(AppTileViewModel.RunningCount) or nameof(AppTileViewModel.Name))
            UpdateHeader();
    }

    private void UpdateHeader()
    {
        Title = $"Журнал DNS — {_tile.Name}";
        TitleText.Text = Title;

        string state;
        var recording = _tile.IsDnsTracking && _tile.IsDnsActive;
        if (!_tile.IsDnsTracking)
            state = "Журнал выключен";
        else if (!_tile.IsDnsActive)
            state = "Запись начнётся при запуске приложения из лаунчера";
        else
            state = _tile.IsRunning ? "Идёт запись" : "Идёт запись · приложение не запущено";

        StateText.Text = state;
        StateDot.Fill = (Brush)FindResource(recording ? "Success" : "TextMuted");
        CountsText.Text = $"{Plural(_journal.TotalQueries, "запрос", "запроса", "запросов")} · " +
                          $"{Plural(_journal.UniqueDomains, "домен", "домена", "доменов")}";

        var unique = UniqueToggle.IsChecked == true;
        var visible = unique ? _domainsView.Cast<object>().Any() : _queriesView.Cast<object>().Any();
        EmptyText.Text = _filter.Length > 0 ? "Ничего не найдено"
            : _tile.IsDnsTracking ? "Пока запросов нет. Запустите приложение из лаунчера, и его запросы появятся здесь."
            : "Журнал пуст";
        EmptyText.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- поиск и режим

    private void ApplyFilter()
    {
        _filter = SearchBox.Text.Trim();
        _queriesView.Refresh();
        _domainsView.Refresh();
        UpdateHeader();
    }

    private void SwitchMode()
    {
        var unique = UniqueToggle.IsChecked == true;
        RowsList.ItemsSource = unique ? _domainsView : _queriesView;
        QueriesHeader.Visibility = unique ? Visibility.Collapsed : Visibility.Visible;
        DomainsHeader.Visibility = unique ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeader();
        UpdateCopyButton();
        if (!unique)
            ScrollToEnd();
    }

    // ---------------------------------------------------------------- копирование

    /// <summary>Домены выделенных строк, без повторов и без отметок.</summary>
    private List<string> SelectedDomains() =>
        RowsList.SelectedItems.Cast<object>()
            .Select(o => o switch
            {
                DnsQueryRow { IsQuery: true } q => q.Domain,
                DnsDomainRow d => d.Domain,
                _ => null
            })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private bool HasSelectedDomains() => SelectedDomains().Count > 0;

    private void UpdateCopyButton() => CopyButton.IsEnabled = HasSelectedDomains();

    private void CopySelected()
    {
        var domains = SelectedDomains();
        if (domains.Count == 0)
            return;

        // Порядок строк как в списке, а не как их выделяли.
        var order = RowsList.Items.Cast<object>().Select((item, index) => (item, index)).ToDictionary(x => x.item, x => x.index);
        var ordered = RowsList.SelectedItems.Cast<object>()
            .OrderBy(o => order.TryGetValue(o, out var i) ? i : int.MaxValue)
            .Select(o => o switch { DnsQueryRow { IsQuery: true } q => q.Domain, DnsDomainRow d => d.Domain, _ => null })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

        if (TrySetClipboard(string.Join(Environment.NewLine, ordered)))
        {
            CopyText.Text = domains.Count == 1 ? "Скопировано" : $"Скопировано: {domains.Count}";
            _copiedReset.Stop();
            _copiedReset.Start();
        }
        else
        {
            AlertWindow.ShowMessage(this, AlertKind.Error, "Не удалось скопировать",
                "Буфер обмена занят другой программой. Попробуйте ещё раз.");
        }
    }

    /// <summary>Буфер обмена может быть на мгновение занят другой программой (CLIPBRD_E_CANT_OPEN): пробуем несколько раз.</summary>
    private static bool TrySetClipboard(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(60);
            }
            catch (ExternalException)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- прокрутка

    private bool IsAtEnd() =>
        UniqueToggle.IsChecked != true && (_scroll is null || _scroll.VerticalOffset >= _scroll.ScrollableHeight - 2);

    private void ScrollToEnd()
    {
        if (UniqueToggle.IsChecked == true)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _scroll?.ScrollToEnd()));
    }

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

    /// <summary>«1 запрос», «3 запроса», «12 запросов».</summary>
    internal static string Plural(long count, string one, string few, string many)
    {
        var mod100 = count % 100;
        var mod10 = count % 10;
        var word = mod100 is >= 11 and <= 14 ? many
            : mod10 == 1 ? one
            : mod10 is >= 2 and <= 4 ? few
            : many;
        return $"{count} {word}";
    }
}

/// <summary>Строка режима «все запросы»: запрос или отметка («Новый запуск»).</summary>
public sealed class DnsQueryRow
{
    public DnsQueryRow(DnsEntry entry)
    {
        IsMarker = entry.IsMarker;
        Domain = entry.Domain;
        Display = entry.IsMarker ? entry.Domain : DnsNames.ToDisplay(entry.Domain);
        TimeText = entry.Time.ToString("HH:mm:ss");
        TypeText = entry.TypeText;
        ProcessText = entry.IsMarker ? string.Empty : $"{entry.ProcessName} · {entry.ProcessId}";
        MarkerText = entry.IsMarker ? $"{entry.Domain} · {ActivityLogWindow.FormatTime(entry.Time, DateTime.Today)}" : string.Empty;
    }

    public bool IsMarker { get; }
    public bool IsQuery => !IsMarker;

    /// <summary>Домен как в DNS (для копирования); у отметки — её текст.</summary>
    public string Domain { get; }

    /// <summary>Домен для показа: национальные домены — буквами, а не xn--.</summary>
    public string Display { get; }

    public string TimeText { get; }
    public string TypeText { get; }
    public string ProcessText { get; }
    public string MarkerText { get; }

    public bool Matches(string filter) =>
        Domain.Contains(filter, StringComparison.OrdinalIgnoreCase) || Display.Contains(filter, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Строка режима «только уникальные»: домен, его типы и число запросов.</summary>
public sealed class DnsDomainRow : ObservableObject
{
    private readonly List<ushort> _types;
    private int _count;
    private DateTime _lastSeen;

    public DnsDomainRow(DnsDomainSummary summary)
    {
        Domain = summary.Domain;
        Display = DnsNames.ToDisplay(summary.Domain);
        _types = summary.Types.ToList();
        _count = summary.Count;
        _lastSeen = summary.LastSeen;
    }

    public DnsDomainRow(DnsEntry entry)
    {
        Domain = entry.Domain;
        Display = DnsNames.ToDisplay(entry.Domain);
        _types = new List<ushort> { entry.Type };
        _count = 1;
        _lastSeen = entry.Time;
    }

    public string Domain { get; }
    public string Display { get; }

    public int Count => _count;

    public string TypesText => string.Join(", ", _types.Select(DnsNames.TypeName));

    public string LastSeenText => _lastSeen.ToString("HH:mm:ss");

    public void Add(DnsEntry entry)
    {
        _count++;
        _lastSeen = entry.Time;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(LastSeenText));
        if (!_types.Contains(entry.Type))
        {
            _types.Add(entry.Type);
            OnPropertyChanged(nameof(TypesText));
        }
    }

    public bool Matches(string filter) =>
        Domain.Contains(filter, StringComparison.OrdinalIgnoreCase) || Display.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
