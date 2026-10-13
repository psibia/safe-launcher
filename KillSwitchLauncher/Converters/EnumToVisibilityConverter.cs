using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace KillSwitchLauncher.Converters;

/// <summary>Visible, если значение enum совпадает с одним из имён в ConverterParameter («A» или «A,B»), иначе Collapsed.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value?.ToString();
        var names = (parameter as string ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return name is not null && names.Contains(name) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
