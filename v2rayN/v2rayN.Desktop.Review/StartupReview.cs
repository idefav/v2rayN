using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using ServiceLib.Handler;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Manager;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task ReviewStartupAsync()
    {
        await _vm.Initialization.WaitAsync(TimeSpan.FromSeconds(10));
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        ReviewBrandAssets();
        var icon = TrayIcon.GetIcons(this)!.Single();
        Check(!_vm.DesignMode && !SysProxyHandler.ChangesEnabled && !AutoStartupHandler.ChangesEnabled,
            "production startup runs the real model with isolated fixtures and blocks OS mutations");
        Check(_vm.BlReloadEnabled, "production model initialization completes without leaving the UI busy");
        Check(_startupTrayMenu != null && ReferenceEquals(icon.Menu, _startupTrayMenu),
            "startup retains the XAML native menu root already exported by macOS");
        Check(icon.IsVisible && icon.Icon != null && _main.IsVisible,
            "production startup shows the connection window and status tray");
        var menu = icon.Menu!;
        Check(menu.Items.OfType<NativeMenuItem>().Count(i => i.Header == WorkspaceText.T("打开设置…", "Open settings…")) == 1,
            "startup replaces legacy entries with one settings action");
        Check(menu.Items.OfType<NativeMenuItem>().Count(i => i.Header == WorkspaceText.T("退出 v2rayN", "Quit v2rayN")) == 1,
            "startup retains one quit action");

        var heartbeats = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => heartbeats++;
        timer.Start();
        try
        {
            // Wait across multiple production tray refreshes. No connection action
            // is invoked; the only platform status query is read-only scutil.
            await Task.Delay(4500);
            var nativeTray = MacTrayProbe.Read();
            File.WriteAllText(Path.Combine(Program.Output, "native-tray.json"), JsonSerializer.Serialize(nativeTray));
            if (OperatingSystem.IsMacOS() && !Program.Headless)
                Check(nativeTray.Count == 1 && nativeTray.All(t => !t.Template && t.Visible && t.ImageWidth > 0 && t.ImageHeight > 0
                    && t.ImageWidth <= t.Width && t.ImageHeight <= t.Height),
                    "actual AppKit full-color image fits inside a visible native menu-bar button");
            Check(heartbeats > 5, "native dispatcher remains responsive across tray refreshes");
            Check(ReferenceEquals(icon.Menu, menu), "periodic status refresh keeps the same native menu root");
            var settings = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == WorkspaceText.T("打开设置…", "Open settings…"));
            settings.Command!.Execute(null);
            await Task.Delay(250);
            Check(_main.SettingsWindow is { IsVisible: true } && _main.IsVisible,
                "production tray action opens the independent settings window");
            await CaptureAsync("startup-settings", "Production startup · settings from native tray", _main.SettingsWindow!);
            await _main.NavigateAsync("Home");
            Check(_main.IsVisible && _main.SettingsWindow is { IsVisible: false },
                "returning to connection preserves the live startup window");
            await CaptureAsync("startup-connection", "Production startup · isolated connection window", _main);
            Check(!await CoreManager.Instance.IsCoreRunningAsync() && AppManager.Instance.ConnectionRecovery == null,
                "startup regression starts neither a core nor automatic recovery");
        }
        finally { timer.Stop(); }

        File.WriteAllText(Path.Combine(Program.Output, "manifest.json"), JsonSerializer.Serialize(new
        {
            fixtureData = true, productionStartup = true,
            renderer = Program.Headless ? "Avalonia.Headless + Skia" : "native",
            screenshots = _shots, checks = _checks
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {_checks.Count} production startup checks; {_shots.Count} screenshots.");
    }
}
