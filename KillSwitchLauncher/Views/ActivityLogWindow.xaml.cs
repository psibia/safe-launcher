using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Text;
using System.Windows.Media;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.ViewModels;
using Microsoft.Win32;

namespace KillSwitchLauncher.Views;

/// <summary>Окно журнала событий. Пока оно открыто, новые события появляются сверху сразу.</summary>
public partial class ActivityLogWindow : Window
{
    private readonly ActivityLog _log;
    private readonly ILogActions _logs;
    private readonly ObservableCollection<ActivityRow> _rows;

    /// <summary>Что открыть после закрытия окна (технический журнал, выгрузка): окна не громоздятся друг на друга.</summary>
    private Action? _after;

    private ActivityLogWindow(ActivityLog log, ILogActions logs)
    {
        InitializeComponent();
        _log = log;
        _logs = logs;
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

        ExportButton.Click += (_, _) => Export();
        TechLogButton.Click += (_, _) => CloseThen(_logs.ShowTechLog);
        BannerExportButton.Click += (_, _) => CloseThen(() => _logs.ShowExport(null));
        ClearButton.Click += (_, _) => ClearLog();
        CloseButton.Click += (_, _) => Close();

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
    }

    public static void Show(Window? owner, ActivityLog log, ILogActions logs)
    {
        var window = new ActivityLogWindow(log, logs);
        if (owner is { IsLoaded: true, IsVisible: true })
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
        // Следующее окно — когда это закрыто и главное окно снова не затемнено.
        if (window._after is { } after)
            window.Dispatcher.BeginInvoke(after);
    }

    private void CloseThen(Action action)
    {
        _after = action;
        Close();
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
        SubtitleText.Text = (_rows.Count == 0
            ? "Обрывы VPN, запуски и закрытия приложений"
            : $"Последние события: {_rows.Count} из {ActivityLog.Keep} хранимых") + (_log.Persist ? string.Empty : ", до закрытия лаунчера");
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = _rows.Count > 0;
        ExportButton.IsEnabled = _rows.Count > 0;
        ErrorsBanner.Visibility = HasRecentErrors(_log.Snapshot(), DateTime.Now) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Есть ли за последний час ошибки (не запустилось, закрыто без VPN, обрыв): тогда предлагаем выгрузку.</summary>
    internal static bool HasRecentErrors(System.Collections.Generic.IEnumerable<ActivityEntry> entries, DateTime now) =>
        entries.Any(e => e.Time >= now.AddHours(-1) && e.Kind is LogKind.Failed or LogKind.Blocked or LogKind.VpnDown);

    private void ClearLog()
    {
        if (AlertWindow.Confirm(this, "Очистить журнал?", "Все записи будут удалены без возможности восстановления.", "Очистить", destructive: true))
            _log.Clear();
    }

    /// <summary>Сохранить журнал событий в .txt (по времени, старые сверху).</summary>
    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить журнал событий",
            Filter = "Текстовые файлы (*.txt)|*.txt",
            FileName = $"events-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            InitialDirectory = ExportWindow.DownloadsFolder()
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var text = new StringBuilder();
            foreach (var entry in _log.Snapshot())
                text.Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss")).Append('\t').Append(entry.Kind).Append('\t').Append(entry.Text).Append("\r\n");
            File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            AlertWindow.ShowMessage(this, AlertKind.Error, "Не удалось сохранить файл", ex.Message);
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
