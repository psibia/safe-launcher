using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.Views;

/// <summary>Окно журнала событий. Пока оно открыто, новые события появляются сверху сразу.</summary>
public partial class ActivityLogWindow : Window
{
    private readonly ActivityLog _log;
    private readonly ObservableCollection<ActivityRow> _rows;

    private ActivityLogWindow(ActivityLog log)
    {
        InitializeComponent();
        _log = log;
        _rows = new ObservableCollection<ActivityRow>(log.Snapshot().Reverse().Select(e => new ActivityRow(e)));
        EntriesList.ItemsSource = _rows;
        UpdateSummary();

        _log.Added += OnAdded;
        _log.Cleared += OnCleared;
        Closed += (_, _) =>
        {
            _log.Added -= OnAdded;
            _log.Cleared -= OnCleared;
        };

        OpenFileButton.Click += (_, _) => OpenFile();
        ClearButton.Click += (_, _) => ClearLog();
        CloseButton.Click += (_, _) => Close();

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
    }

    public static void Show(Window? owner, ActivityLog log)
    {
        var window = new ActivityLogWindow(log);
        if (owner is { IsLoaded: true, IsVisible: true })
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
    }

    private void OnAdded(object? sender, ActivityEntry entry) => Dispatcher.BeginInvoke(() =>
    {
        _rows.Insert(0, new ActivityRow(entry));
        while (_rows.Count > ActivityLog.Keep)
            _rows.RemoveAt(_rows.Count - 1);
        UpdateSummary();
    });

    private void OnCleared(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _rows.Clear();
        UpdateSummary();
    });

    private void UpdateSummary()
    {
        SubtitleText.Text = _rows.Count == 0
            ? "Обрывы VPN, запуски и закрытия приложений"
            : $"Последние события: {_rows.Count} из {ActivityLog.Keep} хранимых";
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = _rows.Count > 0;
    }

    private void ClearLog()
    {
        if (AlertWindow.Confirm(this, "Очистить журнал?", "Все записи будут удалены без возможности восстановления.", "Очистить", destructive: true))
            _log.Clear();
    }

    private void OpenFile()
    {
        try
        {
            if (!File.Exists(_log.FilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_log.FilePath)!);
                File.WriteAllText(_log.FilePath, string.Empty);
            }

            Process.Start(new ProcessStartInfo(_log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AlertWindow.ShowMessage(this, AlertKind.Error, "Не удалось открыть файл", ex.Message);
        }
    }

    /// <summary>Строка журнала: значок и цвет по типу события, время коротко.</summary>
    private sealed class ActivityRow
    {
        public ActivityRow(ActivityEntry entry)
        {
            Text = entry.Text;
            TimeText = FormatTime(entry.Time, DateTime.Today);

            var (glyph, brush, soft) = entry.Kind switch
            {
                LogKind.VpnUp => ("", "Success", "SuccessSoft"),
                LogKind.VpnDown => ("", "Danger", "DangerSoft"),
                LogKind.Launch => ("", "Accent", "AccentSoft"),
                LogKind.Closed => ("", "Warn", "WarnSoft"),
                LogKind.Failed => ("", "Danger", "DangerSoft"),
                LogKind.Blocked => ("", "Danger", "DangerSoft"),
                LogKind.Update => ("", "Accent", "AccentSoft"),
                _ => ("", "TextSecondary", "SubtleBg")
            };

            Glyph = glyph;
            Brush = (Brush)Application.Current.FindResource(brush);
            SoftBrush = (Brush)Application.Current.FindResource(soft);
        }

        public string Glyph { get; }
        public Brush Brush { get; }
        public Brush SoftBrush { get; }
        public string Text { get; }
        public string TimeText { get; }
    }

    /// <summary>Время записи коротко: сегодня — «14:05:09», вчера — «вчера 23:10», раньше — «07.10 14:03».</summary>
    internal static string FormatTime(DateTime time, DateTime today)
    {
        if (time.Date == today.Date)
            return time.ToString("HH:mm:ss");
        if (time.Date == today.Date.AddDays(-1))
            return "вчера " + time.ToString("HH:mm");
        return time.ToString("dd.MM HH:mm");
    }
}
