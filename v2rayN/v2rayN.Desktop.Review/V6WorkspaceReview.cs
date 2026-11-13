using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DialogHostAvalonia;
using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using ServiceLib.ViewModels;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private sealed class FeedbackClock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    private void CheckFeedbackTiming()
    {
        var clock = new FeedbackClock(); var state = new ConnectionFeedbackState(clock);
        var id = ConnectionTransition.NewOperationId();
        ConnectionTransition Step(ConnectionStage stage, long? operation = null) => new(stage, false) { OperationId = operation ?? id };
        state.Update(Step(ConnectionStage.Preparing)); clock.Milliseconds = 399; state.Tick();
        Check(!state.Visible, "fast operations do not flash an intermediate notice");
        clock.Milliseconds = 400; state.Tick(); Check(state.Visible, "ongoing feedback appears at 400ms");
        state.Update(Step(ConnectionStage.Waiting)); state.Dismiss();
        state.Update(Step(ConnectionStage.Waiting) with { WaitSeconds = 40 });
        Check(!state.Visible && !state.CancelRequested && state.HadHandoff, "dismissed operation updates silently without cancelling cleanup");
        state.Reopen(); Check(state.Visible && state.RequestCancellation() && !state.RequestCancellation(), "view progress reopens the same operation and cancellation is single-shot");
        state.Update(Step(ConnectionStage.Cancelled)); clock.Milliseconds += 3999; state.Tick(); Check(state.Visible, "cancellation remains readable for four seconds");
        clock.Milliseconds++; state.Tick(); Check(!state.Visible, "cancellation automatically dismisses after four seconds");
        foreach (var (stage, duration) in new[] { (ConnectionStage.Completed, 3000), (ConnectionStage.Failed, 8000) })
        {
            id = ConnectionTransition.NewOperationId(); state.Update(Step(ConnectionStage.Starting)); state.Update(Step(stage));
            Check(state.Visible, "fast operation shows its final result: " + stage);
            clock.Milliseconds += 1000; state.SetPaused(true); clock.Milliseconds += 20000; state.Tick();
            Check(state.Visible, "hover or keyboard focus pauses expiry: " + stage);
            state.SetPaused(false); clock.Milliseconds += duration - 1001; state.Tick();
            state.Update(Step(stage)); Check(state.Visible, "duplicate terminal event preserves remaining display time: " + stage);
            clock.Milliseconds++; state.Tick(); Check(!state.Visible, "duplicate result cannot reset auto-dismiss: " + stage);
            state.Update(Step(ConnectionStage.Waiting)); Check(!state.Visible && state.Current!.Stage == stage, "late progress cannot revive a completed operation");
        }
        var oldId = id; id = ConnectionTransition.NewOperationId(); state.Update(Step(ConnectionStage.Starting));
        clock.Milliseconds += 10000; state.Tick();
        Check(state.Visible && state.Current!.OperationId == id, "old result deadline cannot dismiss a new active operation");
        state.Update(Step(ConnectionStage.Failed, oldId)); Check(state.Current!.OperationId == id, "older operation result cannot replace newer progress");
        id = ConnectionTransition.NewOperationId(); state.Update(Step(ConnectionStage.Starting) with { IsBackground = true });
        clock.Milliseconds += 90000; state.Tick(); Check(!state.Visible, "automatic recovery stays quiet even during a long wait");
        state.Reopen(); state.Tick(); Check(state.Visible, "background operation remains explicitly inspectable");
        state.Update(Step(ConnectionStage.Completed) with { IsBackground = true });
        Check(state.Visible, "explicitly inspected recovery result stays readable");
        var initial = new ConnectionFeedbackState(clock); initial.Update(Step(ConnectionStage.Completed));
        Check(!initial.Visible, "resubscribing to an old completed result does not replay a toast");
        foreach (var operation in new[] { new ConnectionTransition(ConnectionStage.Stopping, false, Disconnecting: true), new ConnectionTransition(ConnectionStage.ApplyingProxy, false, UpdatingProxy: true) })
        {
            state.Update(operation with { OperationId = ConnectionTransition.NewOperationId() });
            Check(!state.CanCancel && !state.RequestCancellation(), "uncancellable cleanup or proxy update does not offer cancellation");
        }
    }

    private async Task ReviewWorkspaceV6Async()
    {
        CheckFeedbackTiming();
        await _main.NavigateAsync("Home"); _main.Width = 440; _main.Height = 620;
        var bar = (StatusBarView)_main.FindControl<ContentControl>("contentStatusBarView")!.Content!;
        var feedback = _main.ConnectionFeedback!;
        var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var windowCount = lifetime.Windows.Count;
        var settings = _main.FindControl<Button>("btnSettings")!;
        var id = ConnectionTransition.NewOperationId();
        void Send(ConnectionStage stage, string? detail = null, bool proxy = false) => _vm.StatusBarViewModel.ReportTransition(new(stage, false, detail, WaitSeconds: 11, UpdatingProxy: proxy) { OperationId = id });
        Button Action(string name) => feedback.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
        void Click(string name) => Action(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        settings.Focus(); Send(ConnectionStage.Preparing); await Task.Delay(550);
        Check(feedback.NoticeVisible && _main.IsEnabled && lifetime.Windows.Count == windowCount, "connection progress creates no top-level or modal window");
        Check(settings.IsKeyboardFocusWithin, "incoming progress never steals keyboard focus");
        Send(ConnectionStage.Waiting); await Task.Delay(50);
        Check(feedback.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("90") == true)
            && !feedback.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("error")), "real port wait displays its bounded budget without an error state");
        await CaptureAsync("v6-wait-light", "In-window handoff feedback", _main);
        Click("feedbackDismiss"); Send(ConnectionStage.Waiting); await Task.Delay(100);
        Check(!feedback.NoticeVisible && !feedback.State.CancelRequested, "closing notice does not cancel or reopen on repeated status");
        Click("feedbackReopen");
        using (var cancellation = new CancellationTokenSource())
        {
            var field = typeof(MainWindowViewModel).GetField("_activeTransitionCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(_vm, cancellation);
            try { Click("feedbackCancel"); Check(cancellation.IsCancellationRequested && !Action("feedbackCancel").IsEnabled, "notice cancel reaches the existing cancellation path once"); }
            finally { field.SetValue(_vm, null); }
        }
        Send(ConnectionStage.Cancelled); await Task.Delay(40); feedback.Dismiss();
        id = ConnectionTransition.NewOperationId(); Send(ConnectionStage.Starting); await Task.Delay(50); Send(ConnectionStage.Completed); await Task.Delay(60);
        Check(feedback.NoticeVisible && !feedback.State.HadHandoff, "ordinary operation shows a short result and clears previous handoff stages");
        settings.Focus(); feedback.State.SetPaused(false);
        await Task.Delay(3150);
        Check(!feedback.NoticeVisible, "production dispatcher timer automatically dismisses success");
        Send(ConnectionStage.Completed); await Task.Delay(100); Check(!feedback.NoticeVisible, "repeated success cannot reopen a dismissed result");
        id = ConnectionTransition.NewOperationId(); Send(ConnectionStage.Starting); await Task.Delay(50);
        _vm.StatusBarViewModel.ConnectionError = "Fixture: port handoff timed out; details remain available.";
        Send(ConnectionStage.Failed, _vm.StatusBarViewModel.ConnectionError); await Task.Delay(60); feedback.Dismiss();
        Check(bar.FindControl<StackPanel>("connectionErrorPanel")!.IsVisible && Action("feedbackRestDiagnostics").IsVisible,
            "failure retains inline retry and diagnostics after transient feedback disappears");
        await CaptureAsync("v6-failure-retained", "Persistent failure summary", _main);
        await _main.NavigateAsync("Logs");
        feedback = _main.ConnectionFeedback!;
        id = ConnectionTransition.NewOperationId(); Send(ConnectionStage.ApplyingProxy, proxy: true); await Task.Delay(50); Send(ConnectionStage.Completed, proxy: true); await Task.Delay(60);
        feedback = _main.ConnectionFeedback!;
        Check(TopLevel.GetTopLevel(feedback) == _main.SettingsWindow && feedback.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "系统代理已更新"),
            $"settings feedback stays in settings and does not claim the core connected (mainActive={_main.IsActive}, settingsActive={_main.SettingsWindow!.IsActive}, top={TopLevel.GetTopLevel(feedback)?.Name})");
        _vm.StatusBarViewModel.ReportTransition(new(ConnectionStage.Failed, true) { OperationId = id - 1 }); await Task.Delay(50);
        Check(_vm.StatusBarViewModel.Transition?.OperationId == id && feedback.State.Current?.OperationId == id, "view model and notice both reject obsolete operation reports");
        _main.SettingsWindow!.Hide(); _main.ShowHideWindow(false);
        id = ConnectionTransition.NewOperationId(); Send(ConnectionStage.Starting); await Task.Delay(450); Send(ConnectionStage.Completed); await Task.Delay(50);
        Check(!_main.IsVisible && !_main.SettingsWindow.IsVisible && !feedback.NoticeVisible, "background progress never activates hidden windows");
        _main.ShowHideWindow(true); await _main.NavigateAsync("Home"); await Task.Delay(50);
        feedback = _main.ConnectionFeedback!;
        Check(!feedback.NoticeVisible, "returning to the app does not replay hidden success");
        _vm.StatusBarViewModel.ConnectionError = null;
        id = ConnectionTransition.NewOperationId(); Send(ConnectionStage.Starting); await Task.Delay(450);
        var host = _main.GetVisualDescendants().OfType<DialogHost>().Single();
        var passwordView = new SudoPasswordInputView((_, _) => Task.FromResult(false)) { DialogIdentifier = host.Identifier };
        var passwordTask = DialogHost.Show(passwordView, host.Identifier); await Task.Delay(60);
        var password = passwordView.FindControl<TextBox>("txtPassword")!; password.Focus();
        Send(ConnectionStage.Verifying); await Task.Delay(80);
        Check(password.IsKeyboardFocusWithin && host.IsOpen, "non-modal updates never dismiss or steal focus from sudo authorization");
        passwordView.FindControl<Button>("btnCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await passwordTask;
        Send(ConnectionStage.Cancelled); await Task.Delay(50); feedback.Dismiss();
        _vm.StatusBarViewModel.Transition = null; _vm.BlReloadEnabled = true; bar.SetReloading(false);
        await Task.Delay(100);
        var timer = (DispatcherTimer)typeof(StatusBarView).GetField("_coreTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bar)!;
        timer.Stop();
        var core = CoreManager.Instance;
        var actualTun = typeof(CoreManager).GetProperty(nameof(CoreManager.RunningTunEnabled))!;
        var running = typeof(StatusBarView).GetField("_coreRunning", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var render = typeof(StatusBarView).GetMethod("RenderCoreState", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var originalTun = core.RunningTunEnabled; var selectedTun = AppManager.Instance.Config.TunModeItem.EnableTun;
        var button = bar.FindControl<Button>("btnConnect")!;
        try
        {
            AppManager.Instance.Config.TunModeItem.EnableTun = true;
            foreach (var dark in new[] { false, true })
            {
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; await Task.Delay(40);
                foreach (var tun in new[] { false, true })
                {
                    actualTun.SetValue(core, tun); running.SetValue(bar, true); render.Invoke(bar, null); _main.UpdateLayout();
                    Check(button.Classes.Contains("tun") == tun, "button follows actual mode even when selected TUN differs");
                    var classes = (IPseudoClasses)button.Classes; var rect = button.Bounds;
                    foreach (var (pseudo, suffix) in new[] { ("", ""), (":pointerover", "Hover"), (":pressed", "Pressed") })
                    {
                        classes.Set(":pointerover", pseudo != ""); classes.Set(":pressed", pseudo == ":pressed"); _main.UpdateLayout();
                        var expected = ((ISolidColorBrush)button.FindResource(button.ActualThemeVariant, (tun ? "ConnectionTun" : "ConnectionAccent") + suffix)!).Color;
                        var face = button.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
                        Check(face.Background is ISolidColorBrush brush && brush.Color == expected && button.Bounds == rect, "actual-mode color fills one stable button face: " + tun + "/" + dark + "/" + pseudo);
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)rect.Width, (int)rect.Height), new Vector(96, 96)); bitmap.Render(button);
                        using var stream = new MemoryStream(); bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                        using var decoded = SkiaSharp.SKBitmap.Decode(stream.ToArray()); var pixel = decoded.GetPixel(10, 12);
                        Check(pixel.Red == expected.R && pixel.Green == expected.G && pixel.Blue == expected.B && pixel.Alpha == 255, "mode and hover pixels match the opaque action palette");
                    }
                    classes.Set(":pressed", false); classes.Set(":pointerover", false);
                    await CaptureAsync($"v6-{(tun ? "tun" : "standard")}-{(dark ? "dark" : "light")}", "Actual-mode action palette", _main);
                }
            }
            bar.SetReloading(true); _main.UpdateLayout();
            Check(button.Classes.Contains("transitioning") && !button.Classes.Contains("tun") && !button.IsEnabled, "switching uses amber instead of prematurely showing TUN green");
            Check(button.GetVisualDescendants().OfType<TextBlock>().All(t => t.Foreground is ISolidColorBrush brush && brush.Color == Colors.White),
                "busy action retains readable white text instead of the theme disabled foreground");
        }
        finally { actualTun.SetValue(core, originalTun); running.SetValue(bar, false); AppManager.Instance.Config.TunModeItem.EnableTun = selectedTun; bar.SetReloading(false); timer.Start(); }
        RequestedThemeVariant = ThemeVariant.Light;
        _main.Width = 360; _main.Height = 480; await Task.Delay(60);
        Check(feedback.Bounds.Width <= _main.ClientSize.Width - 24 && feedback.Bounds.Height == 32, "small window keeps compact feedback within the connection column");
        await CaptureAsync("v6-narrow", "Narrow connection window", _main);
        Check(!await core.IsCoreRunningAsync(), "feedback and palette regression never starts a real core");
    }
}
