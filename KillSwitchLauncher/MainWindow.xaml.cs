using System;
using System.ComponentModel;
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
        _tray.Update(BuildTrayToolTip(), alert: !_viewModel.IsVpnConnected);
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

    /// <summary>Клик мимо панели настроек закрывает её (кроме кнопки «Настройки», она сама переключает панель).</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        if (_viewModel.IsRulesPanelOpen && e.OriginalSource is DependencyObject source &&
            !IsInside(source, SettingsCard) && !IsInside(source, SettingsToggle))
        {
            _viewModel.IsRulesPanelOpen = false;
        }
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, container))
                return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

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
        $"Безопасный лаунчер\n{_viewModel.VpnStatusText} · {_viewModel.RunningSummary}" +
        (_viewModel.IsKillSwitchOff ? "\nKill-Switch выключен" : string.Empty);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsVpnConnected):
            case nameof(MainViewModel.RunningSummary):
            case nameof(MainViewModel.IsKillSwitchOff):
                _tray.Update(BuildTrayToolTip(), alert: !_viewModel.IsVpnConnected);
                break;

            case nameof(MainViewModel.IsRulesPanelOpen) when _viewModel.IsRulesPanelOpen && _viewModel.IsUpdateAvailable:
                // Есть обновление: панель настроек сразу прокручивается к нему.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AboutRow.BringIntoView());
                break;
        }
    }

    /// <summary>Меню значка в трее: обычное меню Windows («Открыть» — по умолчанию, жирным).</summary>
    private void ShowTrayMenu() => _tray.ShowMenu(new TrayMenuItem?[]
    {
        new("Открыть", ShowFromTray, IsDefault: true),
        new("Журнал событий", () => _viewModel.ShowLogCommand.Execute(null)),
        null,
        new("Закрыть лаунчер", ExitApp)
    });

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
