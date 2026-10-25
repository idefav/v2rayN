using Avalonia.Data.Converters;

namespace v2rayN.Desktop.Converters;

public class DelayColorConverter : IValueConverter, IMultiValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Color(value, Application.Current?.ActualThemeVariant);
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => Color(values.FirstOrDefault(), values.ElementAtOrDefault(1) as ThemeVariant);

    private static IBrush Color(object? value, ThemeVariant? theme)
    {
        var delay = value?.ToString().ToInt() ?? 0;
        while (theme?.InheritVariant != null) theme = theme.InheritVariant;
        var dark = theme == ThemeVariant.Dark;
        var color = delay switch
        {
            < 0 => dark ? "#FF7B72" : "#CF222E",
            0 => dark ? "#A0A9B4" : "#656D76",
            <= 500 => dark ? "#7EE787" : "#1A7F37",
            _ => dark ? "#D29922" : "#9A6700"
        };
        return new SolidColorBrush(Avalonia.Media.Color.Parse(color));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null;
    }
}
