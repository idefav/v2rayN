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
            < 0 => dark ? "#FF8B8B" : "#C83232",
            0 => dark ? "#A5B2C6" : "#667386",
            <= 500 => dark ? "#8EDFB2" : "#137C45",
            _ => dark ? "#FFD28D" : "#9A5B08"
        };
        return new SolidColorBrush(Avalonia.Media.Color.Parse(color));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null;
    }
}
