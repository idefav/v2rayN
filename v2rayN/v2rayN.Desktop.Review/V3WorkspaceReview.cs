using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Handler;
using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using ServiceLib.ViewModels;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task ReviewWorkspaceV3Async()
    {
        Check(!SysProxyHandler.ChangesEnabled && !AutoStartupHandler.ChangesEnabled && _vm.DesignMode,
            "review cannot launch a proxy or mutate OS proxy or autostart settings");
        await CheckConnectionHomeAsync();
        var status = (StatusBarView)_main.FindControl<ContentControl>("contentStatusBarView")!.Content!;
        Check(_main.FindControl<Control>("sidebar") == null && _main.FindControl<Control>("navigation") == null,
            "connection home has no legacy navigation rail");
        Check(!_main.WorkspaceWindow.GetVisualDescendants().OfType<DataGrid>().Any(c => c.IsEffectivelyVisible), "server table is absent from the default home");
        Check(status.FindControl<Button>("btnRouting")!.IsEffectivelyVisible && status.FindControl<ToggleSwitch>("togEnableTun")!.IsEffectivelyVisible,
            "home retains route navigation and TUN choice");
        status.FindControl<Button>("btnConnectionControls")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        status.FindControl<TextBox>("txtServerSearch")!.Text = "";
        var choices = status.FindControl<StackPanel>("serverChoices")!;
        var favorites = choices.GetVisualDescendants().OfType<Button>().First(b => b.Name?.StartsWith("favorite-") == true);
        favorites.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(150);
        status.FindControl<ComboBox>("pickerScope")!.SelectedIndex = 1;
        Check(choices.Children.Count == 1 && AppManager.Instance.Config.UiItem.ConnectionFavorites.Count == 1,
            "favorite action persists a real preference and filters the inline picker");
        choices.GetVisualDescendants().OfType<Button>().First(b => b.Name?.StartsWith("favorite-") == true)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(150);
        Check(choices.Children.Count == 0 && status.FindControl<TextBlock>("txtPickerEmpty")!.IsVisible,
            "removing the last favorite presents an empty state without selecting another server");
        status.FindControl<ComboBox>("pickerScope")!.SelectedIndex = 0;
        status.CloseConnectionControls();

        var configuredNode = AppManager.Instance.Config.IndexId;
        AppManager.Instance.Config.IndexId = "";
        status.SetReloading(false);
        var manage = status.ManageServersRequested;
        var requestedManagement = 0;
        status.ManageServersRequested = () => { requestedManagement++; return Task.CompletedTask; };
        status.FindControl<Button>("btnConnect")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(50);
        Check(requestedManagement == 1, "connecting with no selected node opens server management without starting a core");
        status.ManageServersRequested = manage;
        AppManager.Instance.Config.IndexId = configuredNode;
        status.SetReloading(false);

        // A choice made while disconnected must not enter a connection transaction.
        var transition = _vm.StatusBarViewModel.Transition;
        await (Task)typeof(MainWindowViewModel).GetMethod("ReloadSelectedConfigurationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_vm, new object[] { "UI fixture selection", false })!;
        Check(_vm.StatusBarViewModel.HasPendingChanges && ReferenceEquals(transition, _vm.StatusBarViewModel.Transition),
            "disconnected configuration selection remains pending and does not start a transaction");

        _main.FindControl<Button>("btnSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        Check(_main.CurrentWorkspacePage == "Servers", "settings remembers the last visited page");
        Check(_main.FindControl<Control>("homeBrand")!.IsEffectivelyVisible
            && _main.SettingsWindow?.IsVisible == true, "settings opens independently while the connection window remains available");

        // The full production pages are instantiated, not blank navigation placeholders.
        foreach (var entry in WorkspacePages.Available)
        {
            await _main.OpenSettingsAsync(entry.Key);
            await Task.Delay(80);
            var body = _main.WorkspaceWindow.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "settingsBody");
            Check(_main.CurrentWorkspacePage == entry.Key && body.Content is Control content && content.Bounds.Height > 0,
                $"settings entry {entry.Key} opens its real content");
            Check(_main.WorkspaceWindow.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "nav" + entry.Key && b.Classes.Contains("selected")),
                $"settings entry {entry.Key} retains a selected navigation item");
            if (entry.Key == "Servers") Check(body.GetVisualDescendants().OfType<DataGrid>().Any(), "complete server management table is retained");
            if (entry.Key == "Tun") Check(((TunSettingsWindow)body.Content!).ViewModel!.DeferApply, "TUN settings save defers activation to the connection page");
            await CaptureAsync("v3-settings-" + entry.Key.ToLowerInvariant(), entry.Title, _main);
        }
        Check(_main.SettingsPageKeys.Count == WorkspacePages.Available.Count(), "all platform settings have visible entries");
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        Check(desktop.Windows.Count(w => w.IsVisible) == 2, "settings navigation reuses one separate window");

        var search = _main.FindControl<TextBox>("settingsSearch")!;
        search.Focus(); search.Text = "DNS";
        await Task.Delay(50);
        var matches = _main.WorkspaceWindow.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "settingsSearchResults");
        Check(matches.IsVisible && matches.GetVisualDescendants().OfType<Button>().Any(b => (string?)b.Tag == "DNS"), "settings search finds DNS by keyword");
        Check(ReferenceEquals(_main.WorkspaceWindow.FocusManager?.GetFocusedElement(), search), "search rendering preserves keyboard focus");
        matches.GetVisualDescendants().OfType<Button>().First(b => (string?)b.Tag == "DNS").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        Check(_main.CurrentWorkspacePage == "DNS" && string.IsNullOrEmpty(search.Text), "search result navigates and dismisses results");
        search.Text = "sg.example.com";
        await Task.Delay(50);
        Check(!matches.GetVisualDescendants().OfType<Button>().Any(), "settings search does not search private server records");
        search.Text = "";

        await CheckDraftNavigationAsync();
        await _main.OpenSettingsAsync("Core");
        var shortcutEditor = _main.WorkspaceWindow.GetLogicalDescendants().OfType<OptionSettingWindow>().Single();
        shortcutEditor.ViewModel!.LocalPort++;
        var shortcutPort = shortcutEditor.ViewModel.LocalPort;
        shortcutEditor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.S, KeyModifiers = KeyModifiers.Meta });
        for (var i = 0; i < 50 && _main.DirtyDraftFields().Any(); i++) await Task.Delay(20);
        Check(AppManager.Instance.Config.Inbound[0].LocalPort == shortcutPort && !_main.DirtyDraftFields().Any() && _main.IsVisible,
            "Command-S saves the settings draft without hiding the window or capturing the screen");
        await _main.OpenSettingsAsync("Application");
        var beforeChoice = _main.WorkspaceWindow.GetLogicalDescendants().OfType<OptionSettingWindow>().Single().ViewModel!;
        var savedSpeed = AppManager.Instance.Config.GuiItem.DisplayRealTimeSpeed;
        await _main.NavigateAsync("Home");
        AppManager.Instance.Config.GuiItem.DisplayRealTimeSpeed = !savedSpeed;
        await _main.OpenSettingsAsync("Application");
        var afterChoice = _main.WorkspaceWindow.GetLogicalDescendants().OfType<OptionSettingWindow>().Single().ViewModel!;
        Check(!ReferenceEquals(beforeChoice, afterChoice) && afterChoice.DisplayRealTimeSpeed == !savedSpeed,
            "reopened settings load a home switch change rather than overwriting it from a cached editor");
        await _main.NavigateAsync("Home");
        AppManager.Instance.Config.GuiItem.DisplayRealTimeSpeed = savedSpeed;
        await _main.OpenSettingsAsync("Appearance");
        var appearance = _main.WorkspaceWindow.GetLogicalDescendants().OfType<ThemeSettingView>().Single();
        var theme = appearance.ViewModel!;
        var savedTheme = AppManager.Instance.Config.UiItem.CurrentTheme;
        theme.CurrentTheme = savedTheme == "Dark" ? "Light" : "Dark";
        await Task.Delay(50);
        Check(AppManager.Instance.Config.UiItem.CurrentTheme == savedTheme, "appearance preview does not write the stored preference");
        var leaving = _main.NavigateAsync("Home");
        await ChooseDraftAsync(WorkspaceText.Stay); await leaving;
        Check(_main.CurrentWorkspacePage == "Appearance", "cancel leave retains appearance draft");
        leaving = _main.NavigateAsync("Home");
        await ChooseDraftAsync(WorkspaceText.Discard); await leaving;
        Check(_main.CurrentWorkspacePage == "Home" && AppManager.Instance.Config.UiItem.CurrentTheme == savedTheme
            && theme.CurrentTheme == savedTheme, "discard restores appearance and returns home");

        // Long and empty states are rendered by the production control with fixture values.
        var name = _vm.StatusBarViewModel.RunningServerDisplay;
        _vm.StatusBarViewModel.RunningServerDisplay = "[SSH] 新加坡跨区域专线 · 上海到香港再到新加坡 · 长名称测试节点（中文与 English name）";
        var configuredTheme = RequestedThemeVariant;
        foreach (var width in new[] { 800, 1000, 1400 })
        foreach (var dark in new[] { false, true })
        {
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            _main.Width = width; _main.Height = 900;
            await _main.NavigateAsync("Home");
            await Task.Delay(80); _main.UpdateLayout();
            Check(status.Bounds.Width <= width && status.FindControl<Border>("summaryCard")!.Bounds.Width <= 620,
                $"home fits {width}px in {(dark ? "dark" : "light")} theme");
            var connect = status.FindControl<Button>("btnConnect")!;
            Check(connect.Bounds.Width > 200 && connect.IsEffectivelyVisible, $"primary connection action remains visible at {width}px");
            await CaptureAsync($"v3-home-{width}-{(dark ? "dark" : "light")}", "V3 home · long node name", _main);
            await _main.OpenSettingsAsync("Application");
            _main.SettingsWindow!.Width = width;
            await Task.Delay(80); _main.SettingsWindow.UpdateLayout();
            var save = _main.WorkspaceWindow.GetVisualDescendants().OfType<Button>().First(b => b.Name == "btnSave");
            var point = save.TranslatePoint(default, _main.SettingsWindow)!.Value;
            await CaptureAsync($"v3-options-{width}-{(dark ? "dark" : "light")}", "V3 grouped settings", _main);
            Check(save.IsEffectivelyVisible && point.X >= 0 && point.X + save.Bounds.Width <= width + 1,
                $"settings save action remains within {width}px window (x={point.X}, w={save.Bounds.Width}, visible={save.IsEffectivelyVisible})");
        }
        RequestedThemeVariant = configuredTheme;
        await _main.NavigateAsync("Home");
        _vm.StatusBarViewModel.RunningServerDisplay = name;
        _vm.StatusBarViewModel.ConnectionError = "Fixture: listener occupied.\nSensitive full error remains in diagnostics.";
        await Task.Delay(80);
        Check(status.FindControl<TextBlock>("txtConnectionError")!.Text == "Fixture: listener occupied.", "home shows one concise error line");
        status.FindControl<Button>("btnConnectionDiagnostics")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(80);
        Check(_main.CurrentWorkspacePage == "Logs", "connection failure links to the real diagnostics page");
        _vm.StatusBarViewModel.ConnectionError = null;
        await _main.NavigateAsync("Home");

        await CheckWorkspaceRegressionsAsync();

        // No timers, navigation or appearance edits can accidentally enable a real core.
        Check(!await CoreManager.Instance.IsCoreRunningAsync() && !CoreManager.Instance.HasUnconfirmedProcesses,
            "isolated UI regression ends without any core process");
        Check(!SysProxyHandler.ChangesEnabled, "OS proxy mutation guard remains disabled throughout review");
    }
}
