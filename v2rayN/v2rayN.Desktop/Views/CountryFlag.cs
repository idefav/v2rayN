using Avalonia.Automation;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ServiceLib.Models.Dto;

namespace v2rayN.Desktop.Views;

// Local assets only; unknown countries have a neutral placeholder, never a guessed flag.
public sealed class CountryFlag : Decorator
{
    private static readonly Dictionary<string, Bitmap> Flags = new(StringComparer.OrdinalIgnoreCase);
    public static readonly StyledProperty<string?> CodeProperty = AvaloniaProperty.Register<CountryFlag, string?>(nameof(Code));
    public string? Code { get => GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    public CountryFlag() { Width = 22; Height = 16; IsHitTestVisible = false; UpdateFlag(); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CodeProperty) UpdateFlag();
    }
    private void UpdateFlag()
    {
        var code = ProxyCheckCountries.Normalize(Code);
        var uri = code == null ? null : new Uri($"avares://v2rayN/Assets/Flags/{code.ToLowerInvariant()}.png");
        if (code != null && uri != null && AssetLoader.Exists(uri))
        {
            if (!Flags.TryGetValue(code, out var flag))
            {
                using var stream = AssetLoader.Open(uri); Flags[code] = flag = new Bitmap(stream);
            }
            Child = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(110, 128, 128, 128)), BorderThickness = new Thickness(1),
                Child = new Image { Source = flag, Stretch = Stretch.Uniform }
            };
        }
        else Child = new TextBlock { Text = "◇", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, FontSize = 14 };
        AutomationProperties.SetName(this, code == null ? ProxyCheckText.T("地域未知", "Unknown region") : ProxyCheckCountries.Name(code));
    }
}
