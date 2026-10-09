using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace KillSwitchLauncher.Views;

/// <summary>
/// Значок из шрифта Segoe Fluent Icons / Segoe MDL2 Assets, выровненный по центру своего контура.
/// Обычный TextBlock центрирует строку по метрикам шрифта, и знак (например «+») в круге съезжает.
/// </summary>
public sealed class GlyphIcon : FrameworkElement
{
    private static readonly Typeface IconTypeface =
        new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Цвет наследуется так же, как у текста.</summary>
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(GlyphIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Кегль значка; элемент занимает квадрат Size × Size.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Glyph)) return;

        var text = new FormattedText(Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconTypeface, Size,
            Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var geometry = text.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;
        if (bounds.IsEmpty) return;

        dc.PushTransform(new TranslateTransform(
            RenderSize.Width / 2 - (bounds.X + bounds.Width / 2),
            RenderSize.Height / 2 - (bounds.Y + bounds.Height / 2)));
        dc.DrawGeometry(Foreground, null, geometry);
        dc.Pop();
    }
}
