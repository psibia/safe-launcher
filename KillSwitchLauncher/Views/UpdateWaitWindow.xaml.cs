using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KillSwitchLauncher.Services;
using SafeLauncher.Updates;

namespace KillSwitchLauncher.Views;

/// <summary>
/// Окно ожидания обновления (KillSwitchLauncher.exe --await-update ВЕРСИЯ "путь к лаунчеру", копия из %TEMP%).
/// Лаунчер запускает его перед тем, как закрыться ради обновления. Окно читает ход установки из status.json
/// (<see cref="UpdateState"/>) и показывает шаги. Когда новая версия установлена, окно запускает её, а помощник службы ждёт,
/// пока она сообщит, что запустилась; не сообщит — помощник вернёт прежнюю версию, и окно запустит уже её (о причине
/// сообщит сам лаунчер). Закрывается, когда новая версия запустилась (или появилось окно лаунчера, если помощник
/// прежней версии не ждёт запуска).
/// Технический журнал здесь не ведётся: в него пишет лаунчер.
/// </summary>
public partial class UpdateWaitWindow : Window
{
    /// <summary>Сколько ждать окна лаунчера, прежде чем сказать, что он запускается дольше обычного.</summary>
    private static readonly TimeSpan SlowLaunch = TimeSpan.FromSeconds(45);

    private readonly UpdateWaitModel _model;
    private readonly string _launcherPath;
    private readonly string _statusPath;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly DispatcherTimer _timer;
    private UpdateWaitOutcome _outcome = UpdateWaitOutcome.Running;
    private Process? _launcher;
    private DateTime _launchedUtc;

    /// <summary>Окно можно закрыть: установка закончилась или прервалась. Пока она идёт, закрыть нельзя: некому будет запустить лаунчер.</summary>
    private bool _canClose;

    public UpdateWaitWindow(string version, string launcherPath, string statusPath)
    {
        InitializeComponent();
        _model = new UpdateWaitModel(version, _startedUtc);
        _launcherPath = launcherPath;
        _statusPath = statusPath;
        TitleText.Text = _model.Title;
        MessageText.Text = "Ждём, пока закроется лаунчер";
        _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => OnTick();
        Loaded += (_, _) =>
        {
            OnTick();
            _timer.Start();
        };
    }

    /// <summary>Окно по аргументам командной строки; null — аргументы не те.</summary>
    public static UpdateWaitWindow? FromArguments(string[] args)
    {
        if (args.Length < 3 || args[0] != UpdateService.WaiterArgument || !Version.TryParse(args[1], out _) || !Path.IsPathFullyQualified(args[2]))
            return null;
        return new UpdateWaitWindow(args[1], args[2], UpdateState.FilePath);
    }

    /// <summary>Windows завершает сеанс: окно закрывается без вопросов.</summary>
    public void AllowClose() => _canClose = true;

    private void OnTick()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось следить за установкой", ex.Message + ". Запустите лаунчер, когда установка закончится.");
        }
    }

    private void Tick()
    {
        ElapsedText.Text = "Прошло " + Elapsed(DateTime.UtcNow - _startedUtc);

        // Пока помощник работает (и пока ждёт, что новая версия запустится), следим за ним; потом только за лаунчером.
        if (_outcome is UpdateWaitOutcome.Running or UpdateWaitOutcome.Starting)
        {
            var view = _model.Evaluate(UpdateState.Read(_statusPath), DateTime.UtcNow);
            TitleText.Text = view.Title;
            MessageText.Text = view.Message;
            StepList.ItemsSource = view.Steps;
            var previous = _outcome;
            _outcome = view.Outcome;
            switch (view.Outcome)
            {
                case UpdateWaitOutcome.Starting:
                    // Помощник ждёт, пока новая версия запустится и сообщит об этом: запускаем её, итог скажет помощник.
                    if (previous != UpdateWaitOutcome.Starting)
                        Launch();
                    return;
                case UpdateWaitOutcome.Installed:
                    if (previous == UpdateWaitOutcome.Starting && _launcher is not null)
                    {
                        CloseNow(); // новая версия запустилась и сообщила об этом: её окно уже открыто
                        return;
                    }

                    // Помощник прежней версии не ждёт запуска: лаунчер запускаем сейчас.
                    if (_launcher is null)
                        Launch();
                    break;
                case UpdateWaitOutcome.Failed:
                    // Прежняя версия на месте (новую, если она запускалась, помощник закрыл): запускаем её, о причине она сообщит сама.
                    SetLook(failed: true);
                    ForgetLauncher();
                    Launch();
                    break;
                case UpdateWaitOutcome.Lost:
                    ShowProblem(view.Title, view.Message);
                    return;
                default:
                    return;
            }
        }

        if (_launcher is not null)
            WatchLauncher();
    }

    private void ForgetLauncher()
    {
        _launcher?.Dispose();
        _launcher = null;
    }

    private void Launch()
    {
        Buttons.Visibility = Visibility.Collapsed;
        Progress.Visibility = Visibility.Visible;
        HintText.Text = "Окно закроется само, когда откроется лаунчер";
        try
        {
            _launcher = Process.Start(new ProcessStartInfo(_launcherPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(_launcherPath) ?? string.Empty
            }) ?? throw new InvalidOperationException("Windows не запустила программу.");
            _launchedUtc = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            ShowProblem("Лаунчер не запустился", $"{ex.Message}. Если он не запускается и из меню «Пуск», установите его заново.");
        }
    }

    /// <summary>Лаунчер запущен: ждём его окна. Если он сразу закрылся с кодом 0, уже работал другой экземпляр и показал своё окно.</summary>
    private void WatchLauncher()
    {
        var launcher = _launcher!;
        launcher.Refresh();
        if (launcher.HasExited)
        {
            var code = launcher.ExitCode;
            launcher.Dispose();
            _launcher = null;
            if (code == 0)
            {
                CloseNow();
                return;
            }

            ShowProblem("Лаунчер не запустился", $"Он закрылся с кодом {code}. Попробуйте запустить его ещё раз. Если не выйдет, установите его заново.");
            return;
        }

        if (launcher.MainWindowHandle != IntPtr.Zero)
        {
            CloseNow();
            return;
        }

        if (DateTime.UtcNow - _launchedUtc > SlowLaunch && !_canClose)
        {
            // Окна всё нет: не держим пользователя, лаунчер может открыться и позже.
            _canClose = true;
            HintText.Text = "Лаунчер запускается дольше обычного. Окно можно закрыть: лаунчер откроется сам";
            Buttons.Visibility = Visibility.Visible;
            LaunchButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowProblem(string title, string message)
    {
        _outcome = UpdateWaitOutcome.Lost;
        ForgetLauncher();
        _canClose = true;
        TitleText.Text = title;
        MessageText.Text = message;
        SetLook(failed: true);
        Progress.Visibility = Visibility.Collapsed;
        HintText.Text = string.Empty;
        Buttons.Visibility = Visibility.Visible;
        LaunchButton.Visibility = Visibility.Visible;
    }

    private void SetLook(bool failed)
    {
        Spinner.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        IconCircle.Fill = (Brush)FindResource(failed ? "DangerSoft" : "AccentSoft");
        IconGlyph.Glyph = failed ? "" : "";
        IconGlyph.Foreground = (Brush)FindResource(failed ? "Danger" : "Accent");
    }

    private static string Elapsed(TimeSpan time) =>
        time.TotalMinutes < 1 ? $"{(int)time.TotalSeconds} с" : $"{(int)time.TotalMinutes} мин {time.Seconds:00} с";

    private void CloseNow()
    {
        _canClose = true;
        _timer.Stop();
        Close();
    }

    private void OnLaunchClick(object sender, RoutedEventArgs e) => Launch();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseNow();

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_canClose)
            e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _launcher?.Dispose();
        base.OnClosed(e);
    }
}
