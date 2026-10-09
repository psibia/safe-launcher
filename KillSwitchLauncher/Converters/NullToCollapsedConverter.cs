using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KillSwitchLauncher.Converters;

/// <summary>null → Collapsed, иначе Visible.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
