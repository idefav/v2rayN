using System.Globalization;
using System.Xml.Linq;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Platform;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace v2rayN.Desktop.Common;

/// <summary>Local brand and interface vectors with no icon-font or network dependency.</summary>
public static class WorkspaceIcons
{
    private static readonly Lazy<Dictionary<string, XElement>> Drawings = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://v2rayN/Assets/ConnectionIcons.svg"));
        var drawings = XDocument.Load(stream).Root!.Elements().ToDictionary(e => e.Attribute("id")!.Value);
        // The same source also generates every platform's notification icon.
        using var brand = AssetLoader.Open(new Uri("avares://v2rayN/Assets/BrandGlyph.svg"));
        drawings.Add("brand", XDocument.Load(brand).Root!);
        return drawings;
    });

    public static Control Create(string name, double size = 20, string brush = "WorkspaceText")
    {
        var canvas = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false };
        foreach (var element in Drawings.Value[name].Descendants()
                     .Where(e => e.Name.LocalName is "path" or "circle" or "rect"))
        {
            double Number(string key) => double.Parse(element.Attribute(key)?.Value ?? "0", CultureInfo.InvariantCulture);
            Shape shape;
            if (element.Name.LocalName == "path") shape = new ShapePath { Data = StreamGeometry.Parse(element.Attribute("d")!.Value) };
            else
            {
                shape = element.Name.LocalName == "circle"
                    ? new Ellipse { Width = Number("r") * 2, Height = Number("r") * 2 }
                    : new Rectangle { Width = Number("width"), Height = Number("height"), RadiusX = Number("rx"), RadiusY = Number("rx") };
                Canvas.SetLeft(shape, element.Name.LocalName == "circle" ? Number("cx") - Number("r") : Number("x"));
                Canvas.SetTop(shape, element.Name.LocalName == "circle" ? Number("cy") - Number("r") : Number("y"));
            }
            shape.StrokeThickness = double.Parse(element.AncestorsAndSelf().Attributes("stroke-width")
                .FirstOrDefault()?.Value ?? "1.75", CultureInfo.InvariantCulture);
            shape.StrokeLineCap = PenLineCap.Round;
            shape.StrokeJoin = PenLineJoin.Round;
            shape.Fill = null;
            shape.Bind(Shape.StrokeProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(brush));
            canvas.Children.Add(shape);
        }
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    }

    public static void SetButton(Button button, string icon, string text, string brush = "WorkspaceText")
    {
        // A horizontal StackPanel measures text with infinite width, so Wrap cannot
        // protect narrow navigation items or buttons when the font is enlarged.
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*"), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(Create(icon, 18, brush));
        var label = new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        button.Content = content;
        Avalonia.Automation.AutomationProperties.SetName(button, text);
    }
}
