using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.Views;

/// <summary>Чем закончилась работа в окне с прогрессом.</summary>
public sealed record ProgressOutcome(bool Completed, bool Cancelled, Exception? Error);

/// <summary>Модальное окно-лоадер: выполняет загрузку, показывает прогресс и даёт её отменить.</summary>
public partial class ProgressWindow : Window
{
    private readonly Func<IProgress<DownloadProgress>, CancellationToken, Task> _work;
    private readonly CancellationTokenSource _cancel = new();
    private ProgressOutcome? _outcome;
    private double _fraction;
    private bool _indeterminate;

    private ProgressWindow(string title, string subtitle, Func<IProgress<DownloadProgress>, CancellationToken, Task> work)
    {
        InitializeComponent();
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        _work = work;

        CancelButton.Click += (_, _) => RequestCancel();
        Loaded += async (_, _) => await RunAsync();
        Track.SizeChanged += (_, _) => UpdateFill();

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };

        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        SpinnerTurn.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
    }

    public static ProgressOutcome Run(Window? owner, string title, string subtitle,
        Func<IProgress<DownloadProgress>, CancellationToken, Task> work)
    {
        var window = new ProgressWindow(title, subtitle, work);
        if (owner is { IsLoaded: true, IsVisible: true })
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
        return window._outcome ?? new ProgressOutcome(false, true, null);
    }

    private async Task RunAsync()
    {
        // Progress<T> возвращает отчёты в поток окна.
        var progress = new Progress<DownloadProgress>(Report);
        try
        {
            await _work(progress, _cancel.Token);
            _outcome = new ProgressOutcome(true, false, null);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            _outcome = new ProgressOutcome(false, true, null);
        }
        catch (Exception ex)
        {
            _outcome = new ProgressOutcome(false, false, ex);
        }

        DialogResult = _outcome.Completed;
    }

    private void Report(DownloadProgress p)
    {
        if (p.Total is long total && total > 0)
        {
            SetIndeterminate(false);
            _fraction = Math.Clamp((double)p.Received / total, 0, 1);
            ProgressText.Text = $"{Megabytes(p.Received)} из {Megabytes(total)} МБ · {_fraction:P0}";
        }
        else
        {
            SetIndeterminate(true);
            ProgressText.Text = $"{Megabytes(p.Received)} МБ";
        }

        UpdateFill();
    }

    private void UpdateFill()
    {
        Fill.Width = _indeterminate ? Track.ActualWidth * 0.3 : Track.ActualWidth * _fraction;
    }

    /// <summary>Размер файла неизвестен: по полосе бегает отрезок.</summary>
    private void SetIndeterminate(bool value)
    {
        if (_indeterminate == value) return;
        _indeterminate = value;

        if (value)
        {
            var run = new DoubleAnimation(-Track.ActualWidth * 0.3, Track.ActualWidth, TimeSpan.FromSeconds(1.2))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            FillShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, run);
        }
        else
        {
            FillShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            FillShift.X = 0;
        }
    }

    private static string Megabytes(long bytes) => (bytes / 1024d / 1024d).ToString("0.0");

    private void RequestCancel()
    {
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Отмена…";
        _cancel.Cancel();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Окно закрывается только когда работа завершилась; Alt+F4 и Esc означают «Отмена».
        if (_outcome is null)
        {
            e.Cancel = true;
            if (!_cancel.IsCancellationRequested)
                RequestCancel();
        }

        base.OnClosing(e);
    }
}
