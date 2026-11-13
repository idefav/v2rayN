using DialogHostAvalonia;
using Avalonia.Layout;
using Avalonia.VisualTree;
using v2rayN.Desktop.Common;
using ServiceLib.Services;
using Avalonia.Data;
using Avalonia.Automation;

namespace v2rayN.Desktop.Views;

public partial class StatusBarView : ReactiveUserControl<StatusBarViewModel>
{
    private readonly Config _config;
    private readonly DispatcherTimer _coreTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _reloading;
    // Display-only snapshot; lifecycle decisions always await a fresh query.
    private bool _coreRunning;
    private bool _checkingCore;
    private string? _coreBrushKey;
    private IDisposable? _coreBrush;
    private string? _exitNode;
    private ExitInfoItem? _exitResult;
    private bool _exitStale;
    private List<ProfileItemModel> _pickerItems = [];
    private int _pickerRevision;
    private Control? _pickerSelectionToReveal;
    private bool _pickerFullyExpanded;
    private string _renderedPickerSearch = string.Empty;
    private int _renderedPickerScope = -1;
    internal bool IsServerPickerOpen { get; private set; }
    internal Action<bool>? PickerVisibilityRequested { get; set; }
    private double _headerInset;
    internal void SetWindowHeaderInset(double height)
    {
        _headerInset = height;
        connectionBody.Margin = new Thickness(0, height, 0, 0);
        UpdateHomeLayout();
    }
    public Func<Task>? ConnectRequested { get; set; }
    public Func<Task>? DisconnectRequested { get; set; }
    public Func<Task>? ManageServersRequested { get; set; }
    public Func<Task>? RoutingRequested { get; set; }
    public Func<Task>? DiagnosticsRequested { get; set; }
    public Func<Task>? ApplyRequested { get; set; }
    public event Action<string>? RuntimeSummaryChanged;
    internal ContentControl FeedbackHost => connectionFeedbackHost;
    public Func<bool, Task>? SpeedDisplayChanged { get; set; }
    private bool _updatingSpeed;
    private bool _receivedSpeed;
    private readonly ComboBox cmbSystemProxy = new() { Name = "cmbSystemProxy", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox cmbRoutings2 = new() { Name = "cmbRoutings2", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button btnRestart = new() { Name = "btnRestart" };
    private readonly Button btnAvailability = new() { Name = "btnAvailability" };
    private readonly TextBlock txtInboundDisplay = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock txtInboundLanDisplay = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock txtSpeedProxyDisplay = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock txtSpeedDirectDisplay = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock txtRunningInfoDisplay = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock txtCoreName = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock txtProxyMode = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl exitSummary = new();
    private Control? _coreControls;
    private Control? _proxyControls;
    private string? _displayedState;

    public StatusBarView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateHomeLayout();
        serverListScroll.LayoutUpdated += (_, _) => RevealSelectedServer();
        _config = AppManager.Instance.Config;
        _config.UiItem.ConnectionFavorites ??= [];
        txtSpeedLabel.Text = WorkspaceText.T("实时速度", "Live speed");
        AutomationProperties.SetName(togRealTimeSpeed, txtSpeedLabel.Text);
        AutomationProperties.SetName(togEnableTun, WorkspaceText.T("用户选择的 TUN 模式", "Selected TUN mode"));
        routingIcon.Content = WorkspaceIcons.Create("next", 16);
        btnApplyPending.Content = WorkspaceText.T("应用更改", "Apply");
        btnRetryConnection.Content = WorkspaceText.T("重试", "Retry");
        btnConnectionDiagnostics.Content = WorkspaceText.T("诊断", "Diagnose");
        AutomationProperties.SetName(btnConnectionDiagnostics, WorkspaceText.T("查看连接诊断", "View connection diagnostics"));
        var identity = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var identityFlyout = new Flyout { Content = new ScrollViewer { MaxHeight = 180, Width = 260, Content = identity } };
        btnServerIdentity.Flyout = identityFlyout;
        identityFlyout.Opening += (_, _) => identity.Text = string.Join("\n\n", new[]
        {
            txtNodeLabel.Text + "\n" + txtRunningServerDisplay.Text,
            txtSelectionState.Text,
            txtActualServer.IsVisible ? txtActualServer.Text : null
        }.Where(text => !string.IsNullOrEmpty(text)));
        WorkspaceIcons.SetButton(btnRestart, "refresh", WorkspaceText.T("重启已保存配置", "Restart saved configuration"));
        WorkspaceIcons.SetButton(btnAvailability, "test", ResUI.menuRealPingServer);
        btnRouting.Click += async (_, _) => { if (RoutingRequested != null) await RoutingRequested(); };
        btnConnectionDiagnostics.Click += async (_, _) => { if (DiagnosticsRequested != null) await DiagnosticsRequested(); };
        btnRetryConnection.Click += async (_, _) => await ConnectOrDisconnectAsync(retry: true);
        btnApplyPending.Click += async (_, _) => { if (ApplyRequested != null) await ApplyRequested(); };
        ToolTip.SetTip(togRealTimeSpeed, ResUI.TbSettingsDisplayRealTimeSpeed);
        togRealTimeSpeed.IsChecked = _config.GuiItem.DisplayRealTimeSpeed;
        togRealTimeSpeed.IsCheckedChanged += async (_, _) =>
        {
            if (_updatingSpeed || togRealTimeSpeed.IsChecked == _config.GuiItem.DisplayRealTimeSpeed) return;
            _updatingSpeed = true;
            togRealTimeSpeed.IsEnabled = false;
            try
            {
                _receivedSpeed = false;
                if (SpeedDisplayChanged != null) await SpeedDisplayChanged(togRealTimeSpeed.IsChecked == true);
            }
            catch (Exception ex) { Logging.SaveLog("Live speed toggle", ex); if (ViewModel != null) ViewModel.ConnectionError = ex.Message; }
            finally { _updatingSpeed = false; togRealTimeSpeed.IsEnabled = true; UpdateSpeedDisplay(); }
        };
        txtNodeLabel.Text = WorkspaceText.T("所选节点", "Selected server");
        txtPickerTitle.Text = WorkspaceText.T("更换服务器", "Change server");
        WorkspaceIcons.SetButton(btnConnectionControls, "swap", txtPickerTitle.Text, "WorkspaceAccent");
        WorkspaceIcons.SetButton(btnManageServers, "servers", WorkspaceText.T("管理服务器", "Manage servers"));
        WorkspaceIcons.SetButton(btnCloseConnection, "back", WorkspaceText.T("收起", "Collapse"));
        pickerScope.ItemsSource = new[] { WorkspaceText.T("全部", "All"), WorkspaceText.T("常用", "Favorites") };
        pickerScope.SelectedIndex = 0;
        pickerScope.SelectionChanged += (_, _) => RenderPickerIfFilterChanged();
        AutomationProperties.SetName(pickerScope, WorkspaceText.T("服务器范围", "Server scope"));
        txtServerSearch.PlaceholderText = WorkspaceText.T("搜索名称、地址", "Search name or address");
        txtServerSearch.TextChanged += (_, _) => RenderPickerIfFilterChanged();
        btnConnectionControls.Click += async (_, _) =>
        {
            if (CloseConnectionControls()) return;
            var revision = ++_pickerRevision;
            _pickerItems = [];
            _pickerSelectionToReveal = null;
            _pickerFullyExpanded = false;
            // Reopening starts at the selected server, even if the previous
            // search or favorites scope would have hidden it.
            txtServerSearch.Text = string.Empty;
            pickerScope.SelectedIndex = 0;
            serverListScroll.Offset = default;
            IsServerPickerOpen = true;
            btnConnectionControls.Classes.Add("expanded");
            if (PickerVisibilityRequested != null) PickerVisibilityRequested(true);
            else PresentServerDrawer(Bounds.Width, Bounds.Width, 1, true);
            txtPickerEmpty.Text = WorkspaceText.T("正在加载…", "Loading…");
            txtPickerEmpty.IsVisible = true;
            serverChoices.Children.Clear();
            try
            {
                var items = await AppManager.Instance.ProfileModels(string.Empty, string.Empty) ?? [];
                var measurements = (await ProfileExManager.Instance.GetProfileExs()).ToDictionary(p => p.IndexId);
                if (revision != _pickerRevision || !IsServerPickerOpen) return;
                _pickerItems = items;
                foreach (var item in _pickerItems)
                    if (measurements.TryGetValue(item.IndexId, out var measurement))
                    { item.Delay = measurement.Delay; item.Sort = measurement.Sort; }
                _pickerItems = _pickerItems.OrderBy(p => p.Sort).ToList();
                RenderPicker(revealSelection: true);
            }
            catch (Exception ex)
            {
                if (revision == _pickerRevision && IsServerPickerOpen) txtPickerEmpty.Text = ex.Message;
            }
            if (PickerVisibilityRequested == null) FocusServerSearch();
        };
        btnManageServers.Click += async (_, _) => { if (ManageServersRequested != null) await ManageServersRequested(); };
        btnConnect.Click += async (_, _) => await ConnectOrDisconnectAsync();
        foreach (var label in new[] { ResUI.menuSystemProxyClear, ResUI.menuSystemProxySet, ResUI.menuSystemProxyNothing, ResUI.menuSystemProxyPac })
            cmbSystemProxy.Items.Add(new ComboBoxItem { Content = label });
        cmbRoutings2.ItemsSource = ViewModel?.RoutingItems;
        cmbRoutings2.DisplayMemberBinding = new Binding(nameof(RoutingItem.Remarks));
        TablePresentation.DisplaySelection(cmbRoutings2, WorkspaceText.T("尚无路由规则", "No routing rules"),
            value => (value as RoutingItem)?.Remarks);
        btnRestart.Click += async (_, _) =>
        {
            if (ApplyRequested != null) await ApplyRequested();
            else ViewModel?.ReloadRequested.Publish(RxVoid.Default);
        };
        btnAvailability.Click += async (_, _) => { if (ViewModel != null) await ViewModel.TestServerAvailability(); };
        btnCloseConnection.Click += (_, _) => CloseConnectionControls();
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
                    if (!_config.GuiItem.DisplayRealTimeSpeed || !_coreRunning) return;
                    _receivedSpeed = true;
                    txtDownload.Text = "↓ " + Utils.HumanFy(update.ProxyDown) + "/s";
                    txtUpload.Text = "↑ " + Utils.HumanFy(update.ProxyUp) + "/s";
                    UpdateSpeedDisplay();
                }).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.InboundDisplay, v => v.txtInboundDisplay.Text).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel!.InboundLanDisplay).Subscribe(value =>
                txtInboundLanDisplay.Text = value?.Replace(Global.None, "—", StringComparison.OrdinalIgnoreCase)).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel!.RunningServerDisplay).Subscribe(value =>
            {
                txtRunningServerDisplay.Text = string.IsNullOrWhiteSpace(value) ? "—" : value;
                ToolTip.SetTip(txtRunningServerDisplay, txtRunningServerDisplay.Text);
                ToolTip.SetTip(btnServerIdentity, txtRunningServerDisplay.Text);
                AutomationProperties.SetName(btnServerIdentity, WorkspaceText.T("查看完整节点名称：", "View full server name: ") + txtRunningServerDisplay.Text);
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
            this.WhenAnyValue(v => v.ViewModel!.HasPendingChanges, v => v.ViewModel!.Transition)
                .ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => UpdateControls()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel!.SystemProxySelected, v => v.ViewModel!.EnableTun, v => v.ViewModel!.SelectedRouting)
                .ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => UpdateRuntimeMetadata()).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.EnableTun, v => v.togEnableTun.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SystemProxySelected, v => v.cmbSystemProxy.SelectedIndex).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting, v => v.cmbRoutings2.SelectedItem).DisposeWith(disposables);
            cmbRoutings2.ItemsSource = ViewModel!.RoutingItems;
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
            ViewModel.MarkAuthorizationUiReady();
            ViewModel.DispatcherRefreshIconInteraction.RegisterHandler(interaction =>
            {
                Dispatcher.UIThread.Post(RefreshIcon);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);
        });
        if (Utils.IsNonWindows() && !cmbSystemProxy.Items.IsReadOnly) cmbSystemProxy.Items.RemoveAt(cmbSystemProxy.Items.Count - 1);
        RefreshIcon();
    }

    private async Task ConnectOrDisconnectAsync(bool retry = false)
    {
        if (_reloading || ViewModel?.IsChangingConnection == true) return;
        if (CoreManager.Instance.HasUnconfirmedProcesses || !retry && await CoreManager.Instance.IsCoreRunningAsync())
        { if (DisconnectRequested != null) await DisconnectRequested(); }
        else if (string.IsNullOrEmpty(_config.IndexId) || await AppManager.Instance.GetProfileItem(_config.IndexId) == null)
        { if (ManageServersRequested != null) await ManageServersRequested(); }
        else if (ConnectRequested != null) await ConnectRequested();
    }

    public Control CreateSettingsControls(string section)
    {
        if (section == "SystemProxy")
        {
            if (_proxyControls != null) return _proxyControls;
            var apply = new Button { HorizontalAlignment = HorizontalAlignment.Left };
            WorkspaceIcons.SetButton(apply, "proxy", WorkspaceText.T("应用已保存代理设置 · 即时", "Apply saved proxy settings · Immediate"));
            apply.Click += async (_, _) => { if (ViewModel != null) await ViewModel.ChangeSystemProxyAsync(_config.SystemProxyItem.SysProxyType, true); };
            return _proxyControls = new StackPanel { Spacing = 8, Children = { cmbSystemProxy, apply, txtProxyMode } };
        }
        if (_coreControls != null) return _coreControls;
        var details = new StackPanel { Spacing = 8, Children = { txtCoreName, txtInboundDisplay, txtInboundLanDisplay,
            txtSpeedProxyDisplay, txtSpeedDirectDisplay, exitSummary, txtRunningInfoDisplay } };
        return _coreControls = new StackPanel { Spacing = 8, Children =
        {
            new WrapPanel { Children = { btnRestart, btnAvailability } },
            new Expander { Header = WorkspaceText.T("实际监听与检测状态", "Actual listeners and checks"), Content = details,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch }
        } };
    }

    public bool CloseConnectionControls()
    {
        if (!IsServerPickerOpen) return false;
        IsServerPickerOpen = false;
        _pickerRevision++;
        _pickerSelectionToReveal = null;
        btnConnectionControls.Classes.Remove("expanded");
        if (PickerVisibilityRequested != null) PickerVisibilityRequested(false);
        else PresentServerDrawer(Bounds.Width, Bounds.Width, 0, true);
        btnConnectionControls.Focus();
        return true;
    }

    internal void PresentServerDrawer(double connectionWidth, double panelWidth, double progress, bool narrow)
    {
        var visible = progress > 0 || IsServerPickerOpen;
        pickerClip.IsVisible = serverPicker.IsVisible = visible;
        serverPicker.IsHitTestVisible = IsServerPickerOpen;
        serverPicker.Width = panelWidth;
        pickerClip.Width = narrow && visible ? panelWidth : panelWidth * progress;
        connectionLayout.ColumnDefinitions[0].Width = visible && !narrow ? new GridLength(connectionWidth) : new GridLength(1, GridUnitType.Star);
        // AppKit rounds animated window widths to physical pixels. Grid layout
        // can distribute that half-point remainder to a fixed column; constrain
        // the connection content itself so it cannot stretch during the resize.
        connectionBody.Width = visible && !narrow ? connectionWidth : double.NaN;
        connectionBody.HorizontalAlignment = visible && !narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        connectionColumn.Width = visible && !narrow ? connectionWidth : double.NaN;
        connectionColumn.HorizontalAlignment = visible && !narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Grid.SetColumn(pickerClip, narrow ? 0 : 1);
        pickerClip.Margin = new Thickness(0, narrow || OperatingSystem.IsWindows() ? _headerInset : 0, 0, 0);
        connectionBody.IsVisible = !narrow || !visible;
        connectionColumn.IsVisible = !narrow || !visible;
        serverPicker.RenderTransform = new TranslateTransform((narrow ? panelWidth : 12) * (1 - progress), 0);
        serverPicker.Opacity = narrow ? 1 : .4 + .6 * progress;
        _pickerFullyExpanded = progress >= 1;
        RevealSelectedServer();
    }

    internal void FocusServerSearch() => Dispatcher.UIThread.Post(() =>
    {
        if (IsServerPickerOpen && CanFocusPicker()) txtServerSearch.Focus();
    }, DispatcherPriority.Loaded);

    internal void FocusServerPickerButton()
    {
        if (!IsServerPickerOpen && CanFocusPicker()) btnConnectionControls.Focus();
    }

    private bool CanFocusPicker() => IsEffectivelyVisible && TopLevel.GetTopLevel(this) is Window { IsActive: true } window
        && !window.GetVisualDescendants().OfType<DialogHost>().Any(host => host.IsOpen);

    private void RenderPickerIfFilterChanged()
    {
        // TextChanged is delivered asynchronously. A queued notification for
        // clearing the old query must not rebuild freshly loaded rows and lose
        // their scroll target (or reset an already positioned ScrollViewer).
        if (_renderedPickerSearch != (txtServerSearch.Text?.Trim() ?? string.Empty)
            || _renderedPickerScope != pickerScope.SelectedIndex) RenderPicker();
    }

    private void RenderPicker(bool revealSelection = false)
    {
        // Subsequent searching/favoriting is user navigation, not another
        // request to jump back to the selected node.
        _pickerSelectionToReveal = null;
        serverChoices.Children.Clear();
        var search = txtServerSearch.Text?.Trim() ?? string.Empty;
        _renderedPickerSearch = search;
        _renderedPickerScope = pickerScope.SelectedIndex;
        foreach (var item in _pickerItems.Where(p => ((p.Remarks ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
            || (p.Address ?? "").Contains(search, StringComparison.OrdinalIgnoreCase))
            && (pickerScope.SelectedIndex != 1 || _config.UiItem.ConnectionFavorites.Contains(p.IndexId))))
        {
            var name = string.IsNullOrWhiteSpace(item.Remarks) ? item.Address ?? "—" : item.Remarks;
            var host = item.Address;
            if (System.Net.IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                host = $"[{ip}]";
            var endpoint = string.IsNullOrEmpty(host) ? null : item.Port > 0 ? $"{host}:{item.Port}" : host;
            var identity = string.Join(" · ", new[] { item.ConfigType.ToString(), endpoint, item.SubRemarks }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var delay = item.Delay > 0 ? item.Delay + " ms" : item.Delay < 0 ? WorkspaceText.Failed : WorkspaceText.Untested;
            var selected = item.IndexId == _config.IndexId;
            var active = _coreRunning && item.IndexId == CoreManager.Instance.RuntimeSummary?.ProfileId;
            var button = new Button { Name = "select-" + item.IndexId, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 4, Children =
                {
                    new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = identity + " · " + delay, Classes = { "caption", "muted" }, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = string.Join(" · ", new[] { active ? WorkspaceText.T("正在使用", "In use") : null,
                        selected ? WorkspaceText.T("已选中", "Selected") : null }.Where(s => s != null)),
                        Classes = { "caption" }, IsVisible = selected || active }
                } } };
            AutomationProperties.SetName(button, name + " · " + identity + " · " + delay
                + (selected ? WorkspaceText.T(" · 已选中", " · Selected") : "") + (active ? WorkspaceText.T(" · 正在使用", " · In use") : ""));
            button.Classes.Set("selectedServer", selected);
            if (selected && revealSelection) _pickerSelectionToReveal = button;
            button.Click += (_, _) => { CloseConnectionControls(); ViewModel?.SetDefaultServerRequested.Publish(item.IndexId); };
            var favorite = new Button { Name = "favorite-" + item.IndexId, Classes = { "rowAction" }, VerticalAlignment = VerticalAlignment.Center };
            var favored = _config.UiItem.ConnectionFavorites.Contains(item.IndexId);
            favorite.Content = WorkspaceIcons.Create("favorite", 18, favored ? "WorkspaceAccent" : "WorkspaceMuted");
            AutomationProperties.SetName(favorite, WorkspaceText.T(favored ? "取消常用：" : "设为常用：", favored ? "Unfavorite: " : "Favorite: ") + name + " · " + identity);
            favorite.Click += async (_, _) =>
            {
                favorite.IsEnabled = false;
                var previous = _config.UiItem.ConnectionFavorites.ToList();
                if (!_config.UiItem.ConnectionFavorites.Remove(item.IndexId)) _config.UiItem.ConnectionFavorites.Add(item.IndexId);
                if (await ConfigHandler.SaveConfig(_config) != 0)
                {
                    _config.UiItem.ConnectionFavorites = previous;
                    NoticeManager.Instance.Enqueue(WorkspaceText.T("常用服务器保存失败，已恢复原选择。", "Could not save favorites; the previous selection was restored."));
                }
                RenderPicker();
                var retained = serverChoices.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == favorite.Name);
                if (retained != null) retained.Focus(); else pickerScope.Focus();
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(favorite, 1); row.Children.Add(button); row.Children.Add(favorite);
            serverChoices.Children.Add(row);
        }
        txtPickerEmpty.Text = WorkspaceText.T("没有匹配的服务器，请到服务器管理导入或添加。", "No matching servers. Import or add servers in management.");
        txtPickerEmpty.IsVisible = serverChoices.Children.Count == 0;
        RevealSelectedServer();
    }

    private void RevealSelectedServer()
    {
        if (!IsServerPickerOpen || !_pickerFullyExpanded || _pickerSelectionToReveal is not { } target) return;
        // A dispatcher callback can precede the first measure of async rows.
        // Use actual content coordinates after layout and reveal only once.
        if (target.Bounds.Height <= 0 || serverListScroll.Viewport.Height <= 0
            || serverListScroll.Content is not Control content
            || target.TranslatePoint(default, content) is not { } point
            || serverListScroll.Extent.Height < point.Y + target.Bounds.Height) return;
        _pickerSelectionToReveal = null;
        var top = point.Y - Math.Max(0, (serverListScroll.Viewport.Height - target.Bounds.Height) / 2);
        serverListScroll.Offset = new Vector(0, Math.Clamp(top, 0,
            Math.Max(0, serverListScroll.Extent.Height - serverListScroll.Viewport.Height)));
    }

    public void SetReloading(bool reloading) { _reloading = reloading; UpdateControls(); }

    private void UpdateControls()
    {
        connectionFields.IsEnabled = (!_reloading || CoreManager.Instance.HandoffState == PortHandoffState.Waiting)
            && ViewModel?.IsChangingConnection != true;
        btnConnect.IsEnabled = !_reloading && ViewModel?.IsChangingConnection != true && ViewModel?.Transition is not { IsTerminal: false };
        btnApplyPending.IsEnabled = btnRetryConnection.IsEnabled = btnConnect.IsEnabled;
        btnRestart.IsEnabled = btnConnect.IsEnabled;
        var error = ViewModel?.ConnectionError;
        var summary = error?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        txtConnectionError.Text = summary?.Length > 160 ? summary[..160] + "…" : summary;
        ToolTip.SetTip(txtConnectionError, error);
        connectionErrorPanel.IsVisible = !string.IsNullOrWhiteSpace(error) && ViewModel?.Transition?.Stage != ConnectionStage.Waiting;
        selectionSummary.IsVisible = !connectionErrorPanel.IsVisible;
        UpdateCoreState();
    }

    private void RefreshIcon()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }) return;
        window.Icon = AvaUtils.GetAppIcon(_config.SystemProxyItem.SysProxyType);
    }

    private void UpdateCoreState()
    {
        RenderCoreState();
        _ = RefreshCoreStateAsync();
    }

    private async Task RefreshCoreStateAsync()
    {
        if (_checkingCore) return;
        _checkingCore = true;
        try
        {
            _coreRunning = await CoreManager.Instance.IsCoreRunningAsync();
            RenderCoreState();
        }
        catch (Exception ex) { Logging.SaveLog("StatusBar process health", ex); }
        finally { _checkingCore = false; }
    }

    private void RenderCoreState()
    {
        connectionFields.IsEnabled = (!_reloading || CoreManager.Instance.HandoffState == PortHandoffState.Waiting)
            && ViewModel?.IsChangingConnection != true;
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
        var connectionFailed = CoreManager.Instance.HandoffState != PortHandoffState.Waiting
            && !string.IsNullOrWhiteSpace(ViewModel?.ConnectionError);
        if (connectionFailed) txtRecoveryState.Text = WorkspaceText.T("配置应用失败", "Configuration failed");
        txtRecoveryState.Classes.Set("error", connectionFailed || recovery?.State == RecoveryState.Failed);
        txtRecoveryState.IsVisible = !string.IsNullOrEmpty(txtRecoveryState.Text);
        ToolTip.SetTip(txtRecoveryState, connectionFailed ? ViewModel?.ConnectionError
            : recovery?.State == RecoveryState.Degraded && recovery.Error?.StartsWith("External probes") == true
                ? WorkspaceText.T("内核及本地 SOCKS 正常，未自动重启；可使用代理检查进一步验证。",
                    "Local core and SOCKS are healthy; no automatic restart. Use Proxy Check to investigate.")
                : recovery?.Error);
        UpdateExitSummary();
        var running = _coreRunning;
        var applying = _reloading || ViewModel?.IsChangingConnection == true || ViewModel?.Transition is { IsTerminal: false };
        var actualTun = running && CoreManager.Instance.RunningTunEnabled == true;
        btnConnect.Classes.Set("tun", actualTun && !applying);
        btnConnect.Classes.Set("transitioning", applying);
        var unconfirmed = !applying && CoreManager.Instance.HasUnconfirmedProcesses;
        txtCoreState.Text = applying
            ? WorkspaceText.T("正在切换…", "Switching…") : unconfirmed ? WorkspaceText.T("退出尚未确认", "Exit not confirmed")
            : running ? WorkspaceText.T("内核已就绪", "Core ready") : WorkspaceText.T("未连接", "Disconnected");
        var noSelection = string.IsNullOrEmpty(_config.IndexId);
        var stateIcon = applying ? "switching" : unconfirmed ? "failed" : running ? "connected" : noSelection ? "servers" : "disconnected";
        var stateBrush = applying || unconfirmed ? "WorkspaceWarning" : actualTun ? "ConnectionTunText" : running ? "WorkspaceAccent" : "WorkspaceMuted";
        var displayKey = stateIcon + stateBrush + ViewModel?.Transition?.Disconnecting + ViewModel?.Transition?.UpdatingProxy;
        if (_displayedState != displayKey)
        {
            _displayedState = displayKey;
            connectionStateIcon.Content = WorkspaceIcons.Create(stateIcon, 28, stateBrush);
            WorkspaceIcons.SetButton(btnConnect, applying ? "switching" : running || unconfirmed ? "stop" : "connected",
                applying ? ViewModel?.Transition?.Disconnecting == true ? WorkspaceText.T("正在断开…", "Disconnecting…")
                    : ViewModel?.Transition?.UpdatingProxy == true ? WorkspaceText.T("正在更新代理…", "Updating proxy…") : WorkspaceText.T("正在连接 / 切换…", "Connecting / switching…")
                : unconfirmed ? WorkspaceText.T("继续确认退出", "Confirm exit") : running ? WorkspaceText.T("断开连接", "Disconnect")
                    : noSelection ? WorkspaceText.T("添加服务器", "Add server") : WorkspaceText.T("连接", "Connect"), "WorkspaceOnAccent");
            if (btnConnect.Content is Grid content)
                foreach (var label in content.Children.OfType<TextBlock>())
                    label.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceOnAccent"));
        }
        txtRuntimeCaption.Text = running ? CoreManager.Instance.RunningTunEnabled == true
            ? WorkspaceText.T("TUN 模式正在运行", "TUN mode running") : WorkspaceText.T("普通模式正在运行", "Standard mode running")
            : unconfirmed ? WorkspaceText.T("保留进程记录，请先完成清理。", "Process records retained. Complete cleanup first.") : WorkspaceText.T("内核未运行", "Core stopped");
        if (running && !actualTun && OperatingSystem.IsMacOS())
        {
            var proxy = (Application.Current as App)?.MenuBar?.Snapshot.Proxy ?? ProxyState.Unknown;
            txtRuntimeCaption.Text += " · " + (proxy == ProxyState.Off ? WorkspaceText.T("系统代理未开启", "System proxy off")
                : proxy == ProxyState.Unknown ? WorkspaceText.T("系统代理待确认", "System proxy unknown") : WorkspaceText.T("系统代理已开启", "System proxy on"));
        }
        RuntimeSummaryChanged?.Invoke(txtCoreState.Text + " · " + txtRuntimeCaption.Text);
        ToolTip.SetTip(txtRuntimeCaption, txtRuntimeCaption.Text);
        var runtime = CoreManager.Instance.RuntimeSummary;
        var pending = ViewModel?.HasPendingChanges == true || running && runtime != null
            && (runtime.ProfileId != _config.IndexId || runtime.Tun != _config.TunModeItem.EnableTun);
        txtSelectionState.Text = noSelection ? WorkspaceText.T("先添加或选择一个服务器", "Add or select a server first")
            : pending ? WorkspaceText.T("已选择 / 已保存 · 待应用", "Selected / saved · Pending application")
            : running ? WorkspaceText.T("选择与生效配置一致", "Selection matches the running configuration")
            : WorkspaceText.T("连接时使用所选节点与模式", "Connect using the selected server and mode");
        btnApplyPending.IsVisible = pending && running && !unconfirmed;
        txtActualServer.IsVisible = pending && running && runtime != null;
        txtActualServer.Text = runtime == null ? null : WorkspaceText.T("实际连接：", "Running server: ") + runtime.Server;
        txtSelectionState.IsVisible = !txtActualServer.IsVisible;
        ToolTip.SetTip(txtSelectionState, txtSelectionState.Text);
        ToolTip.SetTip(txtActualServer, txtActualServer.Text);
        var brushKey = applying ? "WorkspaceWarning" : "WorkspaceText";
        if (_coreBrushKey != brushKey)
        {
            _coreBrushKey = brushKey;
            _coreBrush?.Dispose();
            _coreBrush = txtCoreState.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(brushKey));
        }
        if (!running || !_config.GuiItem.DisplayRealTimeSpeed) { txtDownload.Text = "↓ —"; txtUpload.Text = "↑ —"; }
        UpdateSpeedDisplay();
        UpdateRuntimeMetadata();
    }

    private void UpdateRuntimeMetadata()
    {
        var core = AppManager.Instance.RunningCoreType;
        txtCoreName.Text = WorkspaceText.T("内核：", "Core: ") + (_coreRunning && Enum.IsDefined(core)
            ? core == ECoreType.sing_box ? "sing-box" : core.ToString() : WorkspaceText.T("未运行", "Stopped"));
        txtProxyMode.Text = WorkspaceText.T("系统代理：", "System proxy: ") + (_config.SystemProxyItem.SysProxyType switch
        {
            ESysProxyType.ForcedChange => WorkspaceText.T("自动配置", "Automatic"),
            ESysProxyType.ForcedClear => WorkspaceText.T("自动清除", "Clear"),
            ESysProxyType.Unchanged => WorkspaceText.T("保持现状", "Unchanged"),
            ESysProxyType.Pac => "PAC", _ => "—"
        });
        txtTunMode.Text = WorkspaceText.T("用户选择：", "Selected: ") + (_config.TunModeItem.EnableTun ? WorkspaceText.T("开启", "On") : WorkspaceText.T("关闭", "Off"))
            + WorkspaceText.T(" · 实际：", " · Actual: ") + (_coreRunning ? CoreManager.Instance.RunningTunEnabled == true ? "TUN" : WorkspaceText.T("普通", "Standard") : WorkspaceText.T("未连接", "Disconnected"));
        txtRoutingMode.Text = (_coreRunning ? WorkspaceText.T("生效路由：", "Applied routing: ") : WorkspaceText.T("所选路由：", "Selected routing: "))
            + (_coreRunning ? CoreManager.Instance.RuntimeSummary?.Routing ?? "—" : ViewModel?.SelectedRouting?.Remarks ?? "—");
        AutomationProperties.SetName(btnRouting, txtRoutingMode.Text);
        ToolTip.SetTip(btnRouting, txtRoutingMode.Text);
        ToolTip.SetTip(txtTunMode, txtTunMode.Text);
        AutomationProperties.SetHelpText(togEnableTun, txtTunMode.Text);
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
        transferSummary.IsVisible = true;
        UpdateHomeLayout();
    }

    private bool? _shortHome;
    private void UpdateHomeLayout()
    {
        // The drawer changes the window width, but never the connection column's geometry.
        // Reserve space for every home action at the supported 360 x 480 minimum.
        var shortHome = Bounds.Height > 0 && Bounds.Height < 560;
        if (_shortHome == shortHome) return;
        _shortHome = shortHome;
        homeRows.RowDefinitions = new RowDefinitions(shortHome ? "56,80,44,24,48,48,32" : "64,88,44,24,60,60,36");
        homeRows.RowSpacing = shortHome ? 4 : 10;
        homeStack.Margin = shortHome ? new Thickness(12, 8, 12, 0) : new Thickness(20, 12, 20, 0);
        connectionFeedbackHost.Margin = shortHome ? new Thickness(12, 8, 12, 12) : new Thickness(20, 8, 20, 12);
    }

    private void UpdateSpeedDisplay()
    {
        var enabled = _config.GuiItem.DisplayRealTimeSpeed;
        if (!_updatingSpeed) togRealTimeSpeed.IsChecked = enabled;
        txtDownload.IsVisible = txtUpload.IsVisible = enabled;
        if (!enabled || !_coreRunning) _receivedSpeed = false;
        txtSpeedHint.Text = !enabled ? WorkspaceText.T("实时速度未开启", "Live speed is off")
            : !_coreRunning ? WorkspaceText.T("连接后显示实时速度", "Live speed appears when connected")
            : _receivedSpeed ? string.Empty
            : AppManager.Instance.RunningCoreType == ECoreType.Xray
                ? WorkspaceText.T("等待统计数据；若此前未启用统计，需手动重启服务后生效。", "Waiting for statistics. If previously disabled, restart the service manually to enable its API.")
                : WorkspaceText.T("等待速率数据…", "Waiting for speed data…");
        txtSpeedHint.IsVisible = !string.IsNullOrEmpty(txtSpeedHint.Text);
        speedReadout.IsVisible = !txtSpeedHint.IsVisible;
        ToolTip.SetTip(txtSpeedHint, txtSpeedHint.Text);
    }

    private async Task<string?> PasswordInputAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow main })
            main.ShowHideWindow(true);
        const string identifier = "connectionWorkspace";
        var obj = await DialogHost.Show(new SudoPasswordInputView { DialogIdentifier = identifier }, identifier);
        var password = obj?.ToString();
        return password;
    }
}
