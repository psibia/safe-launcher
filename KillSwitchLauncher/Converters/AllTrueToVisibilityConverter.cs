using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace KillSwitchLauncher.Converters;

/// <summary>MultiBinding: Visible, только если все значения true.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
