using System.Runtime.InteropServices;

namespace v2rayN.Desktop.Review;

// Inspect only this test process's AppKit views, never another application's UI.
// Managed TrayIcon.IsVisible does not prove that AppKit can display its image.
internal static class MacTrayProbe
{
    internal static byte[] ReadApplicationIcon()
    {
        if (!OperatingSystem.IsMacOS() || Program.Headless) return [];
        var app = Send(Class("NSApplication"), Sel("sharedApplication"));
        var image = Send(app, Sel("applicationIconImage"));
        if (image == 0) return [];
        var tiff = Send(image, Sel("TIFFRepresentation"));
        var representation = SendObject(Class("NSBitmapImageRep"), Sel("imageRepWithData:"), tiff);
        var data = Png(representation, Sel("representationUsingType:properties:"), 4, 0);
        var length = (int)Send(data, Sel("length"));
        if (length <= 0 || length > 20_000_000) return [];
        var bytes = new byte[length];
        Marshal.Copy(Send(data, Sel("bytes")), bytes, 0, length);
        return bytes;
    }

    internal sealed record Item(double Width, double Height, double ImageWidth, double ImageHeight, bool Template, bool Visible, int ColoredPixels);
    internal static List<Item> Read()
    {
        var result = new List<Item>();
        if (!OperatingSystem.IsMacOS() || Program.Headless) return result;
        var windows = Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("windows"));
        for (nuint i = 0, count = (nuint)Send(windows, Sel("count")); i < count; i++)
            Visit(Send(SendAt(windows, Sel("objectAtIndex:"), i), Sel("contentView")), 0);
        return result;

        void Visit(nint view, int depth)
        {
            if (view == 0 || depth > 12) return;
            if (Kind(view, Sel("isKindOfClass:"), Class("NSStatusBarButton")))
            {
                var frame = Rect(view, Sel("frame"));
                var image = Send(view, Sel("image"));
                var size = image == 0 ? default : Size(image, Sel("size"));
                var colored = 0;
                if (image != 0)
                {
                    var tiff = Send(image, Sel("TIFFRepresentation"));
                    var representation = SendObject(Class("NSBitmapImageRep"), Sel("imageRepWithData:"), tiff);
                    var data = Png(representation, Sel("representationUsingType:properties:"), 4, 0);
                    var length = (int)Send(data, Sel("length"));
                    if (length > 0 && length < 1_000_000)
                    {
                        var bytes = new byte[length]; Marshal.Copy(Send(data, Sel("bytes")), bytes, 0, length);
                        File.WriteAllBytes(Path.Combine(Program.Output, $"native-tray-image-{result.Count}.png"), bytes);
                        using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
                        if (bitmap != null) colored = bitmap.Pixels.Count(p => p.Alpha > 200
                            && Math.Max(p.Red, Math.Max(p.Green, p.Blue)) - Math.Min(p.Red, Math.Min(p.Green, p.Blue)) > 40);
                    }
                }
                result.Add(new(frame.Width, frame.Height, size.Width, size.Height,
                    image != 0 && Bool(image, Sel("isTemplate")), Bool(Send(view, Sel("window")), Sel("isVisible")), colored));
            }
            var children = Send(view, Sel("subviews"));
            for (nuint i = 0, count = (nuint)Send(children, Sel("count")); i < count; i++)
                Visit(SendAt(children, Sel("objectAtIndex:"), i), depth + 1);
        }
    }

    private const string Library = "/usr/lib/libobjc.dylib";
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public double Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public double X, Y, Width, Height; }
    [DllImport(Library, EntryPoint = "objc_getClass")] private static extern nint Class(string name);
    [DllImport(Library, EntryPoint = "sel_registerName")] private static extern nint Sel(string name);
    [DllImport(Library, EntryPoint = "objc_msgSend")] private static extern nint Send(nint obj, nint selector);
    [DllImport(Library, EntryPoint = "objc_msgSend")] private static extern nint SendAt(nint obj, nint selector, nuint index);
    [DllImport(Library, EntryPoint = "objc_msgSend")] private static extern nint SendObject(nint obj, nint selector, nint value);
    [DllImport(Library, EntryPoint = "objc_msgSend")] private static extern nint Png(nint obj, nint selector, nuint type, nint properties);
    [DllImport(Library, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool Kind(nint obj, nint selector, nint type);
    [DllImport(Library, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool Bool(nint obj, nint selector);
    [DllImport(Library, EntryPoint = "objc_msgSend")] private static extern NativeRect Rect(nint obj, nint selector);
    [DllImport(Library, EntryPoint = "objc_msgSend")] private static extern NativeSize Size(nint obj, nint selector);
}
