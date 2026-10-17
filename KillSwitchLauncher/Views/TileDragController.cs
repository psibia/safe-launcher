using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillSwitchLauncher.ViewModels;

namespace KillSwitchLauncher.Views;

/// <summary>
/// Перетаскивание плиток главного экрана мышью: копия плитки за курсором, пустая ячейка под курсором,
/// соседи сдвигаются, у края список прокручивается, Esc — отмена.
/// Порядок меняет <see cref="HomeBoardViewModel"/>; здесь только мышь, координаты и копия плитки.
/// </summary>
public sealed class TileDragController
{
    /// <summary>Полоса у верхнего и нижнего края, в которой список прокручивается сам.</summary>
    private const double ScrollZone = 40;

    private readonly Window _window;
    private readonly FrameworkElement _homeArea;
    private readonly ScrollViewer _homeScroll;
    private readonly ItemsControl _homeTiles;
    private readonly Func<HomeBoardViewModel> _home;
    private readonly DispatcherTimer _scrollTimer;

    private Pending? _pending;
    private DragCopy? _copy;
    private Vector _grab;
    private Point _last;
    private double _scrollStep;
    private bool _releasing;

    public TileDragController(Window window, FrameworkElement homeArea, ScrollViewer homeScroll, ItemsControl homeTiles,
        Func<HomeBoardViewModel> home)
    {
        _window = window;
        _homeArea = homeArea;
        _homeScroll = homeScroll;
        _homeTiles = homeTiles;
        _home = home;

        _scrollTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        _scrollTimer.Tick += OnScrollTick;

        _homeTiles.PreviewMouseLeftButtonDown += OnPress;
        _homeArea.PreviewMouseMove += OnMove;
        _homeArea.PreviewMouseLeftButtonUp += OnRelease;
        _homeArea.LostMouseCapture += OnLostCapture;
        _window.PreviewKeyDown += OnKeyDown;
    }

    private bool IsDragging => _copy is not null;

    // ---------------------------------------------------------------- начало

    /// <summary>Нажатие на плитку: только запомнить. Кнопка получает нажатие как обычно, короткий клик её запустит.</summary>
    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        _pending = null;
        if (IsDragging || _home().IsDragging || VisualOf(e.OriginalSource) is not { } source || !_homeTiles.IsAncestorOf(source))
            return;
        if (_homeTiles.ContainerFromElement(source) is not FrameworkElement container
            || _homeTiles.ItemContainerGenerator.ItemFromContainer(container) is not AppTileViewModel tile)
            return;

        _pending = new Pending(tile, container, e.GetPosition(_homeArea));
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (IsDragging)
        {
            Track(e.GetPosition(_homeArea));
            return;
        }

        if (_pending is not { } pending)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pending = null;
            return;
        }

        var shift = e.GetPosition(_homeArea) - pending.Point;
        if (Math.Abs(shift.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(shift.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _pending = null;
        Start(pending, e.GetPosition(_homeArea));
    }

    private void Start(Pending pending, Point point)
    {
        var home = _home();

        // Снимок до IsDragSource: потом плитка станет прозрачной.
        var image = Snapshot(pending.Container);
        var topLeft = pending.Container.TranslatePoint(new Point(), _homeArea);
        if (!home.BeginDrag(pending.Tile))
            return;

        _grab = pending.Point - topLeft;
        _copy = new DragCopy(_homeArea, image, pending.Container.RenderSize);
        if (_window.TryFindResource("PopupShadow") is Effect shadow)
            _copy.Effect = shadow;
        AdornerLayer.GetAdornerLayer(_homeArea)?.Add(_copy);

        // Захват забирает мышь у кнопки плитки: отпускание не станет кликом.
        if (!Mouse.Capture(_homeArea, CaptureMode.Element))
        {
            home.CancelDrag();
            Finish();
            return;
        }
        Track(point);
    }

    // ---------------------------------------------------------------- движение

    private void Track(Point point)
    {
        if (!_home().IsDragging)
        {
            // Перетаскивание закончилось само (список приложений поменялся).
            Finish();
            return;
        }

        _last = point;
        _copy?.MoveTo(point - _grab);
        Place(point);
        UpdateAutoScroll(point);
    }

    /// <summary>Пустая ячейка — под курсором.</summary>
    private void Place(Point point)
    {
        var home = _home();
        var index = IndexAt(point);
        if (index >= 0 && index != home.DraggedIndex)
        {
            home.MoveDragged(index);
            _homeTiles.UpdateLayout();
        }
    }

    /// <summary>
    /// Ячейка под курсором: та, что содержит точку, иначе ближайшая. Пустое место после последней плитки — последняя ячейка.
    /// Места ячеек берутся из разметки, без сдвига анимации, поэтому пустая ячейка не «дрожит».
    /// </summary>
    private int IndexAt(Point point)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _homeTiles.Items.Count; i++)
        {
            if (Slot(i) is not { } slot)
                continue;
            if (slot.Contains(point))
                return i;

            var dx = Math.Max(Math.Max(slot.Left - point.X, 0), point.X - slot.Right);
            var dy = Math.Max(Math.Max(slot.Top - point.Y, 0), point.Y - slot.Bottom);
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Место ячейки <paramref name="index"/> в координатах области плиток (с полями плитки).</summary>
    private Rect? Slot(int index)
    {
        if (_homeTiles.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container
            || VisualTreeHelper.GetParent(container) is not UIElement panel
            || !_homeArea.IsAncestorOf(panel))
            return null;

        var slot = LayoutInformation.GetLayoutSlot(container);
        return new Rect(panel.TranslatePoint(slot.TopLeft, _homeArea), slot.Size);
    }

    // ---------------------------------------------------------------- автопрокрутка

    private void UpdateAutoScroll(Point point)
    {
        var view = new Rect(_homeScroll.TranslatePoint(new Point(), _homeArea), _homeScroll.RenderSize);
        _scrollStep = 0;
        if (view.Height > 0 && point.X >= view.Left && point.X <= view.Right)
        {
            if (point.Y < view.Top + ScrollZone)
                _scrollStep = -Step(view.Top + ScrollZone - point.Y);
            else if (point.Y > view.Bottom - ScrollZone)
                _scrollStep = Step(point.Y - (view.Bottom - ScrollZone));
        }

        if (_scrollStep != 0)
            _scrollTimer.Start();
        else
            _scrollTimer.Stop();
    }

    /// <summary>2–18 px за шаг: чем глубже курсор в полосе у края, тем быстрее.</summary>
    private static double Step(double depth) => 2 + 16 * Math.Clamp(depth / ScrollZone, 0, 1);

    private void OnScrollTick(object? sender, EventArgs e)
    {
        if (!IsDragging || _scrollStep == 0)
        {
            _scrollTimer.Stop();
            return;
        }

        var before = _homeScroll.VerticalOffset;
        _homeScroll.ScrollToVerticalOffset(before + _scrollStep);
        _homeScroll.UpdateLayout();
        if (_homeScroll.VerticalOffset != before)
            Place(_last);
    }

    // ---------------------------------------------------------------- конец

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        _pending = null;
        if (!IsDragging)
            return;

        var home = _home();
        if (new Rect(_homeArea.RenderSize).Contains(e.GetPosition(_homeArea)))
            home.Drop();
        else
            home.CancelDrag(); // отпустили над шапкой, строкой состояния или вне окна
        Finish();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsDragging || e.Key != Key.Escape)
            return;
        _home().CancelDrag();
        Finish();
        e.Handled = true; // Esc не должен уйти дальше (закрыть сводку, сменить страницу)
    }

    /// <summary>Мышь забрало другое окно (Alt+Tab, UAC, всплывающее окно): отмена. Свой отпуск захвата не в счёт.</summary>
    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (!IsDragging || _releasing || e.OriginalSource != _homeArea)
            return;
        _home().CancelDrag();
        Finish();
    }

    private void Finish()
    {
        _scrollTimer.Stop();
        _scrollStep = 0;
        if (_copy is { } copy)
        {
            AdornerLayer.GetAdornerLayer(_homeArea)?.Remove(copy);
            _copy = null;
        }

        if (Mouse.Captured == _homeArea)
        {
            _releasing = true;
            try
            {
                _homeArea.ReleaseMouseCapture();
            }
            finally
            {
                _releasing = false;
            }
        }
    }

    // ---------------------------------------------------------------- копия плитки

    /// <summary>Картинка плитки в разрешении экрана (масштаб Windows 150% — чёткая копия).</summary>
    private BitmapSource Snapshot(FrameworkElement element)
    {
        var size = element.RenderSize;

        // VisualBrush рисует элемент на его месте в родителе (со сдвигом ячейки), поэтому окно просмотра —
        // прямоугольник плитки в координатах родителя. Без этого у всех плиток, кроме первой, копия была пустой.
        // Если вдруг картинка пустая — второй способ: в собственных координатах элемента.
        var inParent = VisualTreeHelper.GetParent(element) is Visual parent
            ? element.TransformToVisual(parent).TransformBounds(new Rect(size))
            : new Rect(size);
        var bitmap = Render(element, inParent);
        if (IsBlank(bitmap) && inParent != new Rect(size))
            bitmap = Render(element, new Rect(size));
        bitmap.Freeze();
        return bitmap;
    }

    private RenderTargetBitmap Render(Visual element, Rect viewbox)
    {
        var dpi = VisualTreeHelper.GetDpi(_window);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(viewbox.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(viewbox.Height * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new VisualBrush(element)
            {
                Viewbox = viewbox,
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            };
            dc.DrawRectangle(brush, null, new Rect(viewbox.Size));
        }
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>Ни одного непрозрачного пикселя.</summary>
    private static bool IsBlank(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
                return false;
        }
        return true;
    }

    private static DependencyObject? VisualOf(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null and not Visual)
            d = LogicalTreeHelper.GetParent(d);
        return d;
    }

    private sealed record Pending(AppTileViewModel Tile, FrameworkElement Container, Point Point);

    /// <summary>Полупрозрачная копия плитки под курсором в слое украшений; мышь сквозь неё проходит.</summary>
    private sealed class DragCopy : Adorner
    {
        private readonly ImageSource _image;
        private readonly Size _size;
        private Point _position;

        public DragCopy(UIElement adorned, ImageSource image, Size size)
            : base(adorned)
        {
            _image = image;
            _size = size;
            IsHitTestVisible = false;
        }

        public void MoveTo(Point position)
        {
            _position = position;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.PushOpacity(0.85);
            dc.DrawImage(_image, new Rect(_position, _size));
            dc.Pop();
        }
    }
}
