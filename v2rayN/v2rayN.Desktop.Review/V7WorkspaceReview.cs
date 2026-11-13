using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task ReviewWorkspaceV7Async()
    {
        await _main.NavigateAsync("Home");
        var bar = (StatusBarView)_main.FindControl<ContentControl>("contentStatusBarView")!.Content!;
        _vm.BlReloadEnabled = true; bar.SetReloading(false);
        await Task.Delay(150);
        var timer = (DispatcherTimer)typeof(StatusBarView).GetField("_coreTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar)!;
        timer.Stop();
        var running = typeof(StatusBarView).GetField("_coreRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var render = typeof(StatusBarView).GetMethod("RenderCoreState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var core = CoreManager.Instance;
        var mode = typeof(CoreManager).GetProperty(nameof(CoreManager.RunningTunEnabled))!;
        var originalMode = core.RunningTunEnabled;
        var runtimeProperty = typeof(CoreManager).GetProperty(nameof(CoreManager.RuntimeSummary))!;
        var originalRuntime = core.RuntimeSummary;
        var originalSelection = AppManager.Instance.Config.IndexId;
        var name = _vm.StatusBarViewModel.RunningServerDisplay;
        var longName = "[SSH] 新加坡（独立机器+国内路由） · 专线跨区域出口和完整长名称验证 " + new string('长', 100);
        try
        {
            foreach (var (width, height) in new[] { (440, 620), (440, 560), (360, 480) })
            foreach (var dark in new[] { false, true })
            foreach (var state in new[] { "stopped", "tun", "standard", "waiting", "failed", "pending", "empty" })
            {
                _main.Width = width; _main.Height = height;
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                _vm.StatusBarViewModel.Transition = null;
                _vm.StatusBarViewModel.ConnectionError = null;
                _vm.StatusBarViewModel.HasPendingChanges = state == "pending";
                AppManager.Instance.Config.IndexId = state == "empty" ? "" : originalSelection;
                _vm.StatusBarViewModel.RunningServerDisplay = state == "empty" ? "" : longName;
                runtimeProperty.SetValue(core, state == "pending" ? new CoreRuntimeSummary("previous", "实际节点 " + longName, "很长的路由规则 " + longName, false) : null);
                running.SetValue(bar, state is "tun" or "standard" or "pending");
                mode.SetValue(core, state == "tun");
                if (state == "waiting")
                    _vm.StatusBarViewModel.ReportTransition(new(ConnectionStage.Waiting, false, WaitSeconds: 37) { OperationId = ConnectionTransition.NewOperationId() });
                if (state == "failed")
                {
                    _vm.StatusBarViewModel.ConnectionError = "端口占用，未终止未知进程。 " + new string('详', 500);
                    var id = ConnectionTransition.NewOperationId();
                    _vm.StatusBarViewModel.ReportTransition(new(ConnectionStage.Starting, false) { OperationId = id });
                    await Task.Delay(20);
                    _vm.StatusBarViewModel.ReportTransition(new(ConnectionStage.Failed, false, _vm.StatusBarViewModel.ConnectionError) { OperationId = id });
                }
                await Task.Delay(state == "waiting" ? 500 : 80);
                // Health is a synthetic layout fixture; no lifecycle action is invoked.
                running.SetValue(bar, state is "tun" or "standard" or "pending"); render.Invoke(bar, null);
                _main.UpdateLayout();
                var context = $"{width}x{height}/{dark}/{state}/{Program.Language}";
                var scrolls = bar.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible).ToArray();
                Check(scrolls.Length == 0, "connection home has no scroll container, including active feedback: " + context);
                Check(!bar.GetVisualDescendants().OfType<ScrollBar>().Any(s => s.IsEffectivelyVisible), "connection home has no visible scroll bars: " + context);
                foreach (var controlName in new[] { "txtCoreState", "txtRuntimeCaption", "txtRunningServerDisplay", "btnServerIdentity", "btnConnectionControls", "btnConnect", "togEnableTun", "togRealTimeSpeed", "btnRouting", "connectionFeedbackHost", "txtRecoveryState", "btnRetryConnection", "btnConnectionDiagnostics", "btnApplyPending", "txtActualServer" })
                {
                    var control = bar.FindControl<Control>(controlName)!;
                    if (!control.IsEffectivelyVisible && controlName is "txtRecoveryState" or "btnRetryConnection" or "btnConnectionDiagnostics" or "btnApplyPending" or "txtActualServer") continue;
                    var position = control.TranslatePoint(default, _main)!.Value;
                    Check(control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
                        && position.X >= -.5 && position.X + control.Bounds.Width <= width + .5
                        && position.Y >= 55.5 && position.Y + control.Bounds.Height <= height + .5,
                        $"{controlName} is genuinely inside the unscrolled window: {context}, at={position}, bounds={control.Bounds}");
                }
                var rows = bar.FindControl<Grid>("homeRows")!;
                foreach (var child in rows.Children)
                {
                    var row = Grid.GetRow(child);
                    var next = rows.Children.FirstOrDefault(c => Grid.GetRow(c) == row + 1);
                    if (next != null) Check(child.Bounds.Bottom <= next.Bounds.Top + .5, "home rows never overlap: " + row + "/" + context);
                }
                var recovery = bar.FindControl<TextBlock>("txtRecoveryState")!;
                if (recovery.IsEffectivelyVisible)
                    Check(recovery.TranslatePoint(default, rows)!.Value.Y + recovery.Bounds.Height <= rows.Children.Single(c => Grid.GetRow(c) == 1).Bounds.Top,
                        "recovery text fits above the node card: " + context);
                foreach (var button in _main.ConnectionFeedback!.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
                {
                    var position = button.TranslatePoint(default, _main)!.Value;
                    Check(position.X >= 0 && position.X + button.Bounds.Width <= width && position.Y + button.Bounds.Height <= height,
                        "feedback action remains inside the window: " + button.Name + "/" + context);
                }
                if (state == "failed")
                    Check(bar.FindControl<Button>("btnRetryConnection")!.IsEffectivelyVisible
                        && bar.FindControl<Button>("btnConnectionDiagnostics")!.IsEffectivelyVisible, "failure retains direct recovery actions: " + context);
                if (state is "tun" or "waiting" or "failed")
                    await CaptureAsync($"v7-{width}x{height}-{(dark ? "dark" : "light")}-{state}", "Compact home with long fixture text", _main);
                _main.ConnectionFeedback!.Dismiss();
            }
            _vm.StatusBarViewModel.Transition = null;
            _vm.StatusBarViewModel.ConnectionError = null;
            _vm.StatusBarViewModel.RunningServerDisplay = longName;
            await Task.Delay(50);
            var identityButton = bar.FindControl<Button>("btnServerIdentity")!;
            identityButton.Focus(NavigationMethod.Tab);
            identityButton.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Task.Delay(60);
            var identityFlyout = (Flyout)identityButton.Flyout!;
            Check(identityFlyout.IsOpen && ((TextBlock)((ScrollViewer)identityFlyout.Content!).Content!).Text!.Contains(longName),
                "keyboard opens the entire untruncated server identity");
            identityFlyout.Hide();
            var feedback = _main.ConnectionFeedback!;
            var detailOperationId = ConnectionTransition.NewOperationId();
            _vm.StatusBarViewModel.ReportTransition(new(ConnectionStage.Starting, false) { OperationId = detailOperationId });
            await Task.Delay(30);
            var diagnostic = "Failure detail " + new string('D', 1000);
            _vm.StatusBarViewModel.ReportTransition(new(ConnectionStage.Failed, false, diagnostic) { OperationId = detailOperationId });
            await Task.Delay(50);
            var details = feedback.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "feedbackDetails");
            details.Focus(NavigationMethod.Tab);
            details.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Task.Delay(60);
            var flyout = (Flyout)details.Flyout!;
            Check(flyout.IsOpen && ((ScrollViewer)flyout.Content!).GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == diagnostic),
                "keyboard opens full lifecycle details without truncation");
            Check(feedback.Bounds.Height == 32, "opening details never expands the home footer");
            feedback.Dismiss();
            Check(!flyout.IsOpen && !feedback.State.CancelRequested, "dismiss closes details without cancelling the operation");
            Check(!await core.IsCoreRunningAsync(), "compact-home checks never start a real core");
        }
        finally
        {
            _vm.StatusBarViewModel.Transition = null;
            _vm.StatusBarViewModel.ConnectionError = null;
            _vm.StatusBarViewModel.RunningServerDisplay = name;
            _vm.StatusBarViewModel.HasPendingChanges = false;
            runtimeProperty.SetValue(core, originalRuntime);
            AppManager.Instance.Config.IndexId = originalSelection;
            mode.SetValue(core, originalMode); running.SetValue(bar, false); timer.Start();
        }
    }
}
