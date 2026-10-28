using System.Globalization;

namespace ServiceLib.Services;

public enum TunPlatform { MacOS, Windows, Linux }
public sealed record TunValidationIssue(string Key, string Message);
public sealed class TunConfigurationException(string message) : Exception(message);

public static class TunSettingsText
{
    public static bool Chinese => (ResUI.Culture ?? CultureInfo.CurrentUICulture).Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public static string T(string zh, string en) => Chinese ? zh : en;
}

/// <summary>The same validation and platform policy is used by the editor and configuration generator.</summary>
public static class SingboxTunSettings
{
    public static TunPlatform CurrentPlatform => OperatingSystem.IsMacOS() ? TunPlatform.MacOS
        : OperatingSystem.IsWindows() ? TunPlatform.Windows : TunPlatform.Linux;

    public static TunPlatform Platform(CoreConfigContext context) => context.IsMacOS ? TunPlatform.MacOS
        : context.IsWindows ? TunPlatform.Windows : TunPlatform.Linux;

    public static List<string> Addresses(TunModeItem item)
    {
        var addresses = new List<string> { item.IPv4Address.NullIfEmpty() ?? Global.TunIPv4Address.First() };
        if (item.EnableIPv6Address) addresses.Add(item.IPv6Address.NullIfEmpty() ?? Global.TunIPv6Address.First());
        addresses.AddRange((item.Singbox?.AdditionalAddresses ?? []).Where(a => item.EnableIPv6Address || !a.Contains(':')));
        return addresses.Distinct(StringComparer.Ordinal).ToList();
    }

    public static List<string> DnsAddresses(TunModeItem item)
    {
        if (item.Singbox?.DnsAddress is { Count: > 0 } custom)
            return custom.Where(a => item.EnableIPv6Address || !a.Contains(':')).ToList();
        var result = new List<string>();
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            var address = Addresses(item).FirstOrDefault(a => TryPrefix(a, out var ip, out _) && ip.AddressFamily == family);
            if (address == null || !TryPrefix(address, out var ip, out var prefix)) continue;
            var bytes = ip.GetAddressBytes();
            var next = (byte[])bytes.Clone();
            var incremented = false;
            for (var i = next.Length - 1; i >= 0; i--)
            {
                if (next[i] != byte.MaxValue) { next[i]++; incremented = true; break; }
                next[i] = 0;
            }
            var usable = incremented && prefix < bytes.Length * 8;
            for (var bit = 0; usable && bit < prefix; bit++)
                if ((bytes[bit / 8] & (1 << (7 - bit % 8))) != (next[bit / 8] & (1 << (7 - bit % 8)))) usable = false;
            if (usable) result.Add(new IPAddress(next).ToString());
        }
        return result;
    }

    public static List<TunValidationIssue> Validate(TunModeItem item, TunPlatform platform)
    {
        var errors = new List<TunValidationIssue>();
        void Error(string key, string zh, string en) => errors.Add(new(key, TunSettingsText.T(zh, en)));
        void Prefixes(string key, IEnumerable<string>? values, AddressFamily? family = null)
        {
            foreach (var value in values ?? [])
                if (!TryPrefix(value, out var ip, out _) || family != null && ip.AddressFamily != family)
                    Error(key, $"无效的 CIDR 地址：{value}", $"Invalid CIDR address: {value}");
        }
        void Ips(string key, IEnumerable<string>? values)
        {
            foreach (var value in values ?? [])
                if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0)
                    Error(key, $"无效的 IP 地址：{value}", $"Invalid IP address: {value}");
        }
        void Choice(string key, string? value, params string[] choices)
        {
            if (!string.IsNullOrEmpty(value) && !choices.Contains(value))
                Error(key, "请选择列表中的选项。", "Choose a supported option.");
        }
        Prefixes("ipv4_address", [item.IPv4Address.NullIfEmpty() ?? Global.TunIPv4Address.First()], AddressFamily.InterNetwork);
        Prefixes("ipv6_address", [item.IPv6Address.NullIfEmpty() ?? Global.TunIPv6Address.First()], AddressFamily.InterNetworkV6);
        Prefixes("route_exclude_address", item.RouteExcludeAddress);
        Choice("stack", item.Stack, "system", "gvisor", "mixed");
        if (item.Mtu is < 0 or > 65535) Error("mtu", "MTU 应为自动（0）或 1–65535。", "MTU must be automatic (0) or 1–65535.");
        var s = item.Singbox;
        if (s == null) return errors;
        Prefixes("additional_addresses", s.AdditionalAddresses);
        Prefixes("route_address", s.RouteAddress);
        Ips("dns_address", s.DnsAddress);
        Ips("loopback_address", s.LoopbackAddress);
        Choice("dns_mode", s.DnsMode, "disabled", "native", "hijack");
        Choice("udp_mapping", s.UdpMapping, "endpoint_independent", "address_dependent", "address_and_port_dependent");
        Choice("udp_filtering", s.UdpFiltering, "endpoint_independent", "address_dependent", "address_and_port_dependent");
        if (!string.IsNullOrEmpty(s.UdpTimeout) && !ValidDuration(s.UdpTimeout))
            Error("udp_timeout", "请输入正的时长，例如 30s、5m、1h30m。", "Enter a positive duration, such as 30s, 5m or 1h30m.");
        if (s.InterfaceName is { Length: > 0 } name)
        {
            if (name.Any(char.IsControl) || name.Contains('/') || name.Contains('\\') || name != name.Trim())
                Error("interface_name", "设备名不能包含控制字符、路径分隔符或首尾空格。", "Device names cannot contain control characters, path separators or surrounding spaces.");
            if (platform == TunPlatform.MacOS && (!Regex.IsMatch(name, @"^utun\d+$") || !uint.TryParse(name.AsSpan(4), out _)))
                Error("interface_name", "macOS 设备名必须是 utun 加数字，例如 utun20；推荐自动选择。", "macOS names must be utun followed by a number, e.g. utun20; automatic selection is recommended.");
            if (platform == TunPlatform.Linux && Encoding.UTF8.GetByteCount(name) > 15)
                Error("interface_name", "Linux 设备名最多 15 个字节。", "Linux device names must fit in 15 bytes.");
        }
        var dnsEnabled = s.DnsMode != "disabled" && (!string.IsNullOrEmpty(s.DnsMode) || item.AutoRoute);
        if (dnsEnabled && errors.All(e => e.Key is not ("ipv4_address" or "ipv6_address" or "additional_addresses" or "dns_address")))
        {
            var addresses = Addresses(item).Select(a => IPAddress.Parse(a.Split('/')[0])).ToHashSet();
            var dns = DnsAddresses(item);
            if (dns.Count == 0) Error("dns_address", "地址中没有可用的后继 DNS 地址，请扩大网段或指定 DNS 地址。", "No usable next DNS address; enlarge the prefix or supply a DNS address.");
            if (dns.Any(a => addresses.Contains(IPAddress.Parse(a))))
                Error("dns_address", "虚拟 DNS 地址不能与 TUN 自身地址相同。", "The virtual DNS address must differ from the TUN's own addresses.");
        }
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in s.RuleSets ?? [])
        {
            if (string.IsNullOrWhiteSpace(source.Tag) || source.Tag.Any(char.IsControl) || !tags.Add(source.Tag))
                Error("rule_sets", "规则集名称不能为空、重复或包含控制字符。", "Rule-set tags must be nonempty, unique and contain no control characters.");
            if (source.Type is not ("local" or "remote") || source.Format is not ("binary" or "source"))
                Error("rule_sets", "规则集来源和格式无效。", "Invalid rule-set source or format.");
            if (source.Type == "local" && string.IsNullOrWhiteSpace(source.Path))
                Error("rule_sets", $"请为规则集 {source.Tag} 选择文件。", $"Choose a file for rule set {source.Tag}.");
            if (source.Type == "remote" && (!Uri.TryCreate(source.Url, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")))
                Error("rule_sets", $"规则集 {source.Tag} 需要有效的 HTTP(S) 地址。", $"Rule set {source.Tag} needs a valid HTTP(S) URL.");
        }
        if (platform != TunPlatform.Linux) return errors;
        if (s.AutoRedirect == true && !item.AutoRoute) Error("auto_redirect", "自动重定向需要启用自动路由。", "Auto redirect requires auto route.");
        if (s.IncludeInterface?.Count > 0 && s.ExcludeInterface?.Count > 0)
            Error("include_interface", "网卡包含列表和排除列表不能同时使用。", "Interface include and exclude lists are mutually exclusive.");
        if ((s.IncludeInterface?.Count > 0 || s.ExcludeInterface?.Count > 0 || s.IncludeUid?.Count > 0 || s.ExcludeUid?.Count > 0 || s.IncludeUidRange?.Count > 0 || s.ExcludeUidRange?.Count > 0) && !item.AutoRoute)
            Error("auto_route", "网卡或 UID 筛选需要启用自动路由。", "Interface and UID filters require auto route.");
        if (s.IncludeMacAddress?.Count > 0 && s.ExcludeMacAddress?.Count > 0)
            Error("include_mac_address", "MAC 包含列表和排除列表不能同时使用。", "MAC include and exclude lists are mutually exclusive.");
        if ((s.IncludeMacAddress?.Count > 0 || s.ExcludeMacAddress?.Count > 0 || s.ExcludeMptcp == true) && (!item.AutoRoute || s.AutoRedirect != true))
            Error("auto_redirect", "MAC 筛选和 MPTCP 绕过需要启用自动路由与自动重定向。", "MAC filters and MPTCP bypass require auto route and auto redirect.");
        foreach (var (key, values) in new[] { ("include_mac_address", s.IncludeMacAddress), ("exclude_mac_address", s.ExcludeMacAddress) })
            foreach (var value in values ?? [])
                if (!Regex.IsMatch(value, @"^(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$"))
                    Error(key, $"无效的 MAC 地址：{value}", $"Invalid MAC address: {value}");
        foreach (var (key, values) in new[] { ("include_uid_range", s.IncludeUidRange), ("exclude_uid_range", s.ExcludeUidRange) })
            foreach (var value in values ?? [])
            {
                var parts = value.Split(':');
                if (parts.Length != 2 || !uint.TryParse(parts[0], out var lo) || !uint.TryParse(parts[1], out var hi) || lo > hi)
                    Error(key, $"UID 范围应为 起始:结束，例如 1000:1999：{value}", $"Use start:end for UID ranges, e.g. 1000:1999: {value}");
            }
        foreach (var (key, value) in new[] { ("iproute2_table_index", s.IpRoute2TableIndex), ("iproute2_rule_index", s.IpRoute2RuleIndex), ("auto_redirect_iproute2_fallback_rule_index", s.AutoRedirectFallbackRuleIndex) })
            if (value < 0) Error(key, "编号不能为负数。", "Indexes cannot be negative.");
        if (s.NetNs?.Any(char.IsControl) == true) Error("netns", "命名空间不能包含控制字符。", "Network namespaces cannot contain control characters.");
        return errors;
    }

    public static void Apply(TunModeItem item, Inbound4Sbox inbound, TunPlatform platform)
    {
        var s = item.Singbox;
        if (s == null) return;
        inbound.address = Addresses(item);
        if (!string.IsNullOrEmpty(s.InterfaceName)) inbound.interface_name = s.InterfaceName;
        inbound.dns_mode = s.DnsMode.NullIfEmpty();
        if (s.DnsMode != "disabled" && s.DnsAddress is { Count: > 0 })
            inbound.dns_address = NonEmpty(DnsAddresses(item));
        inbound.udp_timeout = s.UdpTimeout.NullIfEmpty();
        inbound.udp_mapping = s.UdpMapping.NullIfEmpty();
        inbound.udp_filtering = s.UdpFiltering.NullIfEmpty();
        inbound.udp_nat_max = s.UdpNatMax;
        inbound.loopback_address = NonEmpty(s.LoopbackAddress);
        if (item.AutoRoute)
        {
            inbound.route_address = NonEmpty(s.RouteAddress);
            inbound.route_address_set = NonEmpty(s.RouteAddressSet);
            inbound.route_exclude_address_set = NonEmpty(s.RouteExcludeAddressSet);
        }
        if (platform != TunPlatform.Linux) return;
        inbound.netns = s.NetNs.NullIfEmpty();
        inbound.auto_redirect = s.AutoRedirect;
        if (item.AutoRoute)
        {
            inbound.iproute2_table_index = s.IpRoute2TableIndex;
            inbound.iproute2_rule_index = s.IpRoute2RuleIndex;
            inbound.include_interface = NonEmpty(s.IncludeInterface);
            inbound.exclude_interface = NonEmpty(s.ExcludeInterface);
            inbound.include_uid = NonEmpty(s.IncludeUid);
            inbound.exclude_uid = NonEmpty(s.ExcludeUid);
            inbound.include_uid_range = NonEmpty(s.IncludeUidRange);
            inbound.exclude_uid_range = NonEmpty(s.ExcludeUidRange);
        }
        if (item.AutoRoute && s.AutoRedirect == true)
        {
            inbound.auto_redirect_input_mark = Mark(s.AutoRedirectInputMark);
            inbound.auto_redirect_output_mark = Mark(s.AutoRedirectOutputMark);
            inbound.auto_redirect_reset_mark = Mark(s.AutoRedirectResetMark);
            inbound.auto_redirect_nfqueue = s.AutoRedirectNfQueue;
            inbound.auto_redirect_iproute2_fallback_rule_index = s.AutoRedirectFallbackRuleIndex;
            inbound.exclude_mptcp = s.ExcludeMptcp;
            inbound.include_mac_address = NonEmpty(s.IncludeMacAddress);
            inbound.exclude_mac_address = NonEmpty(s.ExcludeMacAddress);
        }
    }

    public static bool TryPrefix(string value, out IPAddress ip, out int bits)
    {
        ip = IPAddress.None;
        bits = -1;
        var parts = value.Split('/');
        return parts.Length == 2 && IPAddress.TryParse(parts[0], out ip)
            && (ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.ScopeId == 0)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out bits)
            && bits >= 0 && bits <= ip.GetAddressBytes().Length * 8;
    }

    public static bool ValidDuration(string value)
    {
        var matches = Regex.Matches(value, @"(\d+(?:\.\d+)?)(ns|us|µs|μs|ms|s|m|h)");
        if (matches.Count == 0 || string.Concat(matches.Select(m => m.Value)) != value) return false;
        try
        {
            decimal nanos = 0;
            foreach (Match m in matches)
            {
                var factor = m.Groups[2].Value switch { "h" => 3600000000000m, "m" => 60000000000m, "s" => 1000000000m, "ms" => 1000000m, "ns" => 1m, _ => 1000m };
                nanos += decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * factor;
            }
            return nanos >= 1 && nanos <= long.MaxValue;
        }
        catch (OverflowException) { return false; }
    }

    private static List<T>? NonEmpty<T>(List<T>? values) => values is { Count: > 0 } ? [.. values] : null;
    private static string? Mark(uint? value) => value.HasValue ? $"0x{value.Value:x}" : null;
}
