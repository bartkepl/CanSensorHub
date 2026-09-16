using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CanSensorHub.App.Converters;

/// <summary>bool -&gt; Visibility (true = Visible). Registered app-wide as "BoolToVis" so every module's XAML can use it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>bool -&gt; !bool. Registered app-wide as "InverseBool" (e.g. IsEnabled bound to the negation of IsReadOnly/IsBusy).</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b && !b;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b && !b;
}

/// <summary>bool -&gt; status-dot Brush (green when true, gray otherwise). Used for connection/online indicators.</summary>
public sealed class ConnectionDotConverter : IValueConverter
{
    private static readonly SolidColorBrush OnlineBrush = new(Color.FromRgb(0x4C, 0xBB, 0x6C));
    private static readonly SolidColorBrush OfflineBrush = new(Color.FromRgb(0x8A, 0x8A, 0x8A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? OnlineBrush : OfflineBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
