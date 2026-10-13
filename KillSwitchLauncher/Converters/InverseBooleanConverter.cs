using System;
using System.Globalization;
using System.Windows.Data;

namespace KillSwitchLauncher.Converters;

/// <summary>true ↔ false.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
