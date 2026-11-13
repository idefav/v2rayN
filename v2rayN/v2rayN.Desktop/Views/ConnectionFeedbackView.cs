using Avalonia.Automation;
using Avalonia.Animation;
using Avalonia.Layout;
using ServiceLib.Models.Dto;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>In-window, non-modal lifecycle feedback. Dismissal never stops a core.</summary>
internal sealed class ConnectionFeedbackView : UserControl, IDisposable
{
    private readonly ConnectionFeedbackState _state;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Border _notice = new() { CornerRadius = new(7), BorderThickness = new(1), Padding = new(5, 0), Height = 32, IsVisible = false };
    private readonly DockPanel _rest = new() { MinHeight = 28, LastChildFill = true };
    private readonly TextBlock _restTitle = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _title = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _steps = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl _icon = new() { Width = 14, Height = 14, Margin = new(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _dismiss = ActionButton("feedbackDismiss", "关闭提示", "Dismiss notice");
    private readonly Button _cancel = ActionButton("feedbackCancel", "取消切换", "Cancel switch");
    private readonly Button _retry = ActionButton("feedbackRetry", "重试", "Retry");
    private readonly Button _diagnostics = ActionButton("feedbackDiagnostics", "诊断", "Diagnose");
    private readonly Button _details = ActionButton("feedbackDetails", "查看阶段详情", "Show stage details");
    private readonly Button _reopen = ActionButton("feedbackReopen", "查看最近进度", "Recent progress");
    private readonly Button _restDiagnostics = ActionButton("feedbackRestDiagnostics", "诊断", "Diagnostics");
    private readonly Flyout _detailsFlyout;
    private bool _disposed, _retrying;
    private readonly bool _showRest;
    internal bool PresentationEnabled { get; private set; } = true;
    private string? _iconKey;
    private long? _renderedOperation;
    internal ConnectionFeedbackState State => _state;
    internal bool NoticeVisible => _notice.IsVisible;
    internal event Action? CancelRequested;
    internal event Func<Task>? RetryRequested;
    internal event Func<Task>? DiagnosticsRequested;

    internal ConnectionFeedbackView(TimeProvider? clock = null, ConnectionFeedbackState? state = null, bool showRest = true)
    {
        _state = state ?? new(clock);
        _showRest = showRest;
        Name = "connectionFeedback";
        MinHeight = showRest ? 32 : 0;
        Bind(_notice, Border.BackgroundProperty, "WorkspaceSidebar");
        Bind(_notice, Border.BorderBrushProperty, "WorkspaceBorder");
        Bind(_restTitle, TextBlock.ForegroundProperty, "WorkspaceMuted");
        _dismiss.Content = WorkspaceIcons.Create("close", 14);
        ToolTip.SetTip(_dismiss, WorkspaceText.T("关闭提示，不取消操作", "Dismiss notice without cancelling the operation"));
        _details.Content = WorkspaceText.T("详情", "Details");
        ToolTip.SetTip(_details, WorkspaceText.T("查看阶段与完整信息", "Show stages and full information"));
        _detailsFlyout = new Flyout { Content = new ScrollViewer { Width = 270, MaxHeight = 220,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Spacing = 10, Children = { _steps, _detail } } } };
        _details.Flyout = _detailsFlyout;
        _detailsFlyout.Opened += (_, _) => UpdatePause();
        _detailsFlyout.Closed += (_, _) => UpdatePause();
        var titleRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_dismiss, Dock.Right); DockPanel.SetDock(_icon, Dock.Left);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Children = { _cancel, _retry, _diagnostics, _details, _dismiss } };
        DockPanel.SetDock(actions, Dock.Right);
        titleRow.Children.Add(actions); titleRow.Children.Add(_icon); titleRow.Children.Add(_title);
        _notice.Child = titleRow;
        var restActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _restDiagnostics, _reopen } };
        DockPanel.SetDock(restActions, Dock.Right); _rest.Children.Add(restActions); _rest.Children.Add(_restTitle);
        var body = new Grid { Children = { _rest, _notice } };
        Content = body;
        AutomationProperties.SetLiveSetting(_title, AutomationLiveSetting.Polite);
        _dismiss.Click += (_, _) => Dismiss();
        _reopen.Click += (_, _) => { _state.Reopen(); Render(); _details.Focus(); };
        _cancel.Click += (_, _) => { if (_state.RequestCancellation()) { Render(); CancelRequested?.Invoke(); } };
        _retry.Click += async (_, _) =>
        {
            if (_retrying) return;
            _retrying = true; _retry.IsEnabled = false;
            Dismiss();
            try { await InvokeAsync(RetryRequested); }
            catch (Exception ex) { Logging.SaveLog("Connection feedback retry", ex); }
            finally { _retrying = false; _retry.IsEnabled = true; }
        };
        _diagnostics.Click += async (_, _) => await DiagnoseAsync();
        _restDiagnostics.Click += async (_, _) => await DiagnoseAsync();
        _notice.PointerEntered += (_, _) => UpdatePause();
        _notice.PointerExited += (_, _) => UpdatePause();
        _notice.GotFocus += (_, _) => UpdatePause();
        _notice.LostFocus += (_, _) => Dispatcher.UIThread.Post(UpdatePause);
        _timer.Tick += (_, _) => { _state.Tick(); Render(); };
        AttachedToVisualTree += (_, _) => { if (!_disposed) _timer.Start(); };
        DetachedFromVisualTree += (_, _) => { _detailsFlyout.Hide(); _timer.Stop(); };
        Render();
    }

    internal void Update(ConnectionTransition state, bool foreground)
    {
        var previous = _state.Current?.OperationId;
        if (!_state.Update(state)) return;
        if (previous != state.OperationId) _detailsFlyout.Hide();
        if (!foreground) _state.Dismiss();
        Render();
    }

    internal void Present(bool enabled)
    {
        PresentationEnabled = enabled;
        if (!enabled) _detailsFlyout.Hide();
        if (enabled) UpdatePause();
        Render();
    }

    internal void Dismiss()
    {
        var hadFocus = _notice.IsKeyboardFocusWithin || _detailsFlyout.IsOpen;
        _detailsFlyout.Hide();
        _state.Dismiss(); _state.SetPaused(false); Render();
        if (hadFocus && TopLevel.GetTopLevel(this) is Window { IsActive: true }) _reopen.Focus();
    }

    private void UpdatePause()
    {
        if (PresentationEnabled) _state.SetPaused(_detailsFlyout.IsOpen || _notice.IsPointerOver || _notice.IsKeyboardFocusWithin);
    }
    private static async Task InvokeAsync(Func<Task>? handlers)
    {
        if (handlers == null) return;
        foreach (Func<Task> handler in handlers.GetInvocationList()) await handler();
    }
    private async Task DiagnoseAsync()
    {
        try { await InvokeAsync(DiagnosticsRequested); }
        catch (Exception ex) { Logging.SaveLog("Connection feedback diagnostics", ex); }
    }

    private void Render()
    {
        var show = _state.Visible && PresentationEnabled;
        IsVisible = PresentationEnabled && (show || _showRest);
        if (show && !_notice.IsVisible)
        {
            _notice.Transitions = OperatingSystem.IsMacOS() && MacAppUtils.PrefersReducedMotion() ? null
                : new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) } };
            _notice.Opacity = 0; _notice.IsVisible = true;
            Dispatcher.UIThread.Post(() => { if (!_disposed && _state.Visible) _notice.Opacity = 1; }, DispatcherPriority.Loaded);
        }
        else if (!show) _notice.IsVisible = false;
        _rest.IsVisible = !show && _showRest;
        var state = _state.Current;
        if (_renderedOperation != state?.OperationId || !show) _detailsFlyout.Hide();
        _renderedOperation = state?.OperationId;
        _reopen.IsEnabled = state != null;
        _reopen.Content = state == null ? WorkspaceText.T("暂无连接操作", "No recent operation") : WorkspaceText.T("查看最近进度", "Recent progress");
        _restTitle.Text = state?.Stage == ConnectionStage.Failed ? WorkspaceText.T("上次操作未完成", "Last operation failed")
            : state is { IsTerminal: false } ? StageTitle(state) : WorkspaceText.T("连接动态", "Connection activity");
        _restDiagnostics.IsVisible = state?.Stage == ConnectionStage.Failed;
        if (state == null) return;
        _title.Text = StageTitle(state);
        _title.Classes.Set("error", state.Stage == ConnectionStage.Failed);
        ToolTip.SetTip(_title, _title.Text);
        var detail = state.Stage == ConnectionStage.Waiting
            ? WorkspaceText.T($"TUN 已停止，连接暂时中断。已等待 {state.WaitSeconds} 秒 / 上限 90 秒。", $"TUN stopped; connection interrupted. Waited {state.WaitSeconds}s / limit 90s.")
            : state.Stage == ConnectionStage.Cancelled
                ? WorkspaceText.T("选择已保留；取消不会恢复原模式，请查看实际状态。", "Selection preserved; cancellation does not restore the previous mode. Check actual status.")
                : state.Detail ?? (state.IsTerminal ? WorkspaceText.T("实际运行状态已更新。", "Actual runtime status updated.")
                    : WorkspaceText.T("按实际操作更新进度。", "Progress follows the actual operation."));
        if (_state.CancelRequested && !state.IsTerminal)
            detail = WorkspaceText.T("正在取消，等待已开始的清理完成…", "Cancelling; finishing cleanup already in progress…");
        _detail.Text = detail;
        ToolTip.SetTip(_detail, detail);
        _detail.Classes.Set("error", state.Stage == ConnectionStage.Failed);
        _cancel.IsVisible = !state.IsTerminal && !state.Disconnecting && !state.UpdatingProxy;
        _cancel.IsEnabled = _state.CanCancel;
        _retry.IsVisible = state.Stage is ConnectionStage.Failed or ConnectionStage.Cancelled;
        _diagnostics.IsVisible = state.Stage == ConnectionStage.Failed;
        _steps.Text = state.UpdatingProxy ? WorkspaceText.T("更新系统代理 → 确认应用结果", "Update system proxy → Confirm application")
            : state.Disconnecting ? WorkspaceText.T("停止内核 → 确认退出", "Stop core → Confirm exit")
            : _state.HadHandoff ? WorkspaceText.T("停止 → 等待端口 → 启动 → 验证", "Stop → Wait for port → Start → Verify")
            : WorkspaceText.T("停止 → 启动 → 验证", "Stop → Start → Verify");
        var icon = state.Stage switch { ConnectionStage.Completed => "connected", ConnectionStage.Cancelled => "close", ConnectionStage.Failed => "failed", _ => "switching" };
        var brush = state.Stage == ConnectionStage.Failed ? "WorkspaceDanger" : state.IsTerminal ? "WorkspaceText" : "WorkspaceWarning";
        if (_iconKey != icon + brush) { _iconKey = icon + brush; _icon.Content = WorkspaceIcons.Create(icon, 14, brush); }
    }

    private static string StageTitle(ConnectionTransition state) => state.Stage switch
    {
        ConnectionStage.Preparing => WorkspaceText.T("正在准备配置", "Preparing configuration"),
        ConnectionStage.Stopping => state.Disconnecting ? WorkspaceText.T("正在断开连接", "Disconnecting") : WorkspaceText.T("正在停止旧内核", "Stopping previous core"),
        ConnectionStage.Waiting => WorkspaceText.T($"等待端口 · {state.WaitSeconds}/90 秒", $"Waiting for ports · {state.WaitSeconds}/90s"),
        ConnectionStage.Starting => WorkspaceText.T("正在启动新内核", "Starting new core"),
        ConnectionStage.Verifying => WorkspaceText.T("正在验证连接", "Verifying connection"),
        ConnectionStage.ApplyingProxy => WorkspaceText.T("正在更新系统代理", "Updating system proxy"),
        ConnectionStage.Completed => state.UpdatingProxy ? WorkspaceText.T("系统代理已更新", "System proxy updated") : state.Disconnecting
            ? WorkspaceText.T("已断开连接", "Disconnected") : WorkspaceText.T("连接已就绪", "Connection ready"),
        ConnectionStage.Cancelled => WorkspaceText.T("操作已取消", "Operation cancelled"),
        _ => WorkspaceText.T("操作未完成", "Operation failed")
    };

    private static Button ActionButton(string name, string zh, string en)
    {
        var button = new Button { Name = name, Content = WorkspaceText.T(zh, en), Classes = { "rowAction" }, FontSize = 11,
            MinHeight = 28, MinWidth = 24, Padding = new(3, 2) };
        AutomationProperties.SetName(button, WorkspaceText.T(zh, en));
        return button;
    }
    private static void Bind(AvaloniaObject target, AvaloniaProperty property, string resource)
        => target.Bind(property, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(resource));
    public void Dispose() { _disposed = true; _detailsFlyout.Hide(); _timer.Stop(); _state.Dismiss(); }
}
