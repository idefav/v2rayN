using Avalonia.Data.Converters;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Converters;

public sealed class UnknownTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) || text.Equals(Global.None, StringComparison.OrdinalIgnoreCase)
            ? parameter?.ToString() == "test" ? WorkspaceText.Untested : "—" : text;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
