using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DialogHostAvalonia;
using ServiceLib.Manager;
using ServiceLib.Enums;
using ServiceLib.Helper;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task ReviewWorkspaceV5Async()
    {
        await _main.NavigateAsync("Home");
        _main.Width = 440; _main.Height = 620;
        _vm.BlReloadEnabled = true;
        var bar = (StatusBarView)_main.FindControl<ContentControl>("contentStatusBarView")!.Content!;
        var drawer = _main.ServerDrawer!;
        drawer.ReduceMotion = () => false;
        bar.SetReloading(false);
        await Task.Delay(60);
        var button = bar.FindControl<Button>("btnConnect")!;
        var search = bar.FindControl<TextBox>("txtServerSearch")!;
        var list = bar.FindControl<StackPanel>("serverChoices")!;
        var previousNode = AppManager.Instance.Config.IndexId;
        var previousTun = _vm.StatusBarViewModel.EnableTun;
        var originalSize = _main.ClientSize;
        var originalPosition = _main.Position;
        var screen = _main.Screens.ScreenFromWindow(_main) ?? _main.Screens.Primary;
        var frameWidth = Math.Max(0, (_main.FrameSize ?? _main.ClientSize).Width - _main.ClientSize.Width);
        var planned = ConnectionDrawer.Plan(originalSize.Width, originalPosition, screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080), screen?.Scaling ?? 1, frameWidth);
        var buttonRect = button.Bounds;
        var buttonPoint = button.TranslatePoint(default, _main);
        Check(!bar.IsServerPickerOpen && !bar.FindControl<Border>("serverPicker")!.IsVisible,
            "default compact home has no server list");
        Click("btnConnectionControls");
        await Task.Delay(50);
        Check(button.Bounds == buttonRect && button.TranslatePoint(default, _main) == buttonPoint,
            "connection content keeps its exact geometry during a native window resize");
        await Settled();
        Check(_main.Width == 790 && _main.Height == originalSize.Height, "picker expands the same window 350 points to the right");
        Check(_main.Position == planned.Expanded, "native window expansion uses the planned screen-safe anchor");
        Check(button.Bounds == buttonRect && button.TranslatePoint(default, _main) == buttonPoint,
            $"connection card and main button keep their width and position while expanded: before={buttonRect} at {buttonPoint}; after={button.Bounds} at {button.TranslatePoint(default, _main)}; client={originalSize} -> {_main.ClientSize}");
        Check(bar.FindControl<Border>("serverPicker")!.TranslatePoint(default, _main)!.Value.X >= 440,
            "server selection is on the right, not below or covering the connection");
        Check(search.IsKeyboardFocusWithin, "expanded picker focuses search");
        var manage = bar.FindControl<Button>("btnManageServers")!;
        var manageAt = manage.TranslatePoint(default, _main)!.Value;
        Check(manageAt.Y + manage.Bounds.Height <= _main.ClientSize.Height, "management action stays visible below the independently scrolling list");
        await CaptureAsync("v5-drawer-light", "Right-side server picker", _main);

        search.Text = "no-such-node-for-review";
        await Task.Delay(30);
        Check(list.Children.Count == 0 && bar.FindControl<TextBlock>("txtPickerEmpty")!.IsVisible && manage.IsEffectivelyVisible,
            "empty search retains an explanation and server management");
        search.Text = "";
        await Task.Delay(30);
        Check(list.Children.Count > 0, "clearing search restores server choices");
        bar.FindControl<ComboBox>("pickerScope")!.SelectedIndex = 1;
        Check(list.Children.Count == 0, "empty favorites do not fall back to an unrequested list");
        bar.FindControl<ComboBox>("pickerScope")!.SelectedIndex = 0;
        _main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await Settled();
        Check(_main.ClientSize == originalSize && !bar.IsServerPickerOpen, "Escape restores the compact window size");
        Check(_main.Position == originalPosition, "collapse restores the original native window anchor");
        Check(bar.FindControl<Button>("btnConnectionControls")!.IsKeyboardFocusWithin, "collapse returns keyboard focus to the entry point");

        Click("btnConnectionControls"); await Task.Delay(50);
        var duringOpen = _main.Width;
        Click("btnConnectionControls");
        Check(Math.Abs(_main.Width - duringOpen) < 1, "reversing animation starts at the current width without a jump");
        await Task.Delay(40); Click("btnConnectionControls"); await Settled();
        Check(_main.Width == 790 && bar.IsServerPickerOpen, "rapid open-close-open settles at one expanded width");
        Click("btnCloseConnection"); await Settled();
        Check(_main.Width == 440 && !bar.FindControl<Border>("serverPicker")!.IsVisible, "collapse button hides the drawer after its closing animation");
        Click("btnConnectionControls"); await Settled();
        Click("btnCloseConnection");
        var host = _main.GetVisualDescendants().OfType<DialogHost>().Single();
        var authorization = new SudoPasswordInputView((_, _) => Task.FromResult(false)) { DialogIdentifier = host.Identifier };
        var pendingAuthorization = DialogHost.Show(authorization, host.Identifier);
        await Task.Delay(80);
        var password = authorization.FindControl<TextBox>("txtPassword")!;
        password.Focus();
        await Settled();
        Check(password.IsKeyboardFocusWithin, "closing animation never steals focus from a new authorization dialog");
        authorization.FindControl<Button>("btnCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pendingAuthorization;
        Check(AppManager.Instance.Config.IndexId == previousNode && _vm.StatusBarViewModel.EnableTun == previousTun
            && !await CoreManager.Instance.IsCoreRunningAsync(), "opening, searching and closing never change the selected mode, node or running core");

        drawer.ReduceMotion = () => true;
        Click("btnConnectionControls");
        Check(!drawer.IsAnimating && _main.Width == 790, "reduced motion applies the final layout without a resize animation");
        await Task.Delay(60);
        _main.ShowHideWindow(false);
        Check(_main.Width == 440 && !bar.IsServerPickerOpen, "hiding the connection window clears expansion before storing its size");
        _main.ShowHideWindow(true); await Task.Delay(80);

        var edge = ConnectionDrawer.Plan(440, new PixelPoint(900, 40), new PixelRect(0, 0, 1280, 800), 1, 0);
        Check(!edge.Narrow && edge.Expanded.X == 490 && edge.Origin.X == 900,
            "right-edge expansion shifts only enough to fit the available screen");
        var retina = ConnectionDrawer.Plan(440, new PixelPoint(-200, 40), new PixelRect(-1920, 0, 1920, 1080), 2, 0);
        Check(retina.Expanded.X + (retina.Width + retina.Panel) * 2 <= 0, "screen bounds account for Retina scaling and negative monitor coordinates");
        Check(ConnectionDrawer.Plan(440, default, new PixelRect(0, 0, 720, 900), 1, 0).Narrow,
            "a narrow work area uses a same-window selection page instead of clipping controls");
        _main.CanResize = false;
        Click("btnConnectionControls"); await Task.Delay(60);
        Check(_main.Width == 440 && !bar.FindControl<Grid>("connectionBody")!.IsVisible
            && bar.FindControl<Border>("serverPicker")!.Bounds.Width == 440, "fixed-size window uses the accessible narrow-screen fallback");
        await CaptureAsync("v5-drawer-narrow", "Same-window narrow fallback", _main);
        Click("btnCloseConnection");
        Check(bar.FindControl<Grid>("connectionBody")!.IsVisible, "narrow fallback has a working return action");
        _main.CanResize = true;

        foreach (var dark in new[] { false, true })
        {
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            await Task.Delay(60);
            var classes = (IPseudoClasses)button.Classes;
            var bounds = button.Bounds;
            foreach (var (pseudo, resource) in new[] { ("", "ConnectionAccent"), (":pointerover", "ConnectionAccentHover"), (":pressed", "ConnectionAccentPressed") })
            {
                classes.Set(":pointerover", pseudo != ""); classes.Set(":pressed", pseudo == ":pressed");
                _main.UpdateLayout();
                var face = button.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
                var expected = ((ISolidColorBrush)button.FindResource(button.ActualThemeVariant, resource)!).Color;
                Check(face.Background is ISolidColorBrush b && b.Color == expected && button.Bounds == bounds,
                    $"{(dark ? "dark" : "light")} {pseudo}: hover and press color fill the actual template without changing geometry");
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)button.Bounds.Width, (int)button.Bounds.Height), new Vector(96, 96));
                bitmap.Render(button);
                using var encoded = new MemoryStream();
                bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
                using var decoded = SkiaSharp.SKBitmap.Decode(encoded.ToArray());
                var pixel = decoded.GetPixel(10, 12);
                Check(pixel.Red == expected.R && pixel.Green == expected.G && pixel.Blue == expected.B && pixel.Alpha == 255,
                    "rendered primary face stays opaque and blue through the interaction state: " + pseudo + " "
                    + pixel + " expected=" + expected);
            }
            classes.Set(":pressed", false);
            await CaptureAsync("v5-hover-" + (dark ? "dark" : "light"), "Primary hover state", _main);
            classes.Set(":pointerover", false);
            button.IsEnabled = false;
            Check(!button.IsEnabled && button.Bounds == bounds, "disabled connection action preserves its geometry");
            button.IsEnabled = true;
            Click("btnConnectionControls"); await Task.Delay(50);
            if (dark) await CaptureAsync("v5-drawer-dark", "Dark right-side picker", _main);
            Click("btnCloseConnection");
        }
        RequestedThemeVariant = ThemeVariant.Light;
        foreach (var dark in new[] { false, true }) foreach (var scale in new[] { 1, 2 })
        {
            var hashes = new HashSet<string>();
            foreach (var p in Enum.GetValues<TrayState>()) foreach (var t in Enum.GetValues<TrayState>())
            {
                using var bitmap = WorkspaceTray.RenderIcon(p, t, scale, dark);
                using var stream = new MemoryStream(); bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                hashes.Add(Convert.ToHexString(SHA256.HashData(stream.ToArray())));
                File.WriteAllBytes(Path.Combine(Program.Output, $"v5-tray-{dark}-{scale}x-{p}-{t}.png"), stream.ToArray());
                Check(bitmap.Size.Width == bitmap.Size.Height && ReadPixel(bitmap, new Point(scale, 9 * scale))[3] > 0
                    && ReadPixel(bitmap, new Point(10 * scale, 9 * scale))[3] > 0, "square tray retains both P/T tiles at scale " + scale);
                using var decoded = SkiaSharp.SKBitmap.Decode(stream.ToArray());
                var pixels = decoded.Pixels;
                bool Has(string color) => pixels.Contains(SkiaSharp.SKColor.Parse(color));
                Check(Has("#126CE5") == (p == TrayState.On) && Has("#0D8057") == (t == TrayState.On)
                    && Has("#9D5B10") == (p == TrayState.Unknown || t == TrayState.Unknown),
                    $"color image independently encodes actual proxy {p} and TUN {t}, dark={dark}, scale={scale}");
                Check(pixels.Any(c => c.Alpha == 0) && pixels.Any(c => c.Alpha == 255), "tray image preserves transparent surroundings and opaque marks");
            }
            Check(hashes.Count == 9, "all nine proxy/TUN state combinations remain visually distinct at scale " + scale);
        }
        if (OperatingSystem.IsMacOS() && !Program.Headless)
        {
            using var icon = new TrayIcon { IsVisible = false };
            using var tray = new WorkspaceTray(icon, _ => Task.FromResult(new TraySnapshot(TrayState.On, TrayState.On, ProxyState.Local)),
                () => false, () => true, () => ESysProxyType.Unchanged, _ => Task.CompletedTask, false);
            await tray.RefreshAsync(); icon.IsVisible = true;
            await Task.Delay(80);
            var native = MacTrayProbe.Read().Where(i => i.Visible).ToList();
            File.WriteAllText(Path.Combine(Program.Output, "native-color-tray.json"), System.Text.Json.JsonSerializer.Serialize(native));
            Check(native.Any(i => !i.Template && i.ImageWidth <= i.Width && i.ImageHeight <= i.Height && i.ColoredPixels > 20),
                "visible AppKit status item retains colored pixels and fits the native square slot");
        }
        Check(!await CoreManager.Instance.IsCoreRunningAsync(), "V5 regression never starts a live core");
        await ReviewPickerSelectionAsync(bar);

        void Click(string name) => bar.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Settled()
        {
            for (var i = 0; i < 100 && drawer.IsAnimating; i++) await Task.Delay(10);
            await Task.Delay(40);
            _main.UpdateLayout();
            Check(!drawer.IsAnimating, "drawer animation completes within its bounded duration");
        }
    }

    private async Task ReviewPickerSelectionAsync(StatusBarView bar)
    {
        var config = AppManager.Instance.Config;
        var originalId = config.IndexId;
        var originalFavorites = config.UiItem.ConnectionFavorites.ToList();
        var search = bar.FindControl<TextBox>("txtServerSearch")!;
        var scope = bar.FindControl<ComboBox>("pickerScope")!;
        var scroll = bar.FindControl<ScrollViewer>("serverListScroll")!;
        var list = bar.FindControl<StackPanel>("serverChoices")!;
        var drawer = _main.ServerDrawer!;
        try
        {
            for (var i = 0; i < 80; i++)
            {
                var node = Profile(EConfigType.SSH);
                node.IndexId = $"review-picker-{i:000}";
                node.Remarks = $"定位测试 {i:000} · 长名称服务器 · 不建立真实连接";
                node.Address = $"picker-{i}.example.com";
                await SQLiteHelper.Instance.ReplaceAsync(node);
                ProfileExManager.Instance.SetSort(node.IndexId, 100 + i);
            }
            config.IndexId = "review-picker-070";
            config.UiItem.ConnectionFavorites = ["review-picker-000"];
            search.Text = "a-previous-search-that-hides-the-selection";
            scope.SelectedIndex = 1;
            drawer.ReduceMotion = () => false;
            await OpenAndCheck();
            Check(string.IsNullOrEmpty(search.Text) && scope.SelectedIndex == 0,
                "opening clears stale search and favorites scope so the selected server is discoverable");
            Check(scroll.Offset.Y > 0, "a selected server below the first screen is automatically scrolled into view");
            Check(search.IsKeyboardFocusWithin, "automatic positioning keeps keyboard focus in search");
            await CaptureAsync("picker-selected-below-fold", "Selected server revealed on opening", _main);

            scroll.Offset = default;
            await Task.Delay(80);
            Check(scroll.Offset.Y == 0, "manual scrolling after opening is not pulled back to the selected server");
            search.Text = "picker-0.example.com";
            await Task.Delay(40);
            Check(list.Children.Count == 1 && !list.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("selectedServer")),
                "subsequent search respects the user's filter rather than forcing the selected row into results");
            bar.CloseConnectionControls(); await Settle();
            drawer.ReduceMotion = () => true;
            await OpenAndCheck();
            Check(scroll.Offset.Y > 0, "reopening after a filtered search relocates the selected server with reduced motion");

            bar.CloseConnectionControls(); await Settle();
            config.IndexId = "review-picker-010";
            _main.CanResize = false;
            await OpenAndCheck();
            Check(list.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("selectedServer")).Name == "select-review-picker-010",
                "narrow fallback locates the current selection instead of remembering the previously selected row");
            bar.CloseConnectionControls(); await Settle(); _main.CanResize = true;

            config.IndexId = "review-picker-missing";
            bar.FindControl<Button>("btnConnectionControls")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Check(list.Children.Count >= 80 && scroll.Offset.Y == 0
                && !list.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("selectedServer")),
                "a deleted or missing selection leaves the available server list at the top");
            Check(!await CoreManager.Instance.IsCoreRunningAsync(), "revealing a selected server never starts a core");
        }
        finally
        {
            bar.CloseConnectionControls(); await Settle();
            _main.CanResize = true;
            config.IndexId = originalId;
            config.UiItem.ConnectionFavorites = originalFavorites;
            await SQLiteHelper.Instance.ExecuteAsync("delete from ProfileItem where IndexId like 'review-picker-%'");
        }

        async Task Settle()
        {
            for (var i = 0; i < 100 && (drawer.IsAnimating || bar.IsServerPickerOpen && list.Children.Count < 80); i++)
                await Task.Delay(10);
            await Task.Delay(100);
            _main.UpdateLayout();
        }
        async Task OpenAndCheck()
        {
            bar.FindControl<Button>("btnConnectionControls")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var selected = list.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "select-" + config.IndexId);
            var position = selected.TranslatePoint(default, scroll)!.Value;
            Check(selected.Classes.Contains("selectedServer") && position.Y >= -.5
                && position.Y + selected.Bounds.Height <= scroll.Viewport.Height + .5,
                $"selected row is highlighted and completely within the actual viewport: y={position.Y}, height={selected.Bounds.Height}, viewport={scroll.Viewport.Height}, offset={scroll.Offset}, extent={scroll.Extent}");
        }
    }
}
