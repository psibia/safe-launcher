using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.ViewModels;

namespace KillSwitchLauncher;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(3.5);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _toastTimer;
    private readonly TrayIcon _tray;

    // Закрытие окна по-настоящему (меню трея, обновление, выход из Windows), а не уход в трей.
    private bool _exitRequested;
    private bool _sessionEnding;
    private bool _closing;
    private WindowState _restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        // У баннера свой DataContext (ToastInfo), он не должен наследовать ViewModel окна.
        ToastHost.DataContext = null;
        _toastTimer = new DispatcherTimer { Interval = ToastLifetime };
        _toastTimer.Tick += (_, _) => HideToast();

        _viewModel = new MainViewModel(new SettingsStorage(), new DialogService(), Dispatcher);
        _viewModel.ToastRequested += (_, toast) => ShowToast(toast);
        _viewModel.ShutdownRequested += (_, _) => ExitApp(); // обновление: установщик уже запущен
        DataContext = _viewModel;
        SourceInitialized += (_, _) => RoundCorners();

        _tray = new TrayIcon(BuildTrayToolTip());
        _tray.Update(BuildTrayToolTip(), alert: IsTrayAlert);
        _tray.OpenRequested += (_, _) => ShowFromTray();
        _tray.MenuRequested += (_, _) => ShowTrayMenu();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>Затемнение окна, пока открыт диалог (вызывается из DialogService).</summary>
    public void SetDimmed(bool dimmed) => DimOverlay.Visibility = dimmed ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- баннер-уведомление

    private void ShowToast(ToastInfo toast)
    {
        // Окно в трее или свёрнуто: баннер никто не увидит, поэтому показываем уведомление Windows.
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            _tray.ShowNotification(toast.Title, toast.Subtitle, warning: toast.Kind == ToastKind.Danger);
            return;
        }

        _toastTimer.Stop();
        ToastHost.DataContext = toast;
        ToastHost.Visibility = Visibility.Visible;

        // Быстро выезжает сверху и мягко останавливается.
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-40, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        });
        ToastHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));

        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) =>
        {
            // Если за это время пришёл новый баннер, он уже снова сделал Opacity = 1.
            if (!_toastTimer.IsEnabled)
                ToastHost.Visibility = Visibility.Collapsed;
        };
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-30, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        ToastHost.BeginAnimation(OpacityProperty, fade);
    }

    private void OnToastClicked(object sender, MouseButtonEventArgs e) => HideToast();

    // ---------------------------------------------------------------- окно

    /// <summary>Esc: закрыть сводку, а на странице настроек или приложения — вернуться к плиткам.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape)
            return;

        if (_viewModel.IsSummaryOpen)
            _viewModel.IsSummaryOpen = false;
        else if (_viewModel.IsSettingsOpen || _viewModel.IsAppPage)
            _viewModel.GoBack();
        else
            return;
        e.Handled = true;
    }

    // ---------------------------------------------------------------- перетаскивание программ в окно

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _viewModel.IsAppsPage && DroppedFiles(e) is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Перетащили .exe или ярлыки: одну программу — на её страницу, несколько — сразу в список.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!_viewModel.IsAppsPage || DroppedFiles(e) is not { Length: > 0 } files)
            return;
        e.Handled = true;
        Activate();
        // Окно с вопросами откроется уже после того, как Проводник закончит перетаскивание.
        Dispatcher.BeginInvoke(() => _viewModel.AddFromPaths(files));
    }

    private static string[]? DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    // ---------------------------------------------------------------- свой заголовок окна

    private void OnMinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnHideClicked(object sender, RoutedEventArgs e) => RequestHide();

    /// <summary>Кнопка «Закрыть», Alt+F4 или «Закрыть окно» на панели задач: в трей или выход — как выбрано в настройках или в вопросе.</summary>
    private void RequestHide()
    {
        switch (_viewModel.DecideHide())
        {
            case true:
                HideToTray();
                break;
            case false:
                ExitApp();
                break;
        }
    }

    private void HideToTray()
    {
        // Значка в трее нет (Проводник не запущен): прятать окно некуда, просто сворачиваем.
        if (!_tray.IsShown)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();
        if (_viewModel.TakeTrayHint())
            _tray.ShowNotification("Лаунчер работает в трее",
                "Он продолжает следить за VPN. Открыть — двойной клик по значку, закрыть — правый клик.", warning: false);
    }

    /// <summary>Запуск вместе с Windows: окно не показывается, лаунчер работает в трее. Значка нет — окно свёрнуто.</summary>
    public void StartInTray()
    {
        if (_tray.IsShown)
            return;
        WindowState = WindowState.Minimized;
        Show();
    }

    /// <summary>Показать окно из трея или из свёрнутого состояния и вывести на передний план.</summary>
    public void ShowFromTray()
    {
        if (_closing)
            return; // окно уже закрывается: показывать его снова нельзя

        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _restoreState;
        Activate();
    }

    /// <summary>Закрыть лаунчер по-настоящему. С запущенными приложениями сначала спросит, как и раньше.</summary>
    private void ExitApp()
    {
        // Вопрос о запущенных приложениях должен быть виден, поэтому окно сначала показывается.
        if (_viewModel.NeedsCloseConfirmation)
            ShowFromTray();

        _exitRequested = true;
        Close();
    }

    /// <summary>Выход из Windows или закрытие установщиком: закрываемся без вопросов и без трея.</summary>
    public void PrepareForExit()
    {
        _exitRequested = true;
        _sessionEnding = true;
    }

    // ---------------------------------------------------------------- трей

    private string BuildTrayToolTip() =>
        $"Безопасный лаунчер\n{_viewModel.SummaryTitle} · {_viewModel.SummarySubtitle}" +
        (_viewModel.IsKillSwitchOff ? "\nKill-Switch выключен" : string.Empty);

    /// <summary>Красный значок: есть приложения без защиты или ждущие сеть, либо системный VPN нужен и не подключён.</summary>
    private bool IsTrayAlert => _viewModel.SummaryTone is SummaryTone.Danger or SummaryTone.Warn;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SummaryTitle):
            case nameof(MainViewModel.SummarySubtitle):
            case nameof(MainViewModel.SummaryTone):
            case nameof(MainViewModel.IsKillSwitchOff):
                _tray.Update(BuildTrayToolTip(), alert: IsTrayAlert);
                break;
        }
    }

    /// <summary>
    /// Меню значка в трее: обычное меню Windows («Открыть» — по умолчанию, жирным). Запущенные приложения
    /// можно завершить прямо отсюда.
    /// </summary>
    private void ShowTrayMenu()
    {
        var items = new List<TrayMenuItem?> { new("Открыть", ShowFromTray, IsDefault: true) };

        var running = _viewModel.Apps.Where(a => a.IsRunning).ToList();
        if (running.Count > 0)
        {
            items.Add(null);
            foreach (var tile in running)
                items.Add(new TrayMenuItem($"Завершить «{tile.Name}»", () =>
                {
                    ShowFromTray(); // вопрос «Завершить?» должен быть виден
                    tile.KillCommand.Execute(null);
                }));
        }

        items.Add(null);
        items.Add(new TrayMenuItem("Настройки", () =>
        {
            ShowFromTray();
            _viewModel.OpenSettings();
        }));
        items.Add(new TrayMenuItem("Журнал событий", () => _viewModel.ShowLogCommand.Execute(null)));
        items.Add(null);
        items.Add(new TrayMenuItem("Закрыть лаунчер", ExitApp));
        _tray.ShowMenu(items);
    }

    // ---------------------------------------------------------------- окно

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized)
            _restoreState = WindowState;

        // Развёрнутое окно без системной рамки выходит за край экрана на толщину рамки: отступаем на неё.
        RootGrid.Margin = WindowState == WindowState.Maximized ? GetMaximizedInset() : new Thickness(0);
    }

    private Thickness GetMaximizedInset()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var padded = GetSystemMetrics(SmCxPaddedBorder);
        var x = (GetSystemMetrics(SmCxSizeFrame) + padded) / dpi.DpiScaleX;
        var y = (GetSystemMetrics(SmCySizeFrame) + padded) / dpi.DpiScaleY;
        return new Thickness(x, y, x, y);
    }

    /// <summary>В Windows 11 окно без системного заголовка тоже получает скруглённые углы. На Windows 10 вызов ничего не делает.</summary>
    private void RoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var preference = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // старая ОС: углы останутся прямыми
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            // Alt+F4 или «Закрыть окно» на панели задач — как кнопка «Закрыть». Решаем после выхода из обработчика.
            e.Cancel = true;
            Dispatcher.BeginInvoke(RequestHide);
            base.OnClosing(e);
            return;
        }

        _closing = true;
        if (!_sessionEnding && !_viewModel.ConfirmClose())
        {
            e.Cancel = true;
            _exitRequested = false;
            _closing = false;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _toastTimer.Stop();
        _tray.Dispose();
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
