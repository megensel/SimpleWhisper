using System.Globalization;
using System.Windows.Data;

namespace SimpleWhisper.Views.Controls;

/// <summary>
/// Converts a <see cref="bool"/> value to its inverse. Used in XAML bindings where
/// an element should be enabled when a boolean property is <c>false</c> (e.g. language
/// ComboBox enabled when Auto-detect is off).
/// </summary>
/// <remarks>
/// Usage in XAML: <c>IsEnabled="{Binding SomeFlag, Converter={x:Static controls:InverseBooleanConverter.Instance}}"</c>
/// </remarks>
public sealed class InverseBooleanConverter : IValueConverter
{
    /// <summary>
    /// Singleton instance for use with <c>x:Static</c> in XAML.
    /// </summary>
    public static readonly InverseBooleanConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return value;
    }
}
