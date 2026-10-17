using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KillSwitchLauncher.Views;

/// <summary>
/// WrapPanel, у которого плитки плавно переезжают на новое место: при перетаскивании соседи сдвигаются,
/// а не прыгают. Переносы строк как у обычного WrapPanel.
/// </summary>
public sealed class AnimatedWrapPanel : WrapPanel
{
    private static readonly Duration MoveDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private readonly ConditionalWeakTable<UIElement, Box> _positions = new();

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);

        foreach (UIElement child in InternalChildren)
        {
            var now = LayoutInformation.GetLayoutSlot((FrameworkElement)child).TopLeft;
            if (_positions.TryGetValue(child, out var box))
            {
                if (box.Position != now)
                    Animate(child, box.Position - now);
                box.Position = now;
            }
            else
                _positions.Add(child, new Box { Position = now });
        }
        return result;
    }

    /// <summary>Сдвинуть плитку на прежнее место и плавно вернуть к новому.</summary>
    private static void Animate(UIElement child, Vector shift)
    {
        if (child.RenderTransform is not TranslateTransform move || move.IsFrozen)
        {
            move = new TranslateTransform();
            child.RenderTransform = move;
        }

        // Плитка ещё ехала: начинаем с того места, где она сейчас видна.
        var x = move.X + shift.X;
        var y = move.Y + shift.Y;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, 0, MoveDuration) { EasingFunction = Ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, 0, MoveDuration) { EasingFunction = Ease });
    }

    private sealed class Box
    {
        public Point Position;
    }
}
