using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DialogHostAvalonia;
using ServiceLib.Enums;
using ServiceLib.Base;
using ServiceLib.Common;
using ServiceLib.Manager;
using ReactiveUI.Primitives.Extensions;
using ReactiveUI.Primitives;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task ReviewWorkspaceV4Async()
    {
        await _main.NavigateAsync("Home");
        _vm.BlReloadEnabled = true;
        ((StatusBarView)_main.FindControl<ContentControl>("contentStatusBarView")!.Content!).SetReloading(false);
        _main.Width = 440; _main.Height = 620;
        await Task.Delay(100);
        await CaptureAsync("v4-connection", "Compact connection window", _main);
        var original = _main.ClientSize;
        await _main.OpenSettingsAsync("Application");
        var settings = _main.SettingsWindow!;
        Check(settings.IsVisible && _main.IsVisible && !ReferenceEquals(settings, _main), "connection and settings are independent visible windows");
        Check(settings.Width >= 800 && settings.Width <= 1120 && settings.Height >= 600 && settings.Height <= 780
            && _main.ClientSize == original, "settings opens large, clamped to the screen, without resizing connection");
        settings.Width = 1000; settings.Height = 740;
        await _main.OpenSettingsAsync("Routing");
        Check(ReferenceEquals(settings, _main.SettingsWindow) && settings.Width == 1000 && _main.Width == 440, "settings navigation reuses independent geometry");
        await CaptureAsync("v4-settings", "Independent settings workspace", settings);
        await _main.OpenSettingsAsync("Application");
        var editor = settings.GetLogicalDescendants().OfType<OptionSettingWindow>().Single().ViewModel!;
        editor.KeepOlderDedupl = !editor.KeepOlderDedupl;
        _main.ShowHideWindow(true);
        Check(settings.IsVisible && _main.DirtyDraftFields().Any(), "activating connection keeps the settings draft open");
        var speed = AppManager.Instance.Config.GuiItem.DisplayRealTimeSpeed;
        AppManager.Instance.Config.GuiItem.DisplayRealTimeSpeed = !speed;
        AppManager.Instance.Config.GuiItem.EnableStatistics = true;
        await editor.SaveCmd.Execute().ToTask();
        Check(AppManager.Instance.Config.GuiItem.DisplayRealTimeSpeed == !speed && AppManager.Instance.Config.GuiItem.EnableStatistics,
            "saving another setting preserves newer home speed and statistics choices");
        await _main.OpenSettingsAsync("Application");
        editor = settings.GetLogicalDescendants().OfType<OptionSettingWindow>().Single().ViewModel!;
        editor.KeepOlderDedupl = !editor.KeepOlderDedupl;
        settings.Close();
        await ChooseDraftAsync(WorkspaceText.Stay);
        await Task.Delay(50);
        Check(settings.IsVisible && _main.DirtyDraftFields().Any(), "canceling settings close keeps the draft and window");
        settings.Close();
        await ChooseDraftAsync(WorkspaceText.Discard);
        await Task.Delay(50);
        Check(!settings.IsVisible && _main.IsVisible, "discard and close hides only settings");
        await _main.OpenSettingsAsync("Application");
        Check(ReferenceEquals(settings, _main.SettingsWindow) && settings.Width == 1000, "reopen retains the settings window and size");
        await _main.NavigateAsync("Home");
        var host = _main.GetVisualDescendants().OfType<DialogHost>().Single();
        _main.ShowHideWindow(false);
        var hiddenAuthorization = _vm.StatusBarViewModel.PasswordInputInteraction.HandleSafe(RxVoid.Default).ToTask();
        await Task.Delay(100);
        Check(_main.IsVisible && host.IsOpen, "authorization requested from the hidden connection window reopens its visible owner");
        var hiddenView = host.GetVisualDescendants().OfType<SudoPasswordInputView>().Single();
        hiddenView.FindControl<Button>("btnCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await hiddenAuthorization;
        foreach (var dark in new[] { false, true })
        {
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            _main.Width = 360; _main.Height = 480;
            var completion = new TaskCompletionSource<bool>(); var calls = 0;
            var view = new SudoPasswordInputView((_, _) => { calls++; return completion.Task; }) { DialogIdentifier = host.Identifier };
            var focusEvents = new List<string>();
            view.AttachedToVisualTree += (_, _) => focusEvents.Add("attached");
            view.DetachedFromVisualTree += (_, _) => focusEvents.Add("detached");
            view.Loaded += (_, _) => focusEvents.Add("loaded");
            view.FindControl<TextBox>("txtPassword")!.GotFocus += (_, _) => focusEvents.Add("password focused");
            var pending = DialogHost.Show(view, host.Identifier);
            await Task.Delay(120);
            Check(host.Background is ISolidColorBrush { Color.A: 255, Opacity: 1 }, "authorization surface is opaque in " + (dark ? "dark" : "light"));
            var input = view.FindControl<TextBox>("txtPassword")!;
            // Native activation and opening animation are asynchronous. Wait for
            // readiness with a bound instead of assuming a 120 ms render deadline.
            for (var attempt = 0; !input.IsKeyboardFocusWithin && attempt < 40; attempt++) await Task.Delay(25);
            Check(input.IsKeyboardFocusWithin, "authorization initially focuses password input (actual=" + _main.FocusManager?.GetFocusedElement()?.GetType().Name
                + ", events=" + string.Join(",", focusEvents) + ", closed=" + typeof(SudoPasswordInputView).GetField("_closed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view) + ")");
            var popup = view.GetVisualAncestors().OfType<DialogOverlayPopupHost>().Single();
            popup.Focus();
            await Task.Delay(30);
            Check(input.IsKeyboardFocusWithin, "native popup activation restores the password focus");
            view.FindControl<Button>("btnCancel")!.Focus();
            await Task.Delay(30);
            Check(view.FindControl<Button>("btnCancel")!.IsKeyboardFocusWithin,
                "password focus restoration does not steal focus from dialog actions");
            foreach (var name in new[] { "btnSave", "btnCancel" })
            {
                var button = view.FindControl<Button>(name)!; var at = button.TranslatePoint(default, _main)!.Value;
                Check(at.X >= 0 && at.Y >= 0 && at.X + button.Bounds.Width <= _main.ClientSize.Width && at.Y + button.Bounds.Height <= _main.ClientSize.Height,
                    "authorization action fits the smallest window: " + name);
            }
            input.Text = "fixture-only-not-a-password";
            view.FindControl<Button>("btnSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.FindControl<Button>("btnSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(calls == 1, "duplicate authorization submits share one verification");
            completion.SetResult(false); await Task.Delay(40);
            Check(view.FindControl<TextBlock>("txtAuthorizationError")!.IsVisible && host.IsOpen, "incorrect authorization is inline and retryable");
            await CaptureAsync("v4-authorization-" + (dark ? "dark" : "light"), "Opaque authorization in minimum window", _main);
            view.FindControl<Button>("btnCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(await pending == null && input.Text == "", "cancel clears password and returns no authorization");
        }
        RequestedThemeVariant = ThemeVariant.Light;
        _main.Width = 440; _main.Height = 620;
        var late = new TaskCompletionSource<bool>();
        var canceled = new SudoPasswordInputView((_, _) => late.Task) { DialogIdentifier = host.Identifier };
        var cancelTask = DialogHost.Show(canceled, host.Identifier); await Task.Delay(50);
        canceled.FindControl<TextBox>("txtPassword")!.Text = "fixture";
        canceled.FindControl<Button>("btnSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        canceled.FindControl<Button>("btnCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await cancelTask;
        var next = DialogHost.Show(new TextBlock { Text = "New dialog" }, host.Identifier); await Task.Delay(50);
        late.SetResult(true); await Task.Delay(50);
        Check(host.IsOpen, "late canceled authorization cannot close a newer dialog");
        DialogHost.Close(host.Identifier); await next;

        var selectedTun = false; var busy = false; var actions = new List<string>();
        var snapshot = new TraySnapshot(TrayState.On, TrayState.On, ProxyState.Off, "Fixture node");
        using var icon = new TrayIcon { IsVisible = false };
        var existingMenu = new NativeMenu();
        var legacyItem = new NativeMenuItem("Legacy command");
        existingMenu.Items.Add(legacyItem);
        icon.Menu = existingMenu;
        using var tray = new WorkspaceTray(icon, _ => Task.FromResult(snapshot), () => busy, () => selectedTun,
            () => ESysProxyType.ForcedChange, id => { actions.Add(id); return Task.CompletedTask; }, false);
        Check(ReferenceEquals(existingMenu, icon.Menu) && ReferenceEquals(existingMenu, tray.Menu),
            "tray preserves the identity of an already exported native menu");
        Check(!tray.Menu.Items.Contains(legacyItem), "tray removes legacy entries without replacing the native root");
        using (var freshIcon = new TrayIcon { IsVisible = false })
        using (var fresh = new WorkspaceTray(freshIcon, _ => Task.FromResult(snapshot), () => false, () => false,
            () => ESysProxyType.Unchanged, _ => Task.CompletedTask, false))
            Check(ReferenceEquals(freshIcon.Menu, fresh.Menu) && fresh.Menu.Items.Count > 0,
                "tray also initializes a new icon with no existing menu");
        await tray.RefreshAsync();
        Check(icon.ToolTipText!.Contains("未开启") && icon.ToolTipText.Contains("运行中"), "tray independently reports actual proxy off and actual TUN running");
        Check(tray.Menu.Items.OfType<NativeMenuItem>().Any(i => i.Header == "开启 TUN"), "tray TUN action follows choice while status follows actual runtime");
        selectedTun = true; snapshot = new(TrayState.Off, TrayState.Off, ProxyState.Local);
        await tray.RefreshAsync();
        Check(icon.ToolTipText!.Contains("指向本机") && icon.ToolTipText.Contains("未运行"), "system proxy on does not imply TUN running");
        var connect = tray.Menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "连接");
        connect.Command!.Execute(null); await Task.Delay(30);
        Check(actions.SequenceEqual(new[] { "connect" }), "tray invokes its shared connection action once");
        busy = true; await tray.RefreshAsync();
        connect.Command.Execute(null); await Task.Delay(30);
        Check(!connect.IsEnabled && actions.Count == 1, "busy tray guards actions even when invoked programmatically");
        Check(tray.Menu.Items.OfType<NativeMenuItem>().Any(i => i.Header == "打开设置…" && i.IsEnabled), "settings remains accessible during transitions");
        busy = false; await tray.RefreshAsync();
        static IEnumerable<NativeMenuItem> Commands(NativeMenu menu) => menu.Items.OfType<NativeMenuItem>()
            .SelectMany(item => item.Menu == null ? new[] { item } : Commands(item.Menu));
        actions.Clear();
        foreach (var item in Commands(tray.Menu).Where(item => item.Command != null))
        {
            item.Command!.Execute(null); await Task.Delay(10);
        }
        foreach (var id in new[] { "connect", "tun", "restart", "home", "settings", "copy", "import", "subscriptions", "subscriptions-proxy", "exit",
            "proxy:" + (int)ESysProxyType.ForcedChange, "proxy:" + (int)ESysProxyType.ForcedClear, "proxy:" + (int)ESysProxyType.Unchanged })
            Check(actions.Count(action => action == id) == 1, "tray retains a distinct wired action: " + id);
        foreach (var (raw, expected) in new[]
        {
            ("query failure", ProxyState.Unknown),
            ("<dictionary> {\n HTTPEnable : invalid\n}", ProxyState.Unknown),
            ("<dictionary> {\n}", ProxyState.Off),
            ("<dictionary> {\n HTTPEnable : 1\n HTTPProxy : 127.0.0.1\n HTTPPort : 10808\n}", ProxyState.Local),
            ("<dictionary> {\n ProxyAutoConfigEnable : 1\n}", ProxyState.Other),
            ("<dictionary> {\n HTTPEnable : 1\n HTTPProxy : other.example\n HTTPPort : 10808\n}", ProxyState.Other),
            ("<dictionary> {\n __SCOPED__ : <dictionary> {\n HTTPEnable : 1\n }\n}", ProxyState.Off)
        })
            Check(WorkspaceTray.ParseProxy(raw, 10808) == expected, "actual proxy parser: " + expected);
        foreach (var p in Enum.GetValues<TrayState>()) foreach (var t in Enum.GetValues<TrayState>())
        {
            using var bitmap = WorkspaceTray.RenderIcon(p, t);
            using var stream = File.Create(Path.Combine(Program.Output, $"tray-{p}-{t}.png"));
            bitmap.Save(stream);
            Check(stream.Length > 100 && bitmap.PixelSize == new PixelSize(36, 36), "tray renders a square color status glyph: " + p + "/" + t);
            Check(ReadPixel(bitmap, new Point(2, 18))[3] > 0 && ReadPixel(bitmap, new Point(20, 18))[3] > 0,
                "both P and T remain inside the rendered icon: " + p + "/" + t);
            using var preview = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(36, 36), new Vector(96, 96));
            using (var context = preview.CreateDrawingContext())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 36, 36));
                context.DrawImage(bitmap, new Rect(0, 0, 18, 18), new Rect(0, 0, 36, 36));
            }
            preview.Save(Path.Combine(Program.Output, $"tray-preview-{p}-{t}.png"));
        }
        Check(!await CoreManager.Instance.IsCoreRunningAsync(), "V4 verification never starts a real core");
    }
}
