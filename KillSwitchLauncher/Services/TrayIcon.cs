using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillSwitchLauncher.Services;

/// <summary>Пункт меню значка в трее. Действие выполняется после закрытия меню.</summary>
public sealed record TrayMenuItem(string Text, Action Action, bool IsDefault = false);

/// <summary>
/// Значок в области уведомлений (трей) на чистом Windows API, без Windows Forms.
/// Двойной клик и пункт меню открывают окно, правый клик — меню, а уведомления Windows
/// показываются, пока окно скрыто. Все события приходят в UI-потоке.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1

    private readonly HwndSource _window;
    private readonly int _taskbarCreatedMessage;
    private IntPtr _normalIcon;
    private IntPtr _alertIcon;
    private bool _isAlert;
    private string _toolTip = string.Empty;
    private bool _added;
    private bool _version4;
    private bool _disposed;

    public TrayIcon(string toolTip)
    {
        _toolTip = toolTip;

        // Обычное невидимое окно, а не message-only: только верхнеуровневые окна получают
        // сообщение TaskbarCreated, по которому значок возвращается после перезапуска Проводника.
        _window = new HwndSource(new HwndSourceParameters("SafeLauncherTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080 // WS_EX_TOOLWINDOW: не появляется в Alt+Tab
        });
        _window.AddHook(WndProc);
        _taskbarCreatedMessage = (int)RegisterWindowMessageW("TaskbarCreated");

        _normalIcon = CreateIcon(alert: false);
        _alertIcon = CreateIcon(alert: true);
        Add();
    }

    /// <summary>Двойной клик по значку или клик по уведомлению.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Правый клик по значку: пора показать меню (<see cref="ShowMenu"/>).</summary>
    public event EventHandler? MenuRequested;

    /// <summary>Значок действительно показан (без Проводника, например, его некуда добавить).</summary>
    public bool IsShown => _added && !_disposed;

    /// <summary>
    /// Системное меню Windows у курсора (null в списке — разделитель). Обычное меню Windows надёжно
    /// работает из трея: открывается поверх панели задач и закрывается по клику мимо.
    /// </summary>
    public void ShowMenu(IReadOnlyList<TrayMenuItem?> items)
    {
        if (_disposed)
            return;

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is { } item)
                {
                    AppendMenuW(menu, MfString, (UIntPtr)(i + 1), item.Text);
                    if (item.IsDefault)
                        SetMenuDefaultItem(menu, (uint)(i + 1), 0); // пункт по умолчанию — жирный
                }
                else
                {
                    AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
                }
            }

            GetCursorPos(out var cursor);

            // Без этого меню не закрывается по клику мимо него (особенность Windows, описана в документации).
            SetForegroundWindow(_window.Handle);
            var command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNoNotify | TpmBottomAlign,
                cursor.X, cursor.Y, _window.Handle, IntPtr.Zero);
            PostMessageW(_window.Handle, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (command > 0 && command <= items.Count && items[command - 1] is { } chosen)
                _window.Dispatcher.BeginInvoke(chosen.Action);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>Подсказка при наведении (до 127 символов) и вид значка: false — обычный, true — VPN нет.</summary>
    public void Update(string toolTip, bool alert)
    {
        if (_disposed || (toolTip == _toolTip && alert == _isAlert))
            return;

        _toolTip = toolTip;
        _isAlert = alert;
        var data = CreateData(NifIcon | NifTip | NifShowTip);
        Shell_NotifyIconW(NimModify, ref data);
    }

    /// <summary>Уведомление Windows от имени значка (в Windows 10/11 — всплывающее уведомление в углу).</summary>
    public void ShowNotification(string title, string text, bool warning)
    {
        if (_disposed)
            return;

        var data = CreateData(NifInfo);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(string.IsNullOrWhiteSpace(text) ? " " : text, 255);
        data.dwInfoFlags = warning ? NiifWarning : NiifInfo;
        Shell_NotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_added)
        {
            var data = CreateData(0);
            Shell_NotifyIconW(NimDelete, ref data);
        }

        _window.RemoveHook(WndProc);
        _window.Dispose();
        DestroyIcon(_normalIcon);
        DestroyIcon(_alertIcon);
        _normalIcon = _alertIcon = IntPtr.Zero;
    }

    // ---------------------------------------------------------------- Shell_NotifyIcon

    private void Add()
    {
        var data = CreateData(NifMessage | NifIcon | NifTip | NifShowTip);
        _added = Shell_NotifyIconW(NimAdd, ref data) || Shell_NotifyIconW(NimModify, ref data);
        if (!_added)
            return;

        // Версия 4: правый клик приходит как WM_CONTEXTMENU, подсказка — по флагу NIF_SHOWTIP.
        // Если Windows её не приняла, работаем по-старому: меню по WM_RBUTTONUP.
        data.uVersionOrTimeout = NotifyIconVersion4;
        _version4 = Shell_NotifyIconW(NimSetVersion, ref data);
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _isAlert ? _alertIcon : _normalIcon,
        szTip = Truncate(_toolTip, 127),
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Проводник перезапустился: значок пропал вместе с панелью задач, добавляем заново.
            Add();
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage)
            return IntPtr.Zero;

        handled = true;
        var eventCode = (int)((long)lParam & 0xFFFF);
        switch (eventCode)
        {
            case WmLButtonDblClk:
            case NinKeySelect:
            case NinBalloonUserClick:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;

            case WmContextMenu:
            case WmRButtonUp when !_version4:
                MenuRequested?.Invoke(this, EventArgs.Empty);
                break;
        }

        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- значок

    /// <summary>
    /// Значок рисуется из логотипа под размер маленьких значков системы. Когда VPN нет,
    /// логотип бледнеет и в углу появляется красная точка: это видно, даже не наводя курсор.
    /// </summary>
    private static IntPtr CreateIcon(bool alert)
    {
        try
        {
            var size = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
            var logo = new BitmapImage();
            logo.BeginInit();
            // С именем сборки: так картинка находится и тогда, когда значок создают тесты.
            logo.UriSource = new Uri("pack://application:,,,/KillSwitchLauncher;component/Assets/logo.png");
            logo.CacheOption = BitmapCacheOption.OnLoad;
            logo.EndInit();

            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
            {
                if (alert)
                    dc.PushOpacity(0.45);
                dc.DrawImage(logo, new Rect(0, 0, size, size));
                if (alert)
                {
                    dc.Pop();
                    var radius = size * 0.24;
                    var center = new Point(size - radius - 0.5, size - radius - 0.5);
                    dc.DrawEllipse(Brushes.White, null, center, radius + size * 0.07, radius + size * 0.07);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A)), null, center, radius, radius);
                }
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            // Значок Windows ждёт обычную (не умноженную на альфу) прозрачность.
            var straight = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = size * 4;
            var pixels = new byte[stride * size];
            straight.CopyPixels(pixels, stride, 0);

            var color = CreateBitmap(size, size, 1, 32, pixels);
            var mask = CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
            try
            {
                var info = new IconInfo { fIcon = true, hbmColor = color, hbmMask = mask };
                return CreateIconIndirect(ref info);
            }
            finally
            {
                DeleteObject(color);
                DeleteObject(mask);
            }
        }
        catch (Exception)
        {
            return IntPtr.Zero; // без картинки значок всё равно появится, просто пустым
        }
    }

    internal static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // ---------------------------------------------------------------- Windows API

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NiifInfo = 0x01, NiifWarning = 0x02;
    private const uint NotifyIconVersion4 = 4;
    private const int WmNull = 0x0000, WmContextMenu = 0x007B, WmLButtonDblClk = 0x0203, WmRButtonUp = 0x0205, NinKeySelect = 0x0401, NinBalloonUserClick = 0x0405;
    private const int SmCxSmIcon = 49;
    private const uint MfString = 0x0000, MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002, TpmBottomAlign = 0x0020, TpmNoNotify = 0x0080, TpmReturnCmd = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
