using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Platform;
using SkiaSharp;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private void ReviewBrandAssets()
    {
        foreach (var name in new[] { "v2rayN", "NotifyIcon1", "NotifyIcon2", "NotifyIcon3", "NotifyIcon4" })
        {
            using var resource = AssetLoader.Open(new Uri($"avares://v2rayN/Assets/{name}.ico"));
            using var memory = new MemoryStream();
            resource.CopyTo(memory);
            var bytes = memory.ToArray();
            using var input = new MemoryStream(bytes);
            _ = new WindowIcon(input); // Exercise Avalonia's real ICO decoder.
            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
            Check(count > 1, $"{name}: packaged ICO contains multiple DPI sizes");
            for (var i = 0; i < count; i++)
            {
                var entry = bytes.AsSpan(6 + i * 16, 16);
                var size = entry[0] == 0 ? 256 : entry[0];
                var length = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
                var offset = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
                using var bitmap = SKBitmap.Decode(bytes.AsSpan(offset, length).ToArray());
                Check(bitmap != null && bitmap.Width == size && bitmap.Height == size
                    && bitmap.GetPixel(0, 0).Alpha == 0 && bitmap.Pixels.Any(p => p.Alpha > 220),
                    $"{name}: {size}px embedded frame decodes with transparent corners and visible content");
            }
        }
        var brand = (Viewbox)WorkspaceIcons.Create("brand");
        var canvas = (Canvas)brand.Child!;
        Check(canvas.Children.Count == 2 && canvas.Children.OfType<Shape>().All(s => s.StrokeThickness == 3 && s.Fill == null),
            "brand renderer handles grouped SVG paths and inherited stroke width without painting metadata");
        var ordinary = (Canvas)((Viewbox)WorkspaceIcons.Create("settings")).Child!;
        Check(ordinary.Children.OfType<Shape>().All(s => s.StrokeThickness == 1.75),
            "new brand stroke does not change existing functional icon weights");

        // Only the isolated test .app opts in. Never set the NSApplication icon:
        // this observes what AppKit actually loaded from the bundle's ICNS.
        var expectedPath = Environment.GetEnvironmentVariable("V2RAYN_REVIEW_BUNDLE_ICON");
        if (Program.Headless || string.IsNullOrEmpty(expectedPath)) return;
        var native = MacTrayProbe.ReadApplicationIcon();
        File.WriteAllBytes(System.IO.Path.Combine(Program.Output, "native-application-icon.png"), native);
        using var actual = SKBitmap.Decode(native);
        using var expected = SKBitmap.Decode(expectedPath);
        Check(actual != null && expected != null, "AppKit loads an application icon from the isolated bundle");
        var canonicalIcns = Environment.GetEnvironmentVariable("V2RAYN_REVIEW_BUNDLE_ICNS")!;
        var bundleIcns = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../Resources/AppIcon.icns"));
        Check(File.ReadAllBytes(bundleIcns).SequenceEqual(File.ReadAllBytes(canonicalIcns)),
            "isolated bundle icon bytes match the canonical application ICNS");
        // AppKit may inset and add highlights to legacy ICNS artwork. Compare
        // the normalized white symbol, not pixel colors of its system material.
        var overlap = BrandShapeOverlap(actual!, expected!);
        Console.WriteLine($"Native bundle icon: {actual!.Width}x{actual.Height}; normalized symbol IoU={overlap:F4}");
        Check(overlap > .85, "actual AppKit Dock image retains the new canonical brand geometry");
        var legacyPath = Environment.GetEnvironmentVariable("V2RAYN_REVIEW_LEGACY_ICON")!;
        using var legacy = SKBitmap.Decode(legacyPath);
        Check(legacy != null && BrandShapeOverlap(legacy, expected!) < .5,
            "Dock shape comparison rejects the legacy V icon as a negative control");
    }

    private static double BrandShapeOverlap(SKBitmap first, SKBitmap second)
    {
        static bool[] Mask(SKBitmap bitmap)
        {
            using var scaled = bitmap.Resize(new SKImageInfo(256, 256), new SKSamplingOptions(SKFilterMode.Linear));
            var mask = new bool[256 * 256];
            var minX = 256; var minY = 256; var maxX = -1; var maxY = -1;
            // Exclude highlights on the tile boundary. Both the old and new
            // symbols lie within this central region, including after inset.
            for (var y = 40; y < 216; y++)
            for (var x = 40; x < 216; x++)
            {
                var pixel = scaled.GetPixel(x, y);
                var low = Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
                var high = Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue));
                if (pixel.Alpha <= 200 || low <= 170 || high - low >= 70) continue;
                mask[y * 256 + x] = true;
                minX = Math.Min(x, minX); minY = Math.Min(y, minY);
                maxX = Math.Max(x, maxX); maxY = Math.Max(y, maxY);
            }
            if (maxX - minX < 32 || maxY - minY < 32) return new bool[128 * 128];
            var normalized = new bool[128 * 128];
            for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++)
                normalized[y * 128 + x] = mask[(minY + y * (maxY - minY + 1) / 128) * 256
                    + minX + x * (maxX - minX + 1) / 128];
            return normalized;
        }
        var left = Mask(first); var right = Mask(second);
        var intersection = left.Zip(right, (a, b) => a && b).Count(value => value);
        var union = left.Zip(right, (a, b) => a || b).Count(value => value);
        return union == 0 ? 0 : (double)intersection / union;
    }
}
