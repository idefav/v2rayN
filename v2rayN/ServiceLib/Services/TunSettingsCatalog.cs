namespace ServiceLib.Services;

public enum TunFieldKind { Choice, Preset, List, RuleSets }
public sealed record TunChoice(string Value, string Chinese, string English)
{
    public string Label => TunSettingsText.T(Chinese, English);
    public override string ToString() => Label;
}

public sealed record TunFieldDefinition(
    string Key, string Property, string Group, bool Singbox, TunFieldKind Kind,
    string Chinese, string English, string HintChinese, string HintEnglish,
    string Example, string Default, IReadOnlyList<TunChoice>? Options = null,
    bool LinuxOnly = false, string? Dependency = null, bool Expert = false, bool NotOnMac = false)
{
    public string Title => TunSettingsText.T(Chinese, English);
    public string Hint => TunSettingsText.T(HintChinese, HintEnglish);
    public IReadOnlyList<TunChoice> Choices => Options ?? [];
    public string Help => TunSettingsCatalog.Help(this);
}

/// <summary>Shared bilingual descriptions drive the form, searchable offline help and exported manual.</summary>
public static class TunSettingsCatalog
{
    private static TunChoice C(string value, string zh, string en) => new(value, zh, en);
    private static readonly TunChoice Auto = C("", "自动 · 保持默认", "Automatic · keep default");
    private static readonly TunChoice[] OnOff = [C("true", "开启", "On"), C("false", "关闭", "Off")];
    private static readonly TunChoice[] OptionalOnOff = [Auto, .. OnOff];
    private static readonly TunChoice[] Nat =
    [
        Auto, C("endpoint_independent", "端点无关", "Endpoint independent"),
        C("address_dependent", "按目标 IP 区分", "Address dependent"),
        C("address_and_port_dependent", "按目标 IP 与端口区分", "Address and port dependent"),
    ];

    public static readonly IReadOnlyList<TunFieldDefinition> Fields =
    [
        new("auto_route", "AutoRoute", "basic", false, TunFieldKind.Choice, "自动路由", "Auto route",
            "将系统默认流量送入 TUN；关闭后需要自行管理路由。", "Route default system traffic into TUN; otherwise routes must be managed separately.",
            "true", "true", OnOff),
        new("strict_route", "StrictRoute", "basic", false, TunFieldKind.Choice, "严格路由", "Strict route",
            "应用平台支持的严格接管规则；可改善 DNS 接管，也可能影响其他网络软件。", "Apply platform-specific strict routing; this can affect DNS and other networking software.",
            "true", "true", OnOff, NotOnMac: true),
        new("stack", "Stack", "basic", false, TunFieldKind.Choice, "协议栈", "Network stack",
            "一般保留当前值。mixed 使用系统 TCP 与 gVisor UDP，gvisor 需要内核编译支持。", "Keep the current stack unless troubleshooting. Mixed uses system TCP and gVisor UDP; gvisor requires a matching build.",
            "gvisor", "gvisor",
            [Auto, C("gvisor", "gVisor · 用户空间协议栈", "gVisor · userspace"), C("system", "System · 系统协议栈", "System · native stack"), C("mixed", "Mixed · 系统 TCP / gVisor UDP", "Mixed · system TCP / gVisor UDP")]),
        new("mtu", "Mtu", "basic", false, TunFieldKind.Preset, "最大传输单元", "MTU",
            "优先保留现有值；自动使用客户端首选值 1280，新配置默认 9000。不会改变物理网卡 MTU。", "Keep the existing value. Automatic uses the client's first preset (1280); new configs default to 9000. The physical interface MTU is unchanged.",
            "9000", "9000", [Auto, .. Global.TunMtus.Select(n => C(n.ToString(), n.ToString(), n.ToString()))]),
        new("enable_ipv6", "EnableIPv6Address", "basic", false, TunFieldKind.Choice, "TUN IPv6", "TUN IPv6",
            "配置 TUN 的 IPv6 地址；公网 IPv6 是否可用还取决于网络和出口。", "Configure IPv6 on TUN; public IPv6 also depends on the network and outbound.",
            "false", "false", OnOff),
        new("icmp_routing", "IcmpRouting", "basic", false, TunFieldKind.Choice, "ICMP 策略", "ICMP policy",
            "控制 ping 等 ICMP 流量；模拟应答不代表目标真正可达。", "Control ICMP such as ping; synthetic replies do not prove destination reachability.",
            "default", "default",
            [Auto, C("default", "兼容默认 · 模拟应答", "Compatible default · synthetic reply"), C("direct", "直连", "Direct"), C("rule", "按路由规则", "Follow routing"), C("unreachable", "返回不可达", "Unreachable"), C("drop", "静默丢弃", "Drop")]),
        new("legacy_protect", "EnableLegacyProtect", "basic", false, TunFieldKind.Choice, "兼容前置", "Compatibility pre-core",
            "其他内核运行节点时，可使用 sing-box 前置接管 TUN；不会替换所选节点。", "Use a sing-box TUN pre-core for nodes handled by another core; the selected node is retained.",
            "true", "true", OnOff),
        new("ipv4_address", "IPv4Address", "basic", false, TunFieldKind.Preset, "主 IPv4 地址", "Primary IPv4 address",
            "TUN 自身地址与前缀。自动 DNS 使用同网段的下一个可用地址。", "TUN address and prefix. Automatic DNS uses its next usable address in the prefix.",
            "172.18.0.1/30", "172.18.0.1/30", [Auto, .. Global.TunIPv4Address.Select(a => C(a, a, a))]),
        new("ipv6_address", "IPv6Address", "basic", false, TunFieldKind.Preset, "主 IPv6 地址", "Primary IPv6 address",
            "启用 TUN IPv6 后生效；建议保留可容纳 DNS / 对端地址的前缀。", "Used when TUN IPv6 is enabled; leave room for a DNS or peer address.",
            "fdfe:dcba:9876::1/126", "fdfe:dcba:9876::1/126", [Auto, .. Global.TunIPv6Address.Select(a => C(a, a, a))], Dependency: "ipv6"),
        new("interface_name", "InterfaceName", "basic", true, TunFieldKind.Preset, "设备名称", "Interface name",
            "留空沿用平台默认值。macOS 自定义名称必须是 utun 加数字；Linux 最多 15 字节。", "Leave empty for the platform default. macOS requires utun plus digits; Linux allows at most 15 bytes.",
            "utun20 / singbox_tun", "automatic", [Auto]),
        new("additional_addresses", "AdditionalAddresses", "basic", true, TunFieldKind.List, "额外 TUN 地址", "Additional TUN addresses",
            "为同一设备添加额外 CIDR 地址，保留顺序；关闭 IPv6 时不输出 IPv6 条目。", "Add ordered CIDR addresses to the same device; IPv6 entries are retained but not emitted while IPv6 is off.",
            "10.77.0.1/30", "empty"),
        new("dns_mode", "DnsMode", "dns", true, TunFieldKind.Choice, "DNS 处理方式", "DNS handling",
            "控制 TUN 内的 DNS 入口与自动接管行为。", "Control DNS entry points and automatic interception in TUN.",
            "hijack", "automatic",
            [C("", "自动 · 沿用现有行为", "Automatic · keep existing behavior"), C("hijack", "接管 TUN 内的 DNS", "Intercept TUN DNS"), C("native", "仅处理虚拟 DNS 入口", "Virtual DNS entry only"), C("disabled", "不配置 TUN DNS", "Do not configure TUN DNS")]),
        new("dns_address", "DnsAddress", "dns", true, TunFieldKind.List, "虚拟 DNS 入口", "Virtual DNS entry",
            "TUN 内部接收 DNS 请求的地址，不是公共 DNS 上游。", "TUN's internal DNS entry points, not public upstream resolvers.",
            "172.18.0.2", "next TUN address", [C("172.18.0.2", "172.18.0.2", "172.18.0.2")], Dependency: "dns"),
        new("udp_mapping", "UdpMapping", "udp", true, TunFieldKind.Choice, "UDP 映射方式", "UDP mapping",
            "端点无关会对同一来源端口复用映射；其余方式按目标 IP 或 IP 与端口分别建立映射。", "Endpoint-independent mapping reuses a source-port mapping; the other modes separate mappings by destination address or address and port.",
            "endpoint_independent", "endpoint_independent", Nat),
        new("udp_filtering", "UdpFiltering", "udp", true, TunFieldKind.Choice, "UDP 接收过滤", "UDP filtering",
            "端点无关接受任意远端来源；其余方式仅接受已发送过数据的目标 IP 或 IP 与端口。", "Endpoint-independent filtering accepts any remote endpoint; dependent filtering accepts only contacted addresses or address/port pairs.",
            "endpoint_independent", "endpoint_independent", Nat),
        new("udp_timeout", "UdpTimeout", "udp", true, TunFieldKind.Preset, "UDP 空闲超时", "UDP idle timeout",
            "会话空闲多久后回收。过短可能影响语音或游戏，过长会保留更多状态。", "Expire idle sessions after this duration. Short values can disrupt calls or games; long values retain more state.",
            "5m", "5m", [Auto, .. new[] { "1m", "5m", "10m", "30m" }.Select(a => C(a, a, a))]),
        new("udp_nat_max", "UdpNatMax", "udp", true, TunFieldKind.Preset, "UDP 会话上限", "UDP session limit",
            "达到上限时回收最近最少使用的会话。自动值由内核按设备内存选择，不代表无限。", "The least recently used session is evicted at the limit. Automatic sizing depends on memory; it is not unlimited.",
            "8192", "automatic", [Auto, .. new[] { "4096", "8192", "16384" }.Select(a => C(a, a, a))]),
        new("route_address", "RouteAddress", "routes", true, TunFieldKind.List, "捕获地址范围", "Captured CIDRs",
            "留空使用默认接管范围；填写后使用指定系统路由。未捕获流量不会进入后续代理分流。", "Empty uses the default capture range; custom CIDRs install specific system routes. Uncaptured traffic never reaches proxy routing.",
            "203.0.113.0/24", "empty", Dependency: "auto_route"),
        new("route_exclude_address", "RouteExcludeAddress", "routes", false, TunFieldKind.List, "绕过地址范围", "Excluded CIDRs",
            "这些地址绕过 TUN 系统路由，适合保留局域网或已有 VPN 的访问路径。", "Bypass TUN system routes for these ranges, for example a LAN or an existing VPN.",
            "192.168.0.0/16", "empty",
            [C("10.0.0.0/8", "10.0.0.0/8 · 私网", "10.0.0.0/8 · private"), C("172.16.0.0/12", "172.16.0.0/12 · 私网", "172.16.0.0/12 · private"), C("192.168.0.0/16", "192.168.0.0/16 · 私网", "192.168.0.0/16 · private")], Dependency: "auto_route"),
        new("route_address_set", "RouteAddressSet", "routes", true, TunFieldKind.List, "捕获规则集", "Captured rule sets",
            "从下方已定义资源中多选；仅规则集中的目标 IP CIDR 能用于系统路由，域名规则不能替代 CIDR。", "Select declared resources below; system routing uses destination IP CIDRs, not domain rules.",
            "geoip-cn", "empty", Dependency: "auto_route"),
        new("route_exclude_address_set", "RouteExcludeAddressSet", "routes", true, TunFieldKind.List, "绕过规则集", "Excluded rule sets",
            "将规则集里的目标 IP CIDR 加入 TUN 绕过范围；这不同于把已捕获流量设置为直连。", "Bypass TUN for destination IP CIDRs in the rule sets; this differs from routing already-captured traffic directly.",
            "office-net", "empty", Dependency: "auto_route"),
        new("rule_sets", "RuleSets", "routes", true, TunFieldKind.RuleSets, "规则集来源", "Rule-set sources",
            "添加本地文件或 HTTP(S) 资源，选择二进制 SRS 或源 JSON；仅加载被引用的资源。", "Declare local files or HTTP(S) resources, choosing binary SRS or source JSON; only referenced resources are loaded.",
            "office-net · local · binary · /path/office.srs", "empty"),
        new("auto_redirect", "AutoRedirect", "linux", true, TunFieldKind.Choice, "自动重定向", "Auto redirect",
            "使用 Linux nftables 改善接管及 Docker 网络兼容，需要自动路由和系统权限；默认不启用。", "Use Linux nftables for capture and Docker-network compatibility; requires auto route and system permission. Off by default.",
            "true", "false", OptionalOnOff, LinuxOnly: true, Dependency: "auto_route"),
        new("include_interface", "IncludeInterface", "linux", true, TunFieldKind.List, "仅接管这些网卡", "Included interfaces",
            "留空不限制网卡。与网卡排除列表互斥；命名空间内的网卡可能需要手动填写。", "Empty does not restrict interfaces. Mutually exclusive with excluded interfaces; namespace interfaces may need manual entry.",
            "br-lan", "empty", LinuxOnly: true, Dependency: "auto_route"),
        new("exclude_interface", "ExcludeInterface", "linux", true, TunFieldKind.List, "绕过这些网卡", "Excluded interfaces",
            "与包含列表互斥。严格路由下，应同时考虑返回路径所经过的接口。", "Mutually exclusive with included interfaces. Under strict routing, also account for return-path interfaces.",
            "docker0", "empty", LinuxOnly: true, Dependency: "auto_route"),
        new("include_uid", "IncludeUid", "linux", true, TunFieldKind.List, "接管用户 UID", "Included UIDs",
            "按数字 UID 筛选来源用户，允许与 UID 范围和排除规则组合；0 是 root，不是自动值。", "Filter by numeric source UID; combine with ranges and exclusions. UID 0 means root, not automatic.",
            "1000", "empty", LinuxOnly: true, Dependency: "auto_route"),
        new("exclude_uid", "ExcludeUid", "linux", true, TunFieldKind.List, "绕过用户 UID", "Excluded UIDs",
            "让指定 UID 的流量绕过 TUN；填写运行相关服务的实际用户 ID。", "Bypass TUN for the specified UIDs; use the actual user ID running the service.",
            "1001", "empty", LinuxOnly: true, Dependency: "auto_route"),
        new("include_uid_range", "IncludeUidRange", "linux", true, TunFieldKind.List, "接管 UID 范围", "Included UID ranges",
            "使用 起始:结束，包含两个端点；支持多段。", "Use start:end, inclusive; multiple ranges are supported.",
            "1000:1999", "empty", LinuxOnly: true, Dependency: "auto_route"),
        new("exclude_uid_range", "ExcludeUidRange", "linux", true, TunFieldKind.List, "绕过 UID 范围", "Excluded UID ranges",
            "排除指定 UID 段；起始值不能大于结束值。", "Exclude UID ranges; the start must not exceed the end.",
            "1500:1599", "empty", LinuxOnly: true, Dependency: "auto_route"),
        new("include_mac_address", "IncludeMacAddress", "linux", true, TunFieldKind.List, "接管设备 MAC", "Included MAC addresses",
            "适合 Linux 网关按局域网设备接管，需要自动重定向；与 MAC 排除列表互斥。", "Select LAN devices on a Linux gateway; requires auto redirect and is mutually exclusive with MAC exclusions.",
            "00:11:22:33:44:55", "empty", LinuxOnly: true, Dependency: "auto_redirect"),
        new("exclude_mac_address", "ExcludeMacAddress", "linux", true, TunFieldKind.List, "绕过设备 MAC", "Excluded MAC addresses",
            "让这些局域网设备绕过网关 TUN 接管，需要自动路由和自动重定向。", "Bypass gateway TUN capture for these LAN devices; requires auto route and auto redirect.",
            "66:77:88:99:aa:bb", "empty", LinuxOnly: true, Dependency: "auto_redirect"),
        new("netns", "NetNs", "linux", true, TunFieldKind.Preset, "网络命名空间", "Network namespace",
            "默认使用当前空间；选择已有名称或路径。该页面不创建或删除命名空间。", "Default uses the current namespace; choose an existing name or path. This page does not create or delete namespaces.",
            "vpn-ns / /run/netns/vpn-ns", "empty", [Auto], LinuxOnly: true),
        new("exclude_mptcp", "ExcludeMptcp", "linux", true, TunFieldKind.Choice, "MPTCP 绕过", "MPTCP bypass",
            "开启后 MPTCP 直接绕过代理。需要自动路由和自动重定向；关闭时按内核默认拒绝。", "When enabled, MPTCP bypasses the proxy directly. Requires auto route and auto redirect; the default is to reject unsupported MPTCP.",
            "false", "false", OptionalOnOff, LinuxOnly: true, Dependency: "auto_redirect"),
        new("auto_redirect_input_mark", "AutoRedirectInputMark", "linux", true, TunFieldKind.Preset, "输入连接标记", "Input connection mark",
            "通常保留自动值；仅在与现有 nftables 标记冲突时调整。支持十进制或 0x 十六进制。", "Keep automatic unless an existing nftables mark conflicts. Decimal and 0x hexadecimal values are accepted.",
            "0x2023", "0x2023", [Auto], LinuxOnly: true, Dependency: "auto_redirect", Expert: true),
        new("auto_redirect_output_mark", "AutoRedirectOutputMark", "linux", true, TunFieldKind.Preset, "输出连接标记", "Output connection mark",
            "用于自动重定向的输出连接；需与本机防火墙策略协调。", "Mark output connections for auto redirect; coordinate with local firewall policy.",
            "0x2024", "0x2024", [Auto], LinuxOnly: true, Dependency: "auto_redirect", Expert: true),
        new("auto_redirect_reset_mark", "AutoRedirectResetMark", "linux", true, TunFieldKind.Preset, "重置连接标记", "Reset connection mark",
            "用于重定向预匹配的连接重置；通常无需更改。", "Mark resets during redirect pre-matching; normally no change is needed.",
            "0x2025", "0x2025", [Auto], LinuxOnly: true, Dependency: "auto_redirect", Expert: true),
        new("auto_redirect_nfqueue", "AutoRedirectNfQueue", "linux", true, TunFieldKind.Preset, "NFQueue 队列", "NFQueue number",
            "自动重定向预匹配使用的队列号；范围 0–65535。", "Queue used for redirect pre-matching; range 0–65535.",
            "100", "100", [Auto], LinuxOnly: true, Dependency: "auto_redirect", Expert: true),
        new("iproute2_table_index", "IpRoute2TableIndex", "linux", true, TunFieldKind.Preset, "路由表编号", "Routing table index",
            "自动路由使用的 Linux 路由表；避免与已有策略路由冲突。", "Linux table used by auto route; avoid conflicts with existing policy routing.",
            "2022", "2022", [Auto], LinuxOnly: true, Dependency: "auto_route", Expert: true),
        new("iproute2_rule_index", "IpRoute2RuleIndex", "linux", true, TunFieldKind.Preset, "路由规则起始编号", "Routing rule start index",
            "自动路由生成规则的起始编号；保留默认值可减少与其他软件的冲突。", "Starting priority for generated routing rules; keep the default unless coordinating other software.",
            "9000", "9000", [Auto], LinuxOnly: true, Dependency: "auto_route", Expert: true),
        new("auto_redirect_iproute2_fallback_rule_index", "AutoRedirectFallbackRuleIndex", "linux", true, TunFieldKind.Preset, "回退规则编号", "Fallback rule index",
            "系统默认路由查询后的回退规则；自动重定向启用后生效。", "Fallback after system default routing lookups; used when auto redirect is enabled.",
            "32768", "32768", [Auto], LinuxOnly: true, Dependency: "auto_redirect", Expert: true),
        new("loopback_address", "LoopbackAddress", "basic", true, TunFieldKind.List, "TCP 回接地址", "TCP loopback addresses",
            "访问这些地址的 TCP 连接会回接到来源地址，适合特定本机或网关场景；普通使用留空。", "TCP connections to these addresses connect back to the source address; leave empty for normal usage.",
            "10.7.0.1", "empty", Expert: true),
    ];

    public static string GroupTitle(string group) => group switch
    {
        "basic" => TunSettingsText.T("基础与地址", "Basics & addresses"),
        "dns" => TunSettingsText.T("DNS 接管", "DNS handling"),
        "udp" => TunSettingsText.T("UDP 会话", "UDP sessions"),
        "routes" => TunSettingsText.T("捕获范围", "Capture ranges"),
        "linux" => TunSettingsText.T("Linux 高级", "Linux advanced"),
        _ => TunSettingsText.T("使用帮助", "Help"),
    };

    public static string Help(TunFieldDefinition field)
    {
        var t = TunSettingsText.T;
        var choices = field.Choices.Count == 0
            ? t("可逐条添加、修改或移除；留空时保留自动或默认范围。", "Add, edit or remove individual entries. Empty retains the automatic or default scope.")
            : string.Join("\n", field.Choices.Select(c => $"• {c.Label} ({(c.Value.Length == 0 ? "auto" : c.Value)})"));
        if (field.Key == "dns_mode") choices += "\n" + t("auto 保留旧行为；hijack 接管 TUN DNS；native 仅处理发往虚拟入口的请求；disabled 不自动配置或接管 TUN DNS。四者均不改变混合端口 DNS。", "Auto preserves existing behavior; hijack intercepts TUN DNS; native handles only requests sent to the virtual entry; disabled does not automatically configure or intercept TUN DNS. Mixed-port DNS is unchanged.");
        var scenario = field.Group switch
        {
            "dns" => t("需要明确 DNS 接管边界，或排查部分域名解析异常时。", "When defining DNS capture boundaries or troubleshooting domain resolution."),
            "udp" => t("语音、游戏或 UDP 会话遇到空闲断连、映射复用或资源占用问题时。", "When calls, games or UDP sessions have idle disconnects, mapping reuse or resource issues."),
            "routes" => t("只接管指定网络，或为局域网、公司网络和已有 VPN 保留原路径时。", "When capturing specific networks or retaining LAN, corporate-network or existing VPN routes."),
            "linux" => t("在 Linux 主机或网关上按来源管理接管范围，或调整与现有防火墙的协作时。", "When managing capture by source on a Linux host or gateway, or integrating with an existing firewall."),
            _ => t("首次配置 TUN，或排查路由、协议栈、接口地址及网络兼容问题时。", "When configuring TUN or troubleshooting routes, stacks, interface addresses or compatibility."),
        };
        var dependency = field.Dependency switch
        {
            "auto_route" => t("需要启用自动路由。", "Requires auto route."),
            "auto_redirect" => t("需要 Linux、自动路由和自动重定向。", "Requires Linux, auto route and auto redirect."),
            "ipv6" => t("需要启用 TUN IPv6。", "Requires TUN IPv6."),
            "dns" => t("DNS 模式关闭时不输出此字段。", "Not emitted when DNS handling is disabled."),
            _ => t("无额外开关依赖。", "No additional switch dependency."),
        };
        var effect = field.Group switch
        {
            "dns" => t("变更 DNS 接管不会自动修改上游解析服务器。native / disabled 只调整 TUN 入站，混合代理端口及用户显式路由独立保留。", "DNS capture does not change upstream resolvers. Native/disabled affect TUN; mixed ports and explicit user routing remain independent."),
            "udp" => t("这些设置控制 TUN 内部会话，不保证公网 NAT 类型，也不会让不支持 UDP 的出口获得 UDP 能力。", "These settings control TUN sessions; they do not guarantee a public NAT type or add UDP support to an outbound."),
            "routes" => t("此处决定流量是否进入 TUN。进入 TUN 后才执行代理分流；未捕获或排除流量可能直接使用原网络。规则集必须包含目标 IP CIDR。", "These controls decide whether traffic enters TUN. Proxy routing runs afterwards; bypassed traffic uses its original network. Rule sets need destination IP CIDRs."),
            "linux" => t("修改后可能重建 Linux 路由或防火墙规则。未满足依赖的值保留在草稿中，不会通过关闭父开关静默启用其他功能。", "Changes may rebuild Linux routes or firewall rules. Inactive values remain in the draft and do not silently enable their parent features."),
            _ => t("有效配置变化时需要重新加载内核，现有连接可能中断；配置相同时保留运行连接。", "Effective changes require a core reload and may interrupt connections; unchanged configurations preserve the running connections."),
        };
        if (field.Key.EndsWith("_mark", StringComparison.Ordinal) || field.Key.StartsWith("iproute2_", StringComparison.Ordinal)
            || field.Key is "auto_redirect_nfqueue" or "auto_redirect_iproute2_fallback_rule_index")
            dependency += "\n" + t("自定义值为 0 时仍使用内核默认值；UID 列表中的 0 则表示 root。", "A custom zero still uses the core default; UID zero instead means root.");
        var defaultText = field.Default switch
        {
            "true" => t("开启", "On"), "false" => t("关闭", "Off"), "empty" => t("留空", "Empty"),
            "automatic" => t("自动，保留内核默认值", "Automatic; retain the core default"),
            "next TUN address" => t("同网段的下一个可用 TUN 地址", "Next usable TUN address in the prefix"),
            _ => field.Default
        };
        return $"{field.Title}\n{field.Key}\n\n{t("用途", "Purpose")}\n{field.Hint}\n\n{t("默认值", "Default")}\n{defaultText}\n\n{t("各选项区别", "Option differences")}\n{choices}\n\n{t("适用场景", "When to use")}\n{scenario}\n\n{t("平台 / 版本 / 依赖", "Platform / version / requirements")}\n{(field.LinuxOnly ? "Linux" : field.NotOnMac ? "Windows / Linux" : "macOS / Windows / Linux")}{(field.Singbox ? " · sing-box · " + t("本页适配基线为 1.14+", "this page targets 1.14+") : "")}\n{dependency}\n\n{t("修改影响", "Impact of changes")}\n{effect}\n\n{t("示例", "Example")}\n{field.Example}\n\n{t("故障排查", "Troubleshooting")}\n{t("先恢复本组选项默认值，检查字段错误、实际 TUN 内核及版本，再查看应用日志；避免同时修改多个接管层。", "Restore this group's defaults, check field errors and the actual TUN core/version, then inspect application logs; change one capture layer at a time.")}";
    }

    public static string Manual => "# " + TunSettingsText.T("TUN 设置使用说明", "TUN settings guide") + "\n\n"
        + TunSettingsText.T("默认保留现有行为。保存不会自动开启 TUN；启用完整模板时由模板管理。界面显示的配置不等于网络已验证。", "Defaults preserve existing behavior. Saving does not enable TUN. An enabled full template owns its configuration. Configured state is not proof of network reachability.")
        + "\n\n" + string.Join("\n\n---\n\n", Fields.Select(f => "## " + f.Help));
}
