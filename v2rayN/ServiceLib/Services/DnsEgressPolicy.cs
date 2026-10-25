namespace ServiceLib.Services;

/// <summary>Validation shared by the editor and generator; never silently accepts a direct resolver as a proxy upstream.</summary>
public static class DnsEgressPolicy
{
    public static string? ValidateUpstreams(string? addresses, bool proxy)
    {
        var items = Utils.String2List(addresses ?? "") ?? [];
        if (items.Count == 0) return "DNS 上游不能为空 / DNS upstream is required.";
        foreach (var item in items)
        {
            var value = item.Trim();
            if (IPAddress.TryParse(value, out _)) continue;
            if (!proxy && value is "local" or "localhost") continue;
            if (proxy && value is "local" or "localhost") return "代理 DNS 不能使用系统解析 / Proxy DNS cannot use the system resolver.";
            if (!Uri.TryCreate(value.Contains("://") ? value : "udp://" + value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("udp" or "tcp" or "tls" or "https")
                || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
                || (uri.Scheme == "https" && uri.AbsolutePath == "/")
                || uri.Port == 0)
                return "DNS 地址无效 / Invalid DNS upstream: " + value;
            if (uri.Scheme != "https" && (uri.AbsolutePath is not ("" or "/") || uri.Query.Length > 0))
                return "DNS 地址不支持路径或查询参数 / This DNS transport does not support a path or query: " + value;
        }
        return null;
    }

    public static string? Validate(SimpleDNSItem item, IEnumerable<string> nodeIds)
    {
        if (!Enum.IsDefined(item.EgressMode)) return "未知 DNS 出口策略 / Unknown DNS egress mode.";
        if (item.EgressMode == DnsEgressMode.Legacy) return null;
        var ids = nodeIds.ToHashSet(StringComparer.Ordinal);
        if (item.EgressMode == DnsEgressMode.FollowRouting && !string.IsNullOrEmpty(item.DefaultEgressNodeId)
            && !ids.Contains(item.DefaultEgressNodeId)) return "默认 DNS 节点已失效 / Default DNS node is missing.";
        var error = ValidateUpstreams(item.RemoteDNS ?? Global.DomainRemoteDNSAddress.First(), true)
            ?? ValidateUpstreams(item.DirectDNS ?? Global.DomainDirectDNSAddress.First(), false);
        if (error != null) return error;
        if (!IPAddress.TryParse(item.BootstrapDNS ?? Global.DomainPureIPDNSAddress.First(), out _))
            return "Bootstrap DNS 必须为 IP / Bootstrap DNS must be an IP address.";
        foreach (var pair in item.NodeDnsOverrides ?? [])
        {
            if (!ids.Contains(pair.Key)) return "DNS 覆盖引用的节点已失效 / DNS override node is missing: " + pair.Key;
            if (ValidateUpstreams(pair.Value, true) is { } invalid) return invalid;
        }
        return null;
    }

    // GenRoutingUserRule emits domain, IP and process as independent alternatives.
    // Only the shared port/network/protocol/inbound conditions constrain its domain branch.
    public static bool CanMatchDns(RulesItem rule) =>
        rule.Domain is { Count: > 0 } && string.IsNullOrEmpty(rule.Port) && string.IsNullOrEmpty(rule.Network)
        && (rule.Protocol?.Count ?? 0) == 0 && (rule.InboundTag?.Count ?? 0) == 0;
}
