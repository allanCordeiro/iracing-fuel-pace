using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FuelPace.Settings;

/// <summary>Visible when the bound section name is the parameter (the page that belongs to the selected menu item).</summary>
public sealed class SectionVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value as string, parameter as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Collapsed for null, an empty string or false; visible otherwise.</summary>
public sealed class NotEmptyVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" or false ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Number fields (ruling A25): shown with a point, typed with a comma or a point. Text that is not a number of the field's type
/// leaves the view model unchanged and marks the field invalid (a conversion error of the binding).
/// </summary>
public sealed class NumberInputConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        int i => FuelPace.App.SettingsUi.NumberInput.Format(i),
        double d => FuelPace.App.SettingsUi.NumberInput.Format(d),
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        if (targetType == typeof(int))
            return FuelPace.App.SettingsUi.NumberInput.TryParseInt(text, out int i) ? i : DependencyProperty.UnsetValue;
        return FuelPace.App.SettingsUi.NumberInput.TryParseDouble(text, out double d) ? d : DependencyProperty.UnsetValue;
    }
}
