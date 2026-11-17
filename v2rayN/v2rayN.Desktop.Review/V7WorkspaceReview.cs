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
        await ReviewBackgroundSubscriptionAsync(bar);
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
            foreach (var state in new[] { "stopped", "tun", "standard", "waiting", "failed", "pending", "failed-pending", "incomplete", "unconfirmed", "proxyfailed", "background-refresh", "background-apply", "empty" })
            {
                _main.Width = width; _main.Height = height;
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                _vm.StatusBarViewModel.Transition = null;
                _vm.StatusBarViewModel.ConnectionError = null;
                _vm.StatusBarViewModel.BackgroundRefreshError = state == "background-refresh" ? "[Fixture] background refresh failed" : null;
                _vm.StatusBarViewModel.BackgroundApplyError = state == "background-apply" ? "[Fixture] background application failed" : null;
                _vm.StatusBarViewModel.ConnectionIncomplete = state == "incomplete";
                _vm.StatusBarViewModel.ExitUnconfirmed = state == "unconfirmed";
                _vm.StatusBarViewModel.HasPendingChanges = state is "pending" or "failed-pending";
                AppManager.Instance.Config.IndexId = state == "empty" ? "" : originalSelection;
                _vm.StatusBarViewModel.RunningServerDisplay = state == "empty" ? "" : longName;
                runtimeProperty.SetValue(core, state is "pending" or "failed-pending" ? new CoreRuntimeSummary("previous", "实际节点 " + longName, "很长的路由规则 " + longName, false) { Core = ServiceLib.Enums.ECoreType.sing_box } : null);
                running.SetValue(bar, state is "tun" or "standard" or "pending" or "failed-pending");
                mode.SetValue(core, state == "tun");
                if (state == "waiting")
                    _vm.StatusBarViewModel.ReportTransition(new(ConnectionStage.Waiting, false, WaitSeconds: 37) { OperationId = ConnectionTransition.NewOperationId() });
                if (state is "failed" or "failed-pending" or "proxyfailed")
                {
                    _vm.StatusBarViewModel.ConnectionError = "端口占用，未终止未知进程。 " + new string('详', 500);
                    var id = ConnectionTransition.NewOperationId();
                    _vm.StatusBarViewModel.ReportTransition(new(ConnectionStage.Starting, false) { OperationId = id });
                    await Task.Delay(20);
                    _vm.StatusBarViewModel.ReportTransition(new(ConnectionStage.Failed, false, _vm.StatusBarViewModel.ConnectionError) { OperationId = id });
                }
                await Task.Delay(state == "waiting" ? 500 : 80);
                // Health is a synthetic layout fixture; no lifecycle action is invoked.
                _vm.StatusBarViewModel.ConnectionIncomplete = state == "incomplete";
                _vm.StatusBarViewModel.ExitUnconfirmed = state == "unconfirmed";
                running.SetValue(bar, state is "tun" or "standard" or "pending" or "failed-pending" or "incomplete" or "proxyfailed" or "background-refresh" or "background-apply"); render.Invoke(bar, null);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                _main.UpdateLayout();
                var context = $"{width}x{height}/{dark}/{state}/{Program.Language}";
                var restart = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "btnRestart");
                Check(restart.IsEffectivelyVisible && ReferenceEquals(restart.Command, _vm.StatusBarViewModel.RestartCoreCmd), "home exposes shared restart command: " + context);
                Check(restart.IsEffectivelyEnabled == (state is "tun" or "standard" or "pending" or "failed-pending" or "incomplete" or "proxyfailed" or "background-refresh" or "background-apply"), "restart availability matches lifecycle: " + context);
                if (state is "incomplete" or "unconfirmed")
                    Check(bar.FindControl<TextBlock>("txtCoreState")!.Text == (state == "incomplete"
                        ? ServiceLib.Services.RoutingCoreCompatibility.T("连接不完整", "Connection incomplete")
                        : ServiceLib.Services.RoutingCoreCompatibility.T("退出尚未确认", "Exit not confirmed")), "runtime failure is not displayed as ready: " + context
                            + ", actual=" + bar.FindControl<TextBlock>("txtCoreState")!.Text);
                Check((string)restart.Content! == _vm.StatusBarViewModel.RestartCoreLabel, "restart label reflects saved pending changes: " + context);
                if (state is "background-refresh" or "background-apply")
                    Check(bar.FindControl<TextBlock>("txtCoreState")!.Text == (state == "background-refresh"
                        ? ServiceLib.Services.RoutingCoreCompatibility.T("内核已就绪", "Core ready")
                        : ServiceLib.Services.RoutingCoreCompatibility.T("后台应用失败，连接保持", "Apply failed; connected")),
                        "background failure is not a failed manual switch: " + context);
                var restartOrigin = restart.TranslatePoint(default, _main)!.Value;
                Check(restartOrigin.X >= 0 && restartOrigin.X + restart.Bounds.Width <= width && restartOrigin.Y + restart.Bounds.Height <= height,
                    "restart remains inside compact window: " + context);
                var connect = bar.FindControl<Button>("btnConnect")!;
                Check(connect.TranslatePoint(default, _main)!.Value.X + connect.Bounds.Width <= restartOrigin.X,
                    "connect and restart never overlap: " + context);
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
                if (state == "failed-pending")
                    Check(bar.FindControl<TextBlock>("txtActualServer")!.IsEffectivelyVisible
                        && bar.FindControl<TextBlock>("txtActualServer")!.Text!.Contains("sing-box")
                        && bar.FindControl<TextBlock>("txtNodeLabel")!.Text!.Contains(ServiceLib.Services.RoutingCoreCompatibility.T("待应用", "Pending")),
                        "failed switch retains both the real core/server and pending selection: " + context);
                if (state is "failed" or "failed-pending")
                    Check(bar.FindControl<Button>("btnRetryConnection")!.IsEffectivelyVisible
                        && bar.FindControl<Button>("btnConnectionDiagnostics")!.IsEffectivelyVisible, "failure retains direct recovery actions: " + context);
                if (state is "failed" or "failed-pending")
                {
                    var errorPanel = bar.FindControl<StackPanel>("connectionErrorPanel")!;
                    var fields = bar.FindControl<StackPanel>("connectionFields")!;
                    Check(errorPanel.TranslatePoint(default, bar)!.Value.Y + errorPanel.Bounds.Height
                        <= fields.TranslatePoint(default, bar)!.Value.Y, "error content cannot overlap TUN controls: " + context);
                }
                if (state is "tun" or "waiting" or "failed" or "failed-pending" or "incomplete" or "unconfirmed" or "proxyfailed" or "background-refresh" or "background-apply")
                    await CaptureAsync($"v7-{width}x{height}-{(dark ? "dark" : "light")}-{state}", "Compact home with long fixture text", _main);
                _main.ConnectionFeedback!.Dismiss();
            }
            _vm.StatusBarViewModel.Transition = null;
            _vm.StatusBarViewModel.ConnectionError = null;
            _vm.StatusBarViewModel.BackgroundRefreshError = null;
            _vm.StatusBarViewModel.BackgroundApplyError = null;
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
            _vm.StatusBarViewModel.ConnectionIncomplete = false;
            _vm.StatusBarViewModel.ExitUnconfirmed = false;
            runtimeProperty.SetValue(core, originalRuntime);
            AppManager.Instance.Config.IndexId = originalSelection;
            mode.SetValue(core, originalMode); running.SetValue(bar, false); timer.Start();
        }
    }
}
