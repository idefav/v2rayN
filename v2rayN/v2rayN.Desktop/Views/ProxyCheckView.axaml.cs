using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ServiceLib.Models.Dto;

namespace v2rayN.Desktop.Views;

public partial class ProxyCheckView : UserControl
{
    public ProxyCheckViewModel Model { get; }
    public ProxyCheckView() : this(new ProxyCheckViewModel()) { }
    internal ProxyCheckView(ProxyCheckViewModel model)
    {
        InitializeComponent(); DataContext = Model = model;
        static string T(string zh, string en) => ProxyCheckText.T(zh, en);
        start.Content = T("开始检查", "Start checks"); cancel.Content = T("取消", "Cancel");
        retry.Content = T("重试失败项", "Retry failed"); saveBaseline.Content = T("保存为基线", "Save as baseline");
        loadBaseline.Content = T("选择基线", "Choose baseline"); export.Content = T("导出报告", "Export report");
        advanced.Header = T("高级选项", "Advanced options");
        pageHint.Text = T("通信、出口与预期，分开判断", "Connectivity, egress and expectations — evaluated separately");
        baselineMenu.Content = T("基线 ▾", "Baseline ▾"); settingsButton.Content = T("检查选项", "Check options");
        environmentLabel.Text = T("当前运行环境", "RUNNING ENVIRONMENT");
        environmentDetails.Header = T("查看环境详情", "Environment details");
        routingLabel.Text = T("路由模式", "Routing"); ingressLabel.Text = T("本地入口", "Local ingress");
        observerCardLabel.Text = T("精确出口探测", "Egress observer"); configureObserver.Content = T("配置服务", "Configure");
        observerCardHint.Text = T("签名观测只证明本次被测流量的出口。系统 DNS 单独标注递归解析器。", "Signed evidence applies to the probed flow. System DNS identifies recursive resolvers separately.");
        overviewLabel.Text = T("本轮概览", "Run overview");
        reachableLabel.Text = T("通信正常", "Reachable"); reachableHint.Text = T("真实流量检查", "Traffic checks");
        matchedLabel.Text = T("出口匹配", "Egress matched"); matchedHint.Text = T("符合配置的预期", "Matches configured expectation");
        attentionLabel.Text = T("需关注", "Needs attention"); attentionHint.Text = T("失败或证据不足", "Failed or insufficient evidence");
        uncoveredLabel.Text = T("未覆盖", "Not covered"); uncoveredHint.Text = T("不支持或已跳过", "Unsupported or skipped");
        resultsLabel.Text = T("检查结果", "Check results"); resultSearch.PlaceholderText = T("搜索目标、协议或出口", "Search target, protocol or egress");
        targetColumn.Text = T("协议 / 目标", "Protocol / target"); pathColumn.Text = T("检查路径", "Path");
        communicationColumn.Text = T("通信", "Connectivity"); egressColumn.Text = T("实际出口", "Observed egress");
        comparisonColumn.Text = T("预期比较", "Expectation"); elapsedColumn.Text = T("耗时", "Time");
        emptyTitle.Text = T("暂无可显示的结果", "No results to display");
        emptyHint.Text = T("开始检查以获取真实证据；已有结果时可调整筛选或清空搜索。", "Start a check to collect evidence, or adjust the filter and clear your search.");
        observerRawJson.Header = T("原始连接配置 JSON", "Raw profile JSON");
        observerSection.Header = T("独立出口探测服务", "Independent egress observer");
        enableObserver.Content = T("启用受控探测（IPv4）", "Use controlled observer (IPv4)");
        importObserver.Content = T("导入连接配置", "Import profile");
        testObserver.Content = T("测试控制接口", "Test control connection");
        observerCredentialLabel.Text = T("私有 API 凭证（仅保存在内存，不写入配置、报告或基线）", "Private API credential (memory only; excluded from settings, reports and baselines)");
        expectedLabel.Text = T("预期出口 IP / CIDR（多个值以逗号分隔）", "Expected egress IP / CIDR (comma-separated)");
        rulesLabel.Text = T("分流预期：目标=IP/CIDR；精确规则：目标|协议|路径=IP/CIDR（路径：socks/native/http-connect/system）", "Expectations: target=IP/CIDR; scoped: target|protocol|path=IP/CIDR (paths: socks/native/http-connect/system)");
        dnsLabel.Text = T("公共 DNS（host:port）", "Public DNS (host:port)");
        localDnsLabel.Text = T("本地 DNS（留空自动发现；系统解析单独检查）", "Local DNS (empty to discover; system resolver is checked separately)");
        samplesLabel.Text = T("采样次数", "Samples"); timeoutLabel.Text = T("超时 / 秒", "Timeout / s"); h3TimeoutLabel.Text = T("HTTP3 超时 / 秒", "HTTP3 timeout / s");
        limits.Text = T("结论仅适用于当前探测目标。保存直连基线前请自行关闭代理。WebRTC/浏览器 DoH、断网保护、网络切换、长连接、局域网/.local、UDP 大包与 MTU 尚未覆盖。", "Conclusions apply only to the probed targets. Turn your proxy off before capturing a direct baseline. Browser WebRTC/DoH, kill switch, network changes, long-lived connections, LAN/.local, large UDP datagrams and MTU are not covered.");
        start.Click += async (_, _) => await Model.StartAsync();
        retry.Click += async (_, _) => await Model.StartAsync(true);
        cancel.Click += (_, _) => Model.Cancel();
        saveBaseline.Click += (_, _) => { try { Model.SaveBaseline(); } catch (Exception ex) { Model.Summary = ex.Message; } };
        loadBaseline.Click += async (_, _) => await LoadBaselineAsync();
        export.Click += async (_, _) => await ExportAsync();
        testObserver.Click += async (_, _) => await Model.TestObserverAsync();
        importObserver.Click += async (_, _) => await ImportObserverAsync();
        configureObserver.Click += (_, _) => ShowOptions(observerSection);
        settingsButton.Click += (_, _) => ShowOptions(advanced);
        SizeChanged += (_, _) => { var compact = Bounds.Width < 960; Classes.Set("compact", compact); ProxyCheckGrid.SetCompact(this, compact); };
        AttachedToVisualTree += async (_, _) => await Model.RefreshEnvironmentAsync();
    }
    private static void ShowOptions(Expander section)
    {
        section.IsExpanded = true;
        Dispatcher.UIThread.Post(() => section.BringIntoView(), DispatcherPriority.Loaded);
    }
    private async Task ImportObserverAsync()
    {
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider; if (provider == null) return;
            var files = await provider.OpenFilePickerAsync(new() { Title = "Observer profile.json", AllowMultiple = false, FileTypeFilter = [new("JSON") { Patterns = ["*.json"] }] });
            if (files.Count == 0) return;
            using var file = files[0]; await using var stream = await file.OpenReadAsync();
            if (stream.Length > 16384) throw new IOException("Observer profile exceeds limit");
            using var reader = new StreamReader(stream); Model.ImportObserverProfile(await reader.ReadToEndAsync());
        }
        catch (Exception ex) { Model.ObserverStatus = ex.Message; }
    }
    private async Task LoadBaselineAsync()
    {
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider; if (provider == null) return;
            var files = await provider.OpenFilePickerAsync(new() { Title = ProxyCheckText.T("选择基线报告", "Choose baseline report"), AllowMultiple = false, FileTypeFilter = [new("JSON") { Patterns = ["*.json"] }] });
            if (files.Count == 0) return;
            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            if (stream.Length > 2 * 1024 * 1024) throw new IOException("Baseline exceeds limit");
            using var reader = new StreamReader(stream); Model.ImportBaseline(await reader.ReadToEndAsync());
        }
        catch (Exception ex) { Model.Summary = ex.Message; }
    }
    private async Task ExportAsync()
    {
        try
        {
            var json = Model.ExportReport();
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider; if (provider == null) return;
            using var file = await provider.SaveFilePickerAsync(new() { Title = ProxyCheckText.T("导出检查报告", "Export check report"), SuggestedFileName = $"proxy-check-{DateTime.Now:yyyyMMdd-HHmmss}.json", DefaultExtension = "json" });
            if (file == null) return;
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
            await using var writer = new StreamWriter(stream); await writer.WriteAsync(json);
        }
        catch (Exception ex) { Model.Summary = ex.Message; }
    }
}
