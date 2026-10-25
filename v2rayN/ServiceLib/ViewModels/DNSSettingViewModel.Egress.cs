namespace ServiceLib.ViewModels;

public sealed record DnsNodeChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

public partial class DNSSettingViewModel
{
    [Reactive] public partial int EgressModeIndex { get; set; }
    [Reactive] public partial string DefaultDnsNodeId { get; set; } = "";
    [Reactive] public partial string NodeOverridesJson { get; set; } = "{}";
    [Reactive] public partial bool BlockProxyUdp { get; set; }
    [Reactive] public partial bool BlockQuic { get; set; }
    [Reactive] public partial string EgressError { get; set; } = "";
    public List<DnsNodeChoice> DnsNodes { get; } = [];
    public string NodeCapability { get; private set; } = "未验证 / Not verified";
    public string RuntimeSummary => $"内核 / Core: {(CoreManager.Instance.IsCoreRunning ? AppManager.Instance.RunningCoreType.ToString() : "未运行 / Not running")}\nTUN 配置 / configured: {(_config.TunModeItem.EnableTun ? "开启 / On" : "关闭 / Off")}\n运行配置与检测结果请查看当前代理和代理检查。";
    private bool _fullTemplateEnabled;
    public bool IsManagedEgressEditable => !SBCustomDNSEnableCompatible && !_fullTemplateEnabled;
    public bool IsUdpPolicyEditable => !_fullTemplateEnabled;
    public bool IsDefaultDnsEditable => IsManagedEgressEditable && EgressModeIndex == (int)DnsEgressMode.FollowRouting;

    private async Task InitEgressAsync()
    {
        var item = _config.SimpleDNSItem;
        _fullTemplateEnabled = (await AppManager.Instance.GetFullConfigTemplateItem(ECoreType.sing_box))?.Enabled == true;
        this.RaisePropertyChanged(nameof(IsManagedEgressEditable));
        this.RaisePropertyChanged(nameof(IsUdpPolicyEditable));
        EgressModeIndex = (int)item.EgressMode;
        DefaultDnsNodeId = item.DefaultEgressNodeId ?? "";
        NodeOverridesJson = JsonUtils.Serialize(item.NodeDnsOverrides ?? new());
        BlockProxyUdp = item.BlockProxyUdp;
        BlockQuic = item.BlockQuic;
        var nodes = await AppManager.Instance.ProfileItems("") ?? [];
        DnsNodes.Add(new("", "当前代理 / Current proxy"));
        DnsNodes.AddRange(nodes.Where(n => Global.SingboxSupportConfigType.Contains(n.ConfigType)
            || n.ConfigType.IsGroupType() || n.ConfigType == EConfigType.Outbound)
            .Select(n => new DnsNodeChoice(n.IndexId, n.Remarks + " · " + n.ConfigType)));
        var current = nodes.FirstOrDefault(n => n.IndexId == _config.IndexId);
        NodeCapability = current?.ConfigType == EConfigType.SSH
            ? (current.GetProtocolExtra().SshUdpForwarding == true ? "SSH UDP 已配置；服务端与端到端能力未验证。" : "SSH UDP 未开启；代理 UDP 不可用。")
            : "UDP 能力未验证；以节点协议与端到端检测为准。";
        this.RaisePropertyChanged(nameof(NodeCapability));
        this.RaisePropertyChanged(nameof(RuntimeSummary));
    }

    public Dictionary<string, string> ReadNodeOverrides() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(NodeOverridesJson) ?? new();

    public void SetNodeOverride(string nodeId, string? upstream)
    {
        var values = ReadNodeOverrides();
        if (upstream == null) values.Remove(nodeId);
        else values[nodeId] = upstream;
        NodeOverridesJson = JsonUtils.Serialize(values);
    }

    private SimpleDNSItem? ValidateEgressDraft()
    {
        EgressError = "";
        try
        {
            var draft = JsonUtils.DeepCopy(_config.SimpleDNSItem);
            draft.EgressMode = (DnsEgressMode)EgressModeIndex;
            draft.DefaultEgressNodeId = DefaultDnsNodeId;
            draft.NodeDnsOverrides = ReadNodeOverrides();
            draft.BlockProxyUdp = BlockProxyUdp;
            draft.BlockQuic = BlockQuic;
            draft.RemoteDNS = string.IsNullOrWhiteSpace(RemoteDNS) ? null : RemoteDNS;
            draft.DirectDNS = string.IsNullOrWhiteSpace(DirectDNS) ? null : DirectDNS;
            draft.BootstrapDNS = string.IsNullOrWhiteSpace(BootstrapDNS) ? null : BootstrapDNS;
            draft.UseSystemHosts = UseSystemHosts;
            draft.AddCommonHosts = AddCommonHosts;
            draft.FakeIP = FakeIP;
            draft.FakeIPRange = FakeIPRange;
            draft.BlockBindingQuery = BlockBindingQuery;
            draft.BlockAAAAQuery = BlockAAAAQuery;
            draft.Strategy4Freedom = Strategy4Freedom;
            draft.Strategy4Proxy = Strategy4Proxy;
            draft.Strategy4ProxyDial = Strategy4ProxyDial;
            draft.Hosts = Hosts;
            draft.DirectExpectedIPs = DirectExpectedIPs;
            draft.ParallelQuery = ParallelQuery;
            draft.ServeStale = ServeStale;
            draft.EnableHappyEyeballs = EnableHappyEyeballs;
            if (IsManagedEgressEditable)
                EgressError = DnsEgressPolicy.Validate(draft, DnsNodes.Select(n => n.Id)) ?? "";
            return EgressError.Length == 0 ? draft : null;
        }
        catch (JsonException)
        {
            EgressError = "节点 DNS 覆盖格式无效 / Invalid node DNS overrides.";
            return null;
        }
    }

    private async Task<bool> ValidateEgressGenerationAsync(SimpleDNSItem draft)
    {
        if (!IsManagedEgressEditable || draft.EgressMode == DnsEgressMode.Legacy) return true;
        var node = await AppManager.Instance.GetProfileItem(_config.IndexId);
        if (node == null || AppManager.Instance.GetCoreType(node, node.ConfigType) != ECoreType.sing_box) return true;
        // Validate the complete draft before changing shared settings or persisting anything.
        // Override the stored raw-DNS flag: the user may be disabling custom DNS in this draft.
        var config = JsonUtils.DeepCopy(_config);
        config.SimpleDNSItem = draft;
        var built = await CoreConfigContextBuilder.Build(config, node, new DNSItem { Enabled = false });
        if (!built.Success)
            EgressError = string.Join(Environment.NewLine, built.ValidatorResult.Errors);
        else
        {
            var generated = new ServiceLib.Services.CoreConfig.CoreConfigSingboxService(built.Context).GenerateClientConfigContent();
            if (!generated.Success) EgressError = generated.Msg ?? ResUI.OperationFailed;
        }
        return EgressError.Length == 0;
    }
}
