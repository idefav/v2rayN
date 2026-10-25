using DialogHostAvalonia;
using Avalonia.VisualTree;
using v2rayN.Desktop.Common;
using ServiceLib.Services;

namespace v2rayN.Desktop.Views;

public partial class StatusBarView : ReactiveUserControl<StatusBarViewModel>
{
    private readonly Config _config;
    private readonly DispatcherTimer _coreTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _reloading;
    private string? _coreBrushKey;
    private IDisposable? _coreBrush;
    private string? _exitNode;
    private ExitInfoItem? _exitResult;
    private bool _exitStale;
    private Flyout connectionFlyout => (Flyout)btnConnectionControls.Flyout!;

    public StatusBarView()
    {
        InitializeComponent();
        _config = AppManager.Instance.Config;
        TablePresentation.DisplaySelection(cmbRoutings2, WorkspaceText.T("尚无路由规则", "No routing rules"),
            value => (value as RoutingItem)?.Remarks);
        btnRestart.Click += (_, _) => ViewModel?.ReloadRequested.Publish(RxVoid.Default);
        btnAvailability.Click += async (_, _) => { if (ViewModel != null) await ViewModel.TestServerAvailability(); };
        btnCloseConnection.Click += (_, _) => CloseConnectionControls();
        connectionFlyout.Closed += (_, _) => btnConnectionControls.Focus();
        connectionFlyout.Popup.ShouldUseOverlayLayer = true;
        connectionFlyout.Opened += (_, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            connectionScroll.MaxHeight = Math.Max(120, (window?.Bounds.Height ?? 640) - 112);
            connectionPanel.Width = Math.Min(336, Math.Max(240, (window?.Bounds.Width ?? 600) - 64));
            cmbSystemProxy.Focus();
        };
        _coreTimer.Tick += (_, _) => UpdateCoreState();

        this.WhenActivated(disposables =>
        {
            void ExitChanged(string id) { if (id == _config.IndexId) Dispatcher.UIThread.Post(UpdateExitSummary); }
            ExitInfoService.Instance.Changed += ExitChanged;
            new ActionDisposable(() => ExitInfoService.Instance.Changed -= ExitChanged).DisposeWith(disposables);
            UpdateCoreState();
            _coreTimer.Start();
            new ActionDisposable(_coreTimer.Stop).DisposeWith(disposables);
            AppEvents.DispatcherStatisticsRequested.AsObservable()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(update =>
                {
                    if (!_config.GuiItem.DisplayRealTimeSpeed || !CoreManager.Instance.IsCoreRunning) return;
                    txtDownload.Text = "↓ " + Utils.HumanFy(update.ProxyDown) + "/s";
                    txtUpload.Text = "↑ " + Utils.HumanFy(update.ProxyUp) + "/s";
                }).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.InboundDisplay, v => v.txtInboundDisplay.Text).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel!.InboundLanDisplay).Subscribe(value =>
                txtInboundLanDisplay.Text = value?.Replace(Global.None, "—", StringComparison.OrdinalIgnoreCase)).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel!.RunningServerDisplay).Subscribe(value =>
            {
                txtRunningServerDisplay.Text = string.IsNullOrWhiteSpace(value) ? "—" : value;
                ToolTip.SetTip(txtRunningServerDisplay, txtRunningServerDisplay.Text);
            }).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedProxyDisplay, v => v.txtSpeedProxyDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedDirectDisplay, v => v.txtSpeedDirectDisplay.Text).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel!.LastAvailabilityCheck, v => v.ViewModel!.IsTestingAvailability)
                .ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ =>
                {
                    var result = ViewModel!.LastAvailabilityCheck;
                    txtRunningInfoDisplay.Text = ViewModel.IsTestingAvailability ? WorkspaceText.Testing :
                        result == null ? WorkspaceText.Untested : result.Time > 0 ? result.Time + " ms" : WorkspaceText.Failed;
                    ToolTip.SetTip(txtRunningInfoDisplay, result?.GetValidIp() ?? "—");
                    btnAvailability.IsEnabled = !ViewModel.IsTestingAvailability;
                }).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel!.IsChangingConnection, v => v.ViewModel!.ConnectionError)
                .ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => UpdateControls()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel!.SystemProxySelected, v => v.ViewModel!.EnableTun, v => v.ViewModel!.SelectedRouting)
                .ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => UpdateRuntimeMetadata()).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.EnableTun, v => v.togEnableTun.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SystemProxySelected, v => v.cmbSystemProxy.SelectedIndex).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting, v => v.cmbRoutings2.SelectedItem).DisposeWith(disposables);
            ViewModel!.SetClipboardDataInteraction.RegisterHandler(async interaction =>
            {
                await AvaUtils.SetClipboardData(this, interaction.Input);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);
            ViewModel.PasswordInputInteraction.RegisterHandler(async interaction =>
            {
                CloseConnectionControls();
                interaction.SetOutput(await PasswordInputAsync());
            }).DisposeWith(disposables);
            ViewModel.DispatcherRefreshIconInteraction.RegisterHandler(interaction =>
            {
                Dispatcher.UIThread.Post(RefreshIcon);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);
        });
        if (Utils.IsNonWindows() && !cmbSystemProxy.Items.IsReadOnly) cmbSystemProxy.Items.RemoveAt(cmbSystemProxy.Items.Count - 1);
        RefreshIcon();
    }

    public bool CloseConnectionControls()
    {
        if (!connectionFlyout.IsOpen) return false;
        connectionFlyout.Hide();
        btnConnectionControls.Focus();
        return true;
    }

    public void SetReloading(bool reloading) { _reloading = reloading; UpdateControls(); }

    private void UpdateControls()
    {
        connectionFields.IsEnabled = !_reloading && ViewModel?.IsChangingConnection != true;
        txtConnectionError.Text = ViewModel?.ConnectionError;
        txtConnectionError.IsVisible = !string.IsNullOrWhiteSpace(txtConnectionError.Text);
        UpdateCoreState();
    }

    private void RefreshIcon()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }) return;
        window.Icon = AvaUtils.GetAppIcon(_config.SystemProxyItem.SysProxyType);
        var icons = TrayIcon.GetIcons(Application.Current);
        if (icons?.Count > 0) { icons[0].Icon = window.Icon; TrayIcon.SetIcons(Application.Current, icons); }
    }

    private void UpdateCoreState()
    {
        var recovery = AppManager.Instance.ConnectionRecovery;
        txtRecoveryState.Text = recovery?.State switch
        {
            RecoveryState.Checking => WorkspaceText.T("检查中", "Checking connection"),
            RecoveryState.Recovering => WorkspaceText.T("恢复中", "Recovering connection"),
            RecoveryState.Offline => WorkspaceText.T("等待网络", "Waiting for network"),
            RecoveryState.Failed => WorkspaceText.T("连接异常", "Connection unavailable"),
            RecoveryState.Degraded => WorkspaceText.T("探测异常，已保留连接", "Probe unavailable; connection preserved"),
            _ => string.Empty,
        };
        var connectionFailed = !string.IsNullOrWhiteSpace(ViewModel?.ConnectionError);
        if (connectionFailed) txtRecoveryState.Text = WorkspaceText.T("配置应用失败", "Configuration failed");
        txtRecoveryState.Classes.Set("error", connectionFailed || recovery?.State == RecoveryState.Failed);
        txtRecoveryState.IsVisible = !string.IsNullOrEmpty(txtRecoveryState.Text);
        ToolTip.SetTip(txtRecoveryState, connectionFailed ? ViewModel?.ConnectionError
            : recovery?.State == RecoveryState.Degraded && recovery.Error?.StartsWith("External probes") == true
                ? WorkspaceText.T("内核及本地 SOCKS 正常，未自动重启；可使用代理检查进一步验证。",
                    "Local core and SOCKS are healthy; no automatic restart. Use Proxy Check to investigate.")
                : recovery?.Error);
        UpdateExitSummary();
        var running = CoreManager.Instance.IsCoreRunning;
        var applying = _reloading || ViewModel?.IsChangingConnection == true;
        txtCoreState.Text = applying
            ? WorkspaceText.T("正在应用…", "Applying…") : running ? WorkspaceText.Running : WorkspaceText.Stopped;
        txtRuntimeCaption.Text = running ? WorkspaceText.T("当前运行配置", "Running configuration") : WorkspaceText.T("已选活动配置", "Configured active profile");
        var brushKey = applying ? "WorkspaceWarning" : running ? "WorkspaceSuccess" : "WorkspaceMuted";
        if (_coreBrushKey != brushKey)
        {
            _coreBrushKey = brushKey;
            _coreBrush?.Dispose();
            _coreBrush = txtCoreState.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(brushKey));
        }
        if (!running || !_config.GuiItem.DisplayRealTimeSpeed) { txtDownload.Text = "↓ —"; txtUpload.Text = "↑ —"; }
        UpdateRuntimeMetadata();
    }

    private void UpdateRuntimeMetadata()
    {
        var core = AppManager.Instance.RunningCoreType;
        txtCoreName.Text = WorkspaceText.T("内核：", "Core: ") + (CoreManager.Instance.IsCoreRunning && Enum.IsDefined(core)
            ? core == ECoreType.sing_box ? "sing-box" : core.ToString() : WorkspaceText.T("未运行", "Stopped"));
        txtProxyMode.Text = WorkspaceText.T("系统代理：", "System proxy: ") + (_config.SystemProxyItem.SysProxyType switch
        {
            ESysProxyType.ForcedChange => WorkspaceText.T("自动配置", "Automatic"),
            ESysProxyType.ForcedClear => WorkspaceText.T("自动清除", "Clear"),
            ESysProxyType.Unchanged => WorkspaceText.T("保持现状", "Unchanged"),
            ESysProxyType.Pac => "PAC", _ => "—"
        });
        txtTunMode.Text = "TUN: " + (_config.TunModeItem.EnableTun ? WorkspaceText.T("开启", "On") : WorkspaceText.T("关闭", "Off"));
        txtRoutingMode.Text = WorkspaceText.T("路由：", "Routing: ") + (ViewModel?.SelectedRouting?.Remarks ?? "—");
    }

    private void UpdateExitSummary()
    {
        var id = _config.IndexId;
        var result = ExitInfoService.Instance.Get(id ?? "");
        var stale = result?.Ip != null && (result.State != "success" || DateTime.UtcNow - result.CheckedAt > TimeSpan.FromHours(24));
        if (_exitNode == id && ReferenceEquals(_exitResult, result) && _exitStale == stale && exitSummary.Content != null) return;
        _exitNode = id; _exitResult = result; _exitStale = stale;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(ExitPresentation.Cell(result));
        if (stale && result?.State == "success")
            panel.Children.Add(new TextBlock { Text = WorkspaceText.T("旧结果", "Previous result"), Classes = { "muted" } });
        exitSummary.Content = panel;
        ToolTip.SetTip(exitSummary, ExitPresentation.Details(result));
    }

    public void SetCompact(bool compact)
    {
        txtRunningServerDisplay.MaxWidth = double.PositiveInfinity;
        transferSummary.IsVisible = !compact;
        cachedExitRow.IsVisible = !compact;
    }

    private async Task<string?> PasswordInputAsync()
    {
        var obj = await DialogHost.Show(new SudoPasswordInputView());
        var password = obj?.ToString();
        if (password.IsNullOrEmpty()) { togEnableTun.IsChecked = false; return password; }
        AppManager.Instance.LinuxSudoPwd = password;
        return password;
    }
}
