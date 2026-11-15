using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Services;
using ServiceLib.ViewModels;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task CheckWorkspaceRegressionsAsync()
    {
        var status = (StatusBarView)_main.FindControl<ContentControl>("contentStatusBarView")!.Content!;
        _vm.BlReloadEnabled = true;
        status.SetReloading(false);
        await _main.OpenSettingsAsync("Help");
        await Task.Delay(50);
        var controls = new[] { "btnConnect", "btnConnectionControls", "togEnableTun", "togRealTimeSpeed", "btnRouting" }
            .Select(name => status.FindControl<Control>(name)!).ToArray();
        Check(controls.All(c => c.IsEffectivelyEnabled) && !ReferenceEquals(TopLevel.GetTopLevel(controls[0]), _main.SettingsWindow),
            "connection controls remain usable in a separate top level");
        _main.FindControl<Button>("btnReturnConnection")!.Focus();
        if (Program.Headless)
        {
            foreach (var modifiers in new[] { RawInputModifiers.None, RawInputModifiers.Shift })
            for (var i = 0; i < 80; i++)
            {
                _main.SettingsWindow!.KeyPressQwerty(PhysicalKey.Tab, modifiers);
                _main.SettingsWindow.KeyReleaseQwerty(PhysicalKey.Tab, modifiers);
                Check(_main.WorkspaceWindow.FocusManager?.GetFocusedElement() is not Control focused || !focused.GetVisualAncestors().Contains(status),
                    $"settings Tab traversal excludes hidden home ({modifiers}, {i})");
            }
        }
        var savedName = _vm.StatusBarViewModel.RunningServerDisplay;
        _vm.StatusBarViewModel.RunningServerDisplay = "Hidden status subscription fixture";
        await Task.Delay(30);
        Check(status.FindControl<TextBlock>("txtRunningServerDisplay")!.Text == "Hidden status subscription fixture",
            "hidden home remains activated for runtime and authorization subscriptions");
        _vm.StatusBarViewModel.RunningServerDisplay = savedName;
        await _main.OpenSettingsAsync("Core");
        Check(_main.WorkspaceWindow.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "btnRestart").IsEffectivelyEnabled,
            "settings restart action stays enabled independently of hidden home");
        await _main.OpenSettingsAsync("SystemProxy");
        Check(_main.WorkspaceWindow.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "cmbSystemProxy").IsEffectivelyEnabled,
            "settings proxy mode remains accessible while home is disabled");
        await _main.NavigateAsync("Home");
        Check(controls.All(c => c.IsEffectivelyEnabled) && controls[1].Focus(), "returning home restores keyboard controls");

        await CheckUpdateDisconnectGuardAsync();
        await CheckRefreshShortcutScopeAsync();
        await CheckSettingsNavigationLayoutAsync();
        await CheckWorkspaceFeatureParityAsync();
        await _main.NavigateAsync("Home");
    }

    private async Task CheckSettingsNavigationLayoutAsync()
    {
        var width = _main.SettingsWindow!.Width;
        var appearance = new ThemeSettingViewModel(editAsDraft: true);
        try
        {
            _main.SettingsWindow.Width = 800;
            foreach (var size in new[] { 14, 24 })
            {
                appearance.CurrentFontSize = size;
                await _main.OpenSettingsAsync("Help");
                foreach (var group in _main.WorkspaceWindow.GetVisualDescendants().OfType<Expander>()
                             .Where(control => control.Name?.StartsWith("group") == true)) group.IsExpanded = true;
                var navigation = _main.WorkspaceWindow.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll =>
                    scroll.Content is StackPanel panel && panel.Children.OfType<Expander>().Any(group => group.Name == "groupapp"));
                foreach (var key in new[] { "Templates", "Servers", "Help" })
                {
                    // Starting at the opposite end catches navigation that only updates selection styling.
                    navigation.Offset = new Vector(0, key == "Servers" ? navigation.Extent.Height : 0);
                    await _main.OpenSettingsAsync(key);
                    _main.UpdateLayout();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    await Task.Delay(100);
                    var selected = _main.WorkspaceWindow.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "nav" + key);
                    var position = selected.TranslatePoint(default, navigation)!.Value;
                    await CaptureAsync($"v3-navigation-{key.ToLowerInvariant()}-{size}", "Current settings entry remains visible", _main);
                    Check(selected.IsEffectivelyVisible && position.Y >= -1
                        && position.Y + selected.Bounds.Height <= navigation.Viewport.Height + 1,
                        $"selected settings entry {key} is scrolled into view at {size}px font (y={position.Y}, height={selected.Bounds.Height}, viewport={navigation.Viewport.Height})");
                }
                foreach (var entry in WorkspacePages.Available)
                {
                    var button = _main.WorkspaceWindow.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "nav" + entry.Key);
                    var label = button.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == entry.Title);
                    var position = label.TranslatePoint(default, button)!.Value;
                    Check(Math.Abs(label.FontSize - size) < .1 && position.X >= -1 && position.X + label.Bounds.Width <= button.Bounds.Width + 1
                        && position.Y >= -1 && position.Y + label.Bounds.Height <= button.Bounds.Height + 1,
                        $"settings label {entry.Key} wraps within its button at {size}px font (label={label.Bounds}, button={button.Bounds})");
                }
                var content = _main.WorkspaceWindow.GetVisualDescendants().OfType<ContentControl>().Single(control => control.Name == "settingsBody").Content;
                navigation.Offset = default;
                await _main.OpenSettingsAsync("Help");
                _main.UpdateLayout();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Delay(100);
                var current = _main.WorkspaceWindow.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "navHelp");
                var currentPosition = current.TranslatePoint(default, navigation)!.Value;
                Check(currentPosition.Y >= -1 && currentPosition.Y + current.Bounds.Height <= navigation.Viewport.Height + 1
                    && ReferenceEquals(content, _main.WorkspaceWindow.GetVisualDescendants().OfType<ContentControl>().Single(control => control.Name == "settingsBody").Content),
                    $"revisiting the current settings page reveals its menu without replacing content at {size}px font");
            }
        }
        finally { appearance.RevertPreview(); _main.SettingsWindow.Width = width; }
    }

    private async Task CheckUpdateDisconnectGuardAsync()
    {
        var config = AppManager.Instance.Config;
        var savedId = config.IndexId;
        var savedTun = config.TunModeItem.EnableTun;
        var pausedField = typeof(MainWindowViewModel).GetField("_connectionPaused", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldPaused = pausedField.GetValue(_vm);
        var oldError = _vm.StatusBarViewModel.ConnectionError;
        var transition = _vm.StatusBarViewModel.Transition;
        try
        {
            // Even a regression cannot start a core: the test has no selected profile or TUN.
            config.IndexId = ""; config.TunModeItem.EnableTun = false;
            _vm.DesignMode = false;
            foreach (var paused in new[] { false, true })
            {
                pausedField.SetValue(_vm, paused);
                _vm.StatusBarViewModel.ConnectionError = "update guard sentinel";
                var restart = await _vm.CheckUpdateViewModel.PrepareCoreRestartAsync!();
                Check(await restart("isolated update regression"), "disconnected core update can commit its new version");
                Check(_vm.StatusBarViewModel.ConnectionError == "update guard sentinel"
                    && ReferenceEquals(transition, _vm.StatusBarViewModel.Transition)
                    && !await CoreManager.Instance.IsCoreRunningAsync(),
                    $"update callback preserves disconnected state (paused={paused})");
            }

            var coordinator = (ConnectionReloadCoordinator)typeof(MainWindowViewModel)
                .GetField("_reloadCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_vm)!;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocking = coordinator.RunAsync(() => true, () => release.Task, CancellationToken.None);
            var current = true;
            var queued = (Task<bool>)typeof(MainWindowViewModel).GetMethod("ReloadCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_vm, [null, CancellationToken.None, "queued update fixture", false, null, (Func<bool>)(() => current)])!;
            Check(!queued.IsCompleted, "update restart waits for its lifecycle transaction");
            current = false;
            release.SetResult(true);
            await blocking;
            Check(await queued && _vm.StatusBarViewModel.ConnectionError == "update guard sentinel"
                && ReferenceEquals(transition, _vm.StatusBarViewModel.Transition),
                "queued update rechecks intent inside the real lifecycle lock without entering startup");
        }
        finally
        {
            _vm.DesignMode = true;
            config.IndexId = savedId; config.TunModeItem.EnableTun = savedTun;
            pausedField.SetValue(_vm, oldPaused);
            _vm.StatusBarViewModel.ConnectionError = oldError;
        }
    }

    private async Task CheckRefreshShortcutScopeAsync()
    {
        var field = typeof(HttpClientHelper).GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = field.GetValue(HttpClientHelper.Instance);
        var responses = new WorkspaceApiFixture();
        using var client = new HttpClient(responses);
        field.SetValue(HttpClientHelper.Instance, client);
        var revision = typeof(MainWindowViewModel).GetField("_connectionRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var paused = typeof(MainWindowViewModel).GetField("_connectionPaused", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldPaused = paused.GetValue(_vm);
        try
        {
            await _main.OpenSettingsAsync("Proxies");
            var view = _main.WorkspaceWindow.GetLogicalDescendants().OfType<ClashProxiesView>().Single();
            _vm.CancelConnectionTransition();
            var before = revision.GetValue(_vm);
            var refresh = view.FindControl<Button>("menuProxiesReload")!;
            refresh.Focus();
            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F5 };
            refresh.RaiseEvent(key);
            await Task.Delay(50);
            Check(key.Handled && Equals(before, revision.GetValue(_vm)) && (bool)paused.GetValue(_vm)!,
                "policy group F5 refresh does not restart or clear explicit disconnect");
            Check(responses.Paths.Count(path => path == "/proxies") == 1 && responses.Paths.Contains("/configs"),
                "policy group F5 still refreshes its actual API once");
            await _main.OpenSettingsAsync("Application");
            _main.FindControl<TextBox>("settingsSearch")!.Text = "";
            _main.FindControl<Button>("btnReturnConnection")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F5 });
            Check(Equals(before, revision.GetValue(_vm)), "settings without a local F5 action never restart the connection");
            await _main.NavigateAsync("Home");
            _main.FindControl<Button>("btnSettings")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F5 });
            await Task.Delay(30);
            Check(!Equals(before, revision.GetValue(_vm)) && !(bool)paused.GetValue(_vm)!,
                "connection home retains its explicit F5 restart shortcut");
        }
        finally { field.SetValue(HttpClientHelper.Instance, original); paused.SetValue(_vm, oldPaused); }
    }

    private sealed class WorkspaceApiFixture : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(request.RequestUri.AbsolutePath == "/configs" ? "{\"mode\":\"rule\"}" : "{\"proxies\":{},\"providers\":{}}") });
        }
    }
}
