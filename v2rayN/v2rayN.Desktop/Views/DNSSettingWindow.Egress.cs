using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using ServiceLib.Services;
using v2rayN.Desktop.Common;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace v2rayN.Desktop.Views;

public partial class DNSSettingWindow
{
    private readonly StackPanel _overrides = new() { Spacing = 8 };
    private readonly ComboBox _defaultNode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _overrideNode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _overrideAddress = new() { PlaceholderText = "https://dns.example.com/dns-query" };
    private bool _egressUpdating;

    private static TextBlock Copy(string text, bool muted = false)
    {
        var result = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (muted) { result.Classes.Add("muted"); result.FontSize = 12; }
        return result;
    }

    private static Border Card(string title, params Control[] controls)
    {
        var body = new StackPanel { Spacing = 12 };
        var heading = Copy(title); heading.Classes.Add("sectionTitle"); body.Children.Add(heading);
        foreach (var control in controls) body.Children.Add(control);
        var card = new Border { Child = body, Margin = new Thickness(0, 0, 0, 16) };
        card.Classes.Add("settingsGroup");
        return card;
    }

    private static void BindValue(AvaloniaObject control, AvaloniaProperty property, string path) =>
        control.Bind(property, new Binding(path) { Mode = BindingMode.TwoWay });

    private Button ActionLink(string text, string target)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not MainWindow main) return;
            if (target == "Tun") await main.OpenSettingsAsync("Tun");
            else await main.NavigateAsync(target);
        };
        return button;
    }

    private void BuildEgressLayout()
    {
        Tag = WorkspaceText.T("DNS 与流量出口", "DNS & traffic egress");
        var original = (DockPanel)Content!;
        var oldTabs = original.Children.OfType<TabControl>().Single();
        var customTabs = oldTabs.Items.OfType<TabItem>().Skip(2).ToArray();
        oldTabs.Items.Clear();
        var footer = (StackPanel)btnSave.Parent!;
        footer.Children.Clear();
        var root = new DockPanel { Margin = new Thickness(8) };
        Content = root;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        btnSave.Content = WorkspaceText.T("保存并应用", "Save & apply"); btnSave.Classes.Add("primary");
        btnCancel.Content = WorkspaceText.T("取消", "Cancel");
        actions.Children.Add(btnCancel); actions.Children.Add(btnSave);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var error = Copy(""); error.Bind(TextBlock.TextProperty, new Binding(nameof(DNSSettingViewModel.EgressError)));
        error.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("WorkspaceDanger"));
        DockPanel.SetDock(error, Dock.Bottom); root.Children.Add(error);

        var policyFields = new StackPanel { Spacing = 12 };
        var mode = new ComboBox { Name = "cmbDnsEgressMode", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { WorkspaceText.T("兼容原有配置（迁移默认）", "Legacy (migration default)"), WorkspaceText.T("跟随域名分流规则", "Follow domain routing"), WorkspaceText.T("统一使用当前代理", "Use current proxy") } };
        BindValue(mode, ComboBox.SelectedIndexProperty, nameof(DNSSettingViewModel.EgressModeIndex));
        policyFields.Children.Add(mode);
        policyFields.Children.Add(Copy(WorkspaceText.T("直连域名使用直连解析；代理域名的 DNS 经对应节点发送。仅 IP、端口或进程规则无法提前确定的解析使用默认出口。", "Direct domains use direct DNS; proxied domains resolve through their node. Queries that cannot match before IP, port or process routing use the default egress."), true));
        policyFields.Children.Add(Copy(WorkspaceText.T("未命中 DNS 路由时", "Unmatched DNS egress")));
        policyFields.Children.Add(_defaultNode);
        _defaultNode.Bind(IsEnabledProperty, new Binding(nameof(DNSSettingViewModel.IsDefaultDnsEditable)));
        policyFields.Children.Add(Copy(WorkspaceText.T("代理 DNS 失败即返回失败，不回退直连。动态组使用同一出站组，不能保证每次命中同一节点。", "Proxy DNS fails closed. Dynamic groups share the outbound group, not necessarily the same node."), true));
        policyFields.Bind(IsEnabledProperty, new Binding(nameof(DNSSettingViewModel.IsManagedEgressEditable)));
        var policy = new StackPanel();
        policy.Children.Add(Card(WorkspaceText.T("DNS 出口策略 · sing-box", "DNS egress · sing-box"), policyFields));
        var udp = new CheckBox { Content = WorkspaceText.T("阻断代理 UDP（直连规则仍生效）", "Block proxy UDP (preserve direct routes)") };
        BindValue(udp, CheckBox.IsCheckedProperty, nameof(DNSSettingViewModel.BlockProxyUdp));
        udp.Bind(IsEnabledProperty, new Binding(nameof(DNSSettingViewModel.IsUdpPolicyEditable)));
        var quic = new CheckBox { Content = WorkspaceText.T("阻断 UDP 443 / QUIC", "Block UDP 443 / QUIC") };
        BindValue(quic, CheckBox.IsCheckedProperty, nameof(DNSSettingViewModel.BlockQuic));
        quic.Bind(IsEnabledProperty, new Binding(nameof(DNSSettingViewModel.IsUdpPolicyEditable)));
        var capability = Copy("", true); capability.Bind(TextBlock.TextProperty, new Binding(nameof(DNSSettingViewModel.NodeCapability)));
        policy.Children.Add(Card(WorkspaceText.T("UDP 与浏览器流量", "UDP & browser traffic"),
            Copy(WorkspaceText.T("默认：所有被接管的 UDP 按实际目标分流，包括游戏、STUN、WebRTC 和 QUIC。系统 HTTP 代理不能接管所有 UDP。", "All intercepted UDP follows target routing, including games, STUN, WebRTC and QUIC. System HTTP proxy does not capture all UDP."), true),
            udp, quic, capability,
            Copy(WorkspaceText.T("WebRTC 按媒体 / STUN 目标执行路由，出口可能与网页不同。DNS 模式不会自动放行 QUIC。", "WebRTC follows media/STUN targets and may differ from the page egress. DNS mode does not enable QUIC."), true),
            ActionLink(WorkspaceText.T("TUN 接管设置", "TUN settings"), "Tun"),
            ActionLink(WorkspaceText.T("节点 UDP 设置", "Node UDP settings"), "Servers"),
            ActionLink(WorkspaceText.T("检查 UDP 与浏览器实际出口", "Check UDP & browser egress"), "ProxyCheck")));

        var upstream = new StackPanel();
        var basic = ExtractFields(gridBasicDNSSettings);
        var upstreamFields = new StackPanel { Spacing = 12 };
        foreach (var pair in basic.Where(p => p.Key is >= 1 and <= 3)) upstreamFields.Children.Add(pair.Value);
        upstreamFields.Bind(IsEnabledProperty, new Binding(nameof(DNSSettingViewModel.IsSimpleDNSEnabled)));
        upstream.Children.Add(Card(WorkspaceText.T("全局 DNS 上游", "Global DNS upstreams"), upstreamFields,
            Copy(WorkspaceText.T("Bootstrap 为直连引导解析例外；新策略要求填写 IP。自定义 DNS 接管对应内核后，本页托管 DNS 不再用于该内核。", "Bootstrap is a direct bootstrap exception; new policies require an IP. Custom DNS replaces managed DNS for its core."), true)));
        var add = new Button { Content = WorkspaceText.T("添加 / 更新覆盖", "Add / update override"), HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) =>
        {
            if (ViewModel == null || _overrideNode.SelectedItem is not DnsNodeChoice node) return;
            if (DnsEgressPolicy.ValidateUpstreams(_overrideAddress.Text, true) is { } invalid) { ViewModel.EgressError = invalid; return; }
            ViewModel.SetNodeOverride(node.Id, _overrideAddress.Text!.Trim()); ViewModel.EgressError = ""; RefreshOverrides();
        };
        var overrideBody = new StackPanel { Spacing = 8, Children = { _overrides, _overrideNode, _overrideAddress, add } };
        overrideBody.Bind(IsEnabledProperty, new Binding(nameof(DNSSettingViewModel.IsManagedEgressEditable)));
        upstream.Children.Add(Card(WorkspaceText.T("节点上游覆盖", "Node upstream overrides"),
            Copy(WorkspaceText.T("按稳定节点 ID 关联；未设置的节点继承全局上游。删除覆盖恢复继承。兼容模式不应用覆盖。", "Bound to stable node IDs. Nodes inherit global upstreams; removing an override restores inheritance. Legacy mode does not apply overrides."), true), overrideBody));
        upstream.Children.Add(Card(WorkspaceText.T("本地名称", "Local names"), Copy(WorkspaceText.T(".local 与 .local. 优先本地解析并直连，保留局域网访问。", ".local and .local. resolve locally and route direct."), true)));

        var advanced = new StackPanel();
        foreach (var pair in basic.Where(p => p.Key >= 5)) advanced.Children.Add(pair.Value);
        foreach (var pair in ExtractFields(gridAdvancedDNSSettings).Where(p => p.Key > 0)) advanced.Children.Add(pair.Value);
        advanced.Bind(IsEnabledProperty, new Binding(nameof(DNSSettingViewModel.IsSimpleDNSEnabled)));
        var advancedBody = new StackPanel();
        advancedBody.Children.Add(Card(WorkspaceText.T("解析、缓存与 Hosts", "Resolution, cache & hosts"), advanced));
        foreach (var custom in customTabs)
        {
            var content = (Control)custom.Content!; custom.Content = null; content.MinHeight = 420;
            advancedBody.Children.Add(new Expander { Header = custom.Header, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) });
        }
        var tabs = new TabControl { Name = "dnsEgressTabs", HorizontalContentAlignment = HorizontalAlignment.Stretch };
        tabs.Items.Add(new TabItem { Header = WorkspaceText.T("出口策略", "Egress policy"), Content = new ScrollViewer { Content = policy } });
        tabs.Items.Add(new TabItem { Header = WorkspaceText.T("DNS 上游", "DNS upstreams"), Content = new ScrollViewer { Content = upstream } });
        tabs.Items.Add(new TabItem { Header = WorkspaceText.T("高级配置", "Advanced"), Content = new ScrollViewer { Content = advancedBody } });
        var summary = new StackPanel();
        var runtime = Copy("", true); runtime.Bind(TextBlock.TextProperty, new Binding(nameof(DNSSettingViewModel.RuntimeSummary)));
        summary.Children.Add(Card(WorkspaceText.T("配置与运行", "Configuration & runtime"),
            Copy(WorkspaceText.T("编辑草稿", "Editing draft")), Copy(WorkspaceText.T("保存后才申请应用；应用失败时旧内核配置仍可能运行。", "Changes apply after saving; an old core configuration may remain active if application fails."), true), runtime,
            Copy(WorkspaceText.T("验证证据：未验证", "Evidence: not verified")),
            ActionLink(WorkspaceText.T("查看代理检查", "Open diagnostics"), "ProxyCheck")));
        summary.Children.Add(Card(WorkspaceText.T("生效边界", "Coverage"), Copy(WorkspaceText.T("TUN 关闭时，浏览器 UDP 接管未确认。\n加密 DNS / 浏览器 DoH 不等同于普通 DNS 劫持。\n完整配置模板优先；新出口策略仅用于 sing-box 托管配置。", "With TUN off, browser UDP interception is unconfirmed.\nBrowser DoH is not ordinary DNS interception.\nFull templates take priority; new policies apply to managed sing-box configurations."), true)));
        var summaryScroll = new ScrollViewer { Content = summary };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,244"), RowDefinitions = new RowDefinitions("*") };
        layout.Children.Add(tabs); layout.Children.Add(summaryScroll); root.Children.Add(layout);
        void Arrange()
        {
            var narrow = layout.Bounds.Width < 820;
            layout.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,244");
            layout.RowDefinitions = new RowDefinitions(narrow ? "*,180" : "*");
            Grid.SetColumn(summaryScroll, narrow ? 0 : 1); Grid.SetRow(summaryScroll, narrow ? 1 : 0);
            summaryScroll.Margin = new Thickness(narrow ? 0 : 16, narrow ? 8 : 0, 0, 0);
        }
        layout.SizeChanged += (_, _) => Arrange();
        Loaded += async (_, _) =>
        {
            if (ViewModel == null) return;
            await ViewModel.Initialization;
            _egressUpdating = true;
            _defaultNode.ItemsSource = ViewModel.DnsNodes;
            _defaultNode.SelectedItem = ViewModel.DnsNodes.FirstOrDefault(n => n.Id == ViewModel.DefaultDnsNodeId);
            _overrideNode.ItemsSource = ViewModel.DnsNodes.Where(n => n.Id.Length > 0).ToList();
            _egressUpdating = false; RefreshOverrides(); Arrange();
        };
        _defaultNode.SelectionChanged += (_, _) =>
        {
            if (!_egressUpdating && ViewModel != null && _defaultNode.SelectedItem is DnsNodeChoice node) ViewModel.DefaultDnsNodeId = node.Id;
        };
    }

    private static SortedDictionary<int, Control> ExtractFields(Grid grid)
    {
        var fields = new SortedDictionary<int, Control>();
        var rows = grid.Children.GroupBy(Grid.GetRow).ToArray();
        grid.Children.Clear();
        foreach (var row in rows)
        {
            var field = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 12) };
            foreach (var control in row.OrderBy(Grid.GetColumn))
            {
                control.Width = double.NaN; control.Margin = new Thickness(0);
                if (control is TextBlock text) text.TextWrapping = TextWrapping.Wrap;
                else control.HorizontalAlignment = HorizontalAlignment.Stretch;
                field.Children.Add(control);
            }
            fields[row.Key] = field;
        }
        return fields;
    }

    private void RefreshOverrides()
    {
        if (ViewModel == null) return;
        _overrides.Children.Clear();
        foreach (var pair in ViewModel.ReadNodeOverrides())
        {
            var label = ViewModel.DnsNodes.FirstOrDefault(n => n.Id == pair.Key)?.Label ?? pair.Key + " (missing)";
            var edit = new Button { Content = WorkspaceText.T("编辑", "Edit") };
            edit.Click += (_, _) => { _overrideNode.SelectedItem = ViewModel.DnsNodes.FirstOrDefault(n => n.Id == pair.Key); _overrideAddress.Text = pair.Value; };
            var remove = new Button { Content = WorkspaceText.T("移除", "Remove") };
            remove.Click += (_, _) => { ViewModel.SetNodeOverride(pair.Key, null); RefreshOverrides(); };
            _overrides.Children.Add(new StackPanel { Spacing = 6, Children = { Copy(label), Copy(pair.Value, true), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { edit, remove } } } });
        }
        if (_overrides.Children.Count == 0) _overrides.Children.Add(Copy(WorkspaceText.T("所有节点继承全局上游", "All nodes inherit global upstreams"), true));
    }
}
