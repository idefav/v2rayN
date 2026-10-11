using Avalonia.Data.Converters;

namespace v2rayN.Desktop.Converters;

public sealed class ActiveStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ResUI.TipActiveServer : "—";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
