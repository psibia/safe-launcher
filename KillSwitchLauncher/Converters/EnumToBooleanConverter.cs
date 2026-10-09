using System;
using System.Globalization;
using System.Windows.Data;

namespace KillSwitchLauncher.Converters;

/// <summary>Связывает RadioButton.IsChecked со значением enum (ConverterParameter = имя значения).</summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}
