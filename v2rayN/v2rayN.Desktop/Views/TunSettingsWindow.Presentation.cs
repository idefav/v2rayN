using ServiceLib.Services;

namespace v2rayN.Desktop.Views;

public partial class TunSettingsWindow
{
    // Presentation copy retained from the accepted prototype. Core schema and validation
    // remain in the shared catalog; the compatibility pre-core keeps its real semantics.
    private static readonly IReadOnlyDictionary<string, (string Zh, string En, string HintZh, string HintEn)> DesignCopy =
        new Dictionary<string, (string, string, string, string)>
    {
        ["auto_route"] = ("自动路由", "Automatic routing", "由 TUN 维护系统路由，接收需要接管的流量。", "Let TUN manage OS routes for captured traffic."),
        ["strict_route"] = ("严格路由", "Strict routing", "收紧接管边界，减少流量绕过 TUN 的可能。", "Tighten routing boundaries to reduce bypasses."),
        ["stack"] = ("网络协议栈", "Network stack", "在兼容性与处理方式之间选择，通常保持自动。", "Choose a compatible packet-processing stack; automatic is recommended."),
        ["mtu"] = ("最大传输单元", "Maximum transmission unit", "遇到分片或兼容问题时，再调整单个数据包大小。", "Adjust packet size when troubleshooting fragmentation or compatibility."),
        ["enable_ipv6"] = ("IPv6", "IPv6", "为 TUN 配置 IPv6 地址与接管范围。", "Configure IPv6 addresses and capture scope."),
        ["icmp_routing"] = ("ICMP 策略", "ICMP policy", "决定诊断报文的处理意图，自动保留原行为。", "Choose the intended handling of diagnostic packets."),
        ["interface_name"] = ("设备名称", "Interface name", "自动分配接口名称，也可指定稳定名称。", "Use an automatically allocated or custom interface name."),
        ["ipv4_address"] = ("IPv4 接口地址", "IPv4 interface addresses", "为虚拟接口分配一个或多个带前缀的地址。", "Assign one or more addresses with prefixes to the virtual interface."),
        ["ipv6_address"] = ("IPv6 接口地址", "IPv6 interface addresses", "启用 IPv6 后生效，可为接口添加多个地址。", "Takes effect with IPv6 enabled; multiple addresses are supported."),
        ["loopback_address"] = ("TCP 回接地址", "TCP loopback addresses", "把发往指定地址的 TCP 连接回接到连接来源。", "Loop TCP connections to these addresses back to their source."),
        ["dns_mode"] = ("DNS 处理方式", "DNS handling mode", "控制 TUN 内的 DNS 入口与自动接管行为。", "Control the virtual DNS entry and automatic interception."),
        ["dns_address"] = ("虚拟 DNS 入口", "Virtual DNS addresses", "TUN 内部接收 DNS 请求的地址，不是公共 DNS 上游。", "Addresses receiving DNS inside TUN, not public DNS upstreams."),
        ["udp_mapping"] = ("UDP 映射方式", "UDP mapping", "控制不同目标之间如何复用本地 UDP 映射。", "Control reuse of local UDP mappings across destinations."),
        ["udp_filtering"] = ("UDP 过滤方式", "UDP filtering", "控制哪些远端可以通过已有映射返回数据。", "Control which remotes may send replies through an existing mapping."),
        ["udp_timeout"] = ("空闲超时", "Idle timeout", "空闲映射超过此时间后释放。", "Release mappings after this period of inactivity."),
        ["udp_nat_max"] = ("最大映射数量", "Maximum mappings", "限制同时保留的 UDP 映射，自动由现有配置决定。", "Limit retained UDP mappings; automatic follows existing configuration."),
        ["route_address"] = ("接管的地址范围", "Included address ranges", "仅把这些目标网段交给 TUN；留空沿用自动范围。", "Send these destination subnets to TUN; empty retains automatic scope."),
        ["route_exclude_address"] = ("排除的地址范围", "Excluded address ranges", "这些目标网段绕过 TUN，由系统原有路径处理。", "These destination subnets bypass TUN using the existing OS path."),
        ["route_address_set"] = ("接管的规则集", "Included rule sets", "从规则集选择需要交给 TUN 的地址范围。", "Use rule sets to define address ranges captured by TUN."),
        ["route_exclude_address_set"] = ("排除的规则集", "Excluded rule sets", "选择应绕过 TUN 的地址规则集。", "Choose address rule sets that should bypass TUN."),
        ["auto_redirect"] = ("自动重定向", "Automatic redirect", "使用 Linux 转发规则辅助 TUN 接管流量。", "Use Linux forwarding rules to assist TUN capture."),
        ["include_interface"] = ("网卡范围", "Interface scope", "选择仅包含或仅排除指定网卡，两种方式互斥。", "Include or exclude interfaces using one exclusive mode."),
        ["include_uid"] = ("包含的用户与范围", "Included users and ranges", "接管指定用户，单个 UID 与范围可以组合。", "Capture selected users; combine individual UIDs and ranges."),
        ["exclude_uid"] = ("排除的用户与范围", "Excluded users and ranges", "绕过指定用户，可与包含列表及 UID 范围组合。", "Bypass selected users; combine with include lists and UID ranges."),
        ["include_mac_address"] = ("MAC 地址范围", "MAC address scope", "按来源 MAC 仅包含或仅排除设备，需自动重定向。", "Include or exclude source devices; automatic redirect is required."),
        ["netns"] = ("网络命名空间", "Network namespace", "使用默认、已有命名空间名称或绝对路径。", "Use the default namespace, an existing name or an absolute path."),
        ["exclude_mptcp"] = ("MPTCP 直连例外", "MPTCP direct exception", "排除 MPTCP 连接；开启后这些连接将直连。", "Exclude MPTCP connections; excluded connections go direct."),
    };

    private static string DesignTitle(TunFieldDefinition field) => DesignCopy.TryGetValue(field.Key, out var copy) ? T(copy.Zh, copy.En) : field.Title;
    private static string DesignHint(TunFieldDefinition field) => DesignCopy.TryGetValue(field.Key, out var copy) ? T(copy.HintZh, copy.HintEn) : field.Hint;

    private static void ApplyDesignChoices(TunSettingsViewModel model)
    {
        foreach (var field in model.Fields)
        {
            if (field.Definition.Key is "dns_mode" or "dns_address") continue;
            for (var i = 0; i < field.Choices.Count; i++)
            {
                var choice = field.Choices[i];
                field.Choices[i] = (field.Definition.Key, choice.Value) switch
                {
                    (_, "") when field.Definition.Kind is TunFieldKind.Choice or TunFieldKind.Preset => new("", "自动", "Automatic"),
                    ("stack", "gvisor") => new("gvisor", "用户态协议栈", "Userspace stack"),
                    ("stack", "system") => new("system", "系统协议栈", "System stack"),
                    ("stack", "mixed") => new("mixed", "混合协议栈", "Mixed stack"),
                    ("mtu", "1500") => new("1500", "1500 · 常规以太网", "1500 · Ethernet"),
                    ("mtu", "1400") => new("1400", "1400 · 保守兼容", "1400 · Conservative"),
                    ("mtu", "9000") => new("9000", "9000 · 大包", "9000 · Large packets"),
                    _ => choice,
                };
            }
        }
        var mtu = model["mtu"];
        if (mtu.Choices.All(c => c.Value != "1400")) mtu.Choices.Insert(mtu.Choices.Count - 1, new("1400", "1400 · 保守兼容", "1400 · Conservative"));
    }
}
