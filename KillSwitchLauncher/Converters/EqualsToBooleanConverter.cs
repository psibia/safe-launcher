using System;
using System.Globalization;
using System.Windows.Data;

namespace KillSwitchLauncher.Converters;

/// <summary>Связывает RadioButton.IsChecked с числом (ConverterParameter = значение), например срок хранения журналов.</summary>
public sealed class EqualsToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is not null
            ? System.Convert.ChangeType(parameter.ToString(), Nullable.GetUnderlyingType(targetType) ?? targetType, CultureInfo.InvariantCulture)!
            : Binding.DoNothing;
}
