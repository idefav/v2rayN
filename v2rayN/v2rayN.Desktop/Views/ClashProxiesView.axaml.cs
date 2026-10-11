using v2rayN.Desktop.Common;
using ServiceLib.Services;
using Avalonia.Controls.Templates;
namespace v2rayN.Desktop.Views;

public partial class ClashProxiesView : ReactiveUserControl<ClashProxiesViewModel>
{
    public Func<Task>? ChangeServer { get; set; }
    public Func<string, Task>? EditServer { get; set; }
    private ProfileItem? _node;
    private bool _settingMode;

    private static string ModeName(string? mode) => mode?.ToLowerInvariant() switch
    {
        "rule" => WorkspaceText.T("规则", "Rule"), "global" => WorkspaceText.T("全局", "Global"), "direct" => WorkspaceText.T("直连", "Direct"), _ => mode ?? "—"
    };
    private static string Strategy(string? type) => type?.ToLowerInvariant() switch
    {
        "selector" => WorkspaceText.T("手动选择：选中候选节点后切换。", "Manual selection: choose a candidate, then switch."),
        "fallback" => WorkspaceText.T("故障转移：按顺序使用可用节点，当前节点不可用时自动切换。", "Fallback: uses the first available candidate, switching when it fails."),
        "urltest" => WorkspaceText.T("自动选择：内核根据延迟检测选择节点。", "Automatic selection: the core chooses using latency checks."),
        "loadbalance" => WorkspaceText.T("负载均衡：不同连接可能使用不同节点。", "Load balancing: connections may use different candidates."),
        _ => type ?? "—"
    };
    private async Task RefreshOverview()
    {
        _node = await AppManager.Instance.GetProfileItem(AppManager.Instance.Config.IndexId);
        var grouped = _node?.ConfigType is EConfigType.PolicyGroup or EConfigType.Custom or EConfigType.Outbound;
        singleNode.IsVisible = !grouped;
        singleNode.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        singleNode.Width = Math.Min(780, Math.Max(240, Bounds.Width - 24));
        lstProxyGroups.IsVisible = ViewModel?.ProxyGroups.Count > 1;
        proxyWorkspace.ColumnDefinitions = new ColumnDefinitions(lstProxyGroups.IsVisible ? Bounds.Width < 760 ? "150,8,*" : "240,16,*" : "0,0,*");
        lstProxyDetails.Columns[2].IsVisible = Bounds.Width >= 700;
        lstProxyDetails.Columns[3].Header = WorkspaceText.T("延迟", "Latency");
        lstProxyDetails.Columns[3].Width = new DataGridLength(Math.Max(100, FontSize * 5));
        lstProxyDetails.Columns[0].Width = new DataGridLength(Math.Max(96, FontSize * 4));
        lstProxyDetails.Columns[1].MinWidth = 100;
        proxyWorkspace.IsVisible = grouped && ViewModel?.LoadState == "ready";
        txtNodeName.Text = _node?.Remarks ?? WorkspaceText.T("尚未选择服务器", "No server selected");
        txtNodeProtocol.Text = _node?.ConfigType.ToString() ?? "—";
        txtProxyState.Text = !CoreManager.Instance.IsCoreRunning ? WorkspaceText.T("内核未运行", "Core is not running")
            : !AppManager.Instance.IsRunningCore(ECoreType.sing_box) ? WorkspaceText.T("内核运行中 · 当前内核不提供策略组接口", "Core running · policy group API unavailable")
            : ViewModel?.LoadState switch { "loading" => WorkspaceText.T("正在读取内核状态…", "Loading core state…"), "ready" => WorkspaceText.T("内核运行中", "Core running"), _ => WorkspaceText.T("无法读取策略组状态，请刷新重试", "Policy group state unavailable; refresh to retry") };
        if (!string.IsNullOrEmpty(ViewModel?.LoadError)) txtProxyState.Text += " · " + ViewModel.LoadError;
        var info = _node == null ? null : ExitInfoService.Instance.Get(_node.IndexId);
        exitLocation.Content = ExitPresentation.Cell(info);
        txtExitInfo.Text = WorkspaceText.T("出口 IP：", "Exit IP: ") + (info?.Ip ?? "—") + "\n" + WorkspaceText.T("最近检测：", "Last checked: ")
            + (info?.CheckedAt > DateTime.MinValue ? info.CheckedAt.ToLocalTime().ToString("g") : "—") + " · " + ExitPresentation.State(info);
        ToolTip.SetTip(txtExitInfo, ExitPresentation.Details(info));
        btnEditNode.IsEnabled = btnCheckExit.IsEnabled = _node != null;
        var selector = string.Equals(ViewModel?.SelectedGroup?.Type, "selector", StringComparison.OrdinalIgnoreCase);
        btnActivateProxy.IsVisible = menuProxiesSelectActivity.IsVisible = selector;
        btnActivateProxy.IsEnabled = selector && ViewModel?.SelectedDetail is { IsActive: false } && ViewModel.IsSwitching == false && ViewModel.LoadState == "ready";
        txtStrategy.Text = Strategy(ViewModel?.SelectedGroup?.Type);
        txtTechnical.Text = string.Join("\n", ViewModel?.ProxyGroups.Select(g => $"{g.Name} · {g.Type} · {g.Now}") ?? []) + "\n" + ExitPresentation.Details(info);
        sortingLabel.IsVisible = cmbSorting.IsVisible = menuGroupProxiesDelaytest.IsVisible = togAutoRefresh.IsVisible = grouped;
        cmbRulemode.IsEnabled = AppManager.Instance.IsRunningCore(ECoreType.sing_box) && ViewModel?.ClashModes.Count > 0;
        txtModeHelp.Text = ViewModel?.RuleModeSelected?.ToLowerInvariant() switch
        {
            "global" => WorkspaceText.T("全局：请求使用全局代理策略。", "Global: requests use the global proxy policy."),
            "direct" => WorkspaceText.T("直连：请求不经过代理。", "Direct: requests bypass the proxy."),
            "rule" => WorkspaceText.T("规则：按路由规则选择代理或直连。", "Rule: routing rules choose proxy or direct connections."),
            _ => WorkspaceText.T("此模式由当前内核提供。", "This mode is supplied by the running core.")
        };
        if (!cmbRulemode.IsEnabled) txtModeHelp.Text = WorkspaceText.T("当前无法读取模式；启动支持此接口的内核后可在这里切换。", "Mode unavailable; start a core with this API to switch modes here.");
    }
    public ClashProxiesView()
    {
        InitializeComponent();
        labelNode.Text = WorkspaceText.T("节点名称", "Server");
        labelProtocol.Text = WorkspaceText.T("协议类型", "Protocol");
        labelExit.Text = WorkspaceText.T("出口地区", "Exit location");
        Common.TablePresentation.DisplaySelection(cmbRulemode, "—", value => ModeName(value as string));
        cmbRulemode.ItemTemplate = new FuncDataTemplate<string>((mode, _) => new TextBlock { Text = ModeName(mode) });
        btnChangeNode.Content = WorkspaceText.T("更换服务器", "Change server");
        btnEditNode.Content = WorkspaceText.T("编辑节点", "Edit server");
        btnCheckExit.Content = WorkspaceText.T("检测出口", "Check exit");
        btnActivateProxy.Content = WorkspaceText.T("切换到此节点", "Switch to this server");
        technicalInfo.Header = WorkspaceText.T("技术信息", "Technical information");
        configurationHelp.Header = WorkspaceText.T("配置说明", "Configuration guide");
        txtConfigurationHelp.Text = WorkspaceText.T("普通节点：在服务器页添加或编辑，然后设为活动。\n自动切换：添加服务器 → 策略组，选择成员和策略，保存后激活该组。\n这里展示正在运行的配置；服务器分组只是列表分类，不等同于策略组。", "Single server: add or edit it on Servers, then activate it.\nAutomatic switching: Add server → Policy group, choose members and a strategy, save and activate the group.\nThis page shows the running configuration. Server folders are not policy groups.");
        btnChangeNode.Click += async (_, _) => { if (ChangeServer != null) await ChangeServer(); };
        btnEditNode.Click += async (_, _) => { if (_node != null && EditServer != null) await EditServer(_node.IndexId); };
        btnCheckExit.Click += (_, _) => { if (_node != null) ExitInfoService.Instance.Queue(_node); };
        SizeChanged += async (_, _) => await RefreshOverview();
        lstProxyDetails.DoubleTapped += LstProxyDetails_DoubleTapped;
        KeyDown += ClashProxiesView_KeyDown;

        this.WhenActivated(disposables =>
        {
            var running = CoreManager.Instance.IsCoreRunning;
            var activeId = AppManager.Instance.Config.IndexId;
            var core = AppManager.Instance.RunningCoreType;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (_, _) =>
            {
                if (running == CoreManager.Instance.IsCoreRunning && (activeId == AppManager.Instance.Config.IndexId || !running) && core == AppManager.Instance.RunningCoreType) return;
                running = CoreManager.Instance.IsCoreRunning; activeId = AppManager.Instance.Config.IndexId; core = AppManager.Instance.RunningCoreType;
                if (running && AppManager.Instance.IsRunningCore(ECoreType.sing_box)) await ViewModel!.ProxiesReload();
                else { ViewModel!.LoadState = "unavailable"; ViewModel.ProxyGroups.Clear(); ViewModel.ProxyDetails.Clear(); ViewModel.ClashModes.Clear(); }
                await RefreshOverview();
            };
            timer.Start();
            new ActionDisposable(timer.Stop).DisposeWith(disposables);
            void UpdateExit(string _) => Dispatcher.UIThread.Post(async () => await RefreshOverview());
            ExitInfoService.Instance.Changed += UpdateExit;
            new ActionDisposable(() => ExitInfoService.Instance.Changed -= UpdateExit).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.LoadState, v => v.ViewModel.SelectedGroup, v => v.ViewModel.IsSwitching, v => v.ViewModel.LoadError)
                .ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(values => { _ = RefreshOverview(); }).DisposeWith(disposables);
            _ = RefreshOverview();
            if (AppManager.Instance.IsRunningCore(ECoreType.sing_box)) _ = ViewModel?.ProxiesReload();
            this.OneWayBind(ViewModel, vm => vm.ProxyGroups, v => v.lstProxyGroups.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedGroup, v => v.lstProxyGroups.SelectedItem).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.ProxyDetails, v => v.lstProxyDetails.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedDetail, v => v.lstProxyDetails.SelectedItem).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.ProxiesReloadCmd, v => v.menuProxiesReload).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.GroupProxiesDelayTestCmd, v => v.menuGroupProxiesDelaytest).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.ProxyDelayTestCmd, v => v.menuProxyDelaytest).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ProxiesSelectActivityCmd, v => v.menuProxiesSelectActivity).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ProxiesSelectActivityCmd, v => v.btnActivateProxy).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.SelectedDetail).Subscribe(detail => _ = RefreshOverview()).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.ClashModes, v => v.cmbRulemode.ItemsSource).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.RuleModeSelected)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(mode => { _settingMode = true; try { cmbRulemode.SelectedItem = ViewModel.ClashModes.FirstOrDefault(value =>
                    string.Equals(value, mode, StringComparison.OrdinalIgnoreCase)); } finally { _settingMode = false; } _ = RefreshOverview(); }).DisposeWith(disposables);
            EventHandler<SelectionChangedEventArgs> modeChanged = (_, _) =>
            {
                if (!_settingMode && cmbRulemode.SelectedItem is string mode && ViewModel.RuleModeSelected != mode) ViewModel.RuleModeSelected = mode;
            };
            cmbRulemode.SelectionChanged += modeChanged;
            new ActionDisposable(() => cmbRulemode.SelectionChanged -= modeChanged).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SortingSelected, v => v.cmbSorting.SelectedIndex).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.AutoRefresh, v => v.togAutoRefresh.IsChecked).DisposeWith(disposables);
        });
    }

    private void ClashProxiesView_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F5:
                ViewModel?.ProxiesReload();
                break;

            case Key.Enter:
                ViewModel?.SetActiveProxy();
                break;
        }
    }

    private void LstProxyDetails_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        ViewModel?.SetActiveProxy();
    }
}
