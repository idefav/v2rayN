using DialogHostAvalonia;
using Avalonia.VisualTree;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class StatusBarView : ReactiveUserControl<StatusBarViewModel>
{
    private readonly Config _config;
    private readonly DispatcherTimer _coreTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _reloading;
    private bool? _lastCoreState;
    private IDisposable? _coreBrush;
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
        var running = CoreManager.Instance.IsCoreRunning;
        txtCoreState.Text = running ? WorkspaceText.Running : WorkspaceText.Stopped;
        if (_lastCoreState != running)
        {
            _lastCoreState = running;
            _coreBrush?.Dispose();
            _coreBrush = txtCoreState.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(running ? "WorkspaceSuccess" : "WorkspaceMuted"));
        }
        if (!running || !_config.GuiItem.DisplayRealTimeSpeed) { txtDownload.Text = "↓ —"; txtUpload.Text = "↑ —"; }
    }

    public void SetCompact(bool compact)
    {
        txtRunningServerDisplay.MaxWidth = compact ? 180 : 360;
        transferSummary.IsVisible = !compact;
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
