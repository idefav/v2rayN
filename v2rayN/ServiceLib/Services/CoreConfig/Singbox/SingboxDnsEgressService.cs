namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private bool ManagedEgress => context.RawDnsItem?.Enabled != true
        && context.FullConfigTemplate?.Enabled != true && context.SimpleDnsItem.EgressMode != DnsEgressMode.Legacy;
    private readonly Dictionary<string, List<Server4Sbox>> _egressDns = new(StringComparer.Ordinal);

    private string DefaultDnsEgress()
    {
        var id = context.SimpleDnsItem.DefaultEgressNodeId;
        if (context.SimpleDnsItem.EgressMode == DnsEgressMode.CurrentProxy || string.IsNullOrEmpty(id)
            || id == _node.IndexId || id == context.SourceNodeId) return Global.ProxyTag;
        if (!context.AllProxiesMap.TryGetValue(id, out var node))
            throw new SshConfigurationException("默认 DNS 节点不存在 / Default DNS node is missing.");
        return EnsureDnsNode(node);
    }

    private string EnsureDnsNode(ProfileItem node)
    {
        var tag = $"{node.IndexId}-{Global.ProxyTag}-{node.Remarks}";
        if (!_coreConfig.outbounds.Any(o => o.tag == tag) && !(_coreConfig.endpoints?.Any(o => o.tag == tag) ?? false))
            FillRangeProxy(new CoreConfigSingboxService(context with { Node = node }).BuildAllProxyOutbounds(tag), _coreConfig, false);
        return tag;
    }

    private List<Server4Sbox> DnsForEgress(string tag)
    {
        if (_egressDns.TryGetValue(tag, out var cached)) return cached;
        var node = tag == Global.ProxyTag ? _node : context.AllProxiesMap.Values.FirstOrDefault(n =>
            tag == $"{n.IndexId}-{Global.ProxyTag}-{n.Remarks}");
        var overrideId = tag == Global.ProxyTag ? context.SourceNodeId ?? _node.IndexId : node?.IndexId;
        var addresses = overrideId != null && context.SimpleDnsItem.NodeDnsOverrides?.TryGetValue(overrideId, out var custom) == true
            ? custom : context.SimpleDnsItem.RemoteDNS ?? Global.DomainRemoteDNSAddress.First();
        if (DnsEgressPolicy.ValidateUpstreams(addresses, true) is { } error) throw new SshConfigurationException(error);
        var servers = ParseDnsAddresses(addresses);
        var hosts = _coreConfig.dns!.servers.FirstOrDefault(s => s.tag == Global.SingboxHostsDNSTag)?.predefined;
        var prefix = "egress-dns-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tag))).Substring(0, 16).ToLowerInvariant();
        for (var i = 0; i < servers.Count; i++)
        {
            servers[i].tag = $"{prefix}-{i + 1}";
            servers[i].detour = tag;
            servers[i].domain_resolver = servers[i].server is { } address && hosts?.ContainsKey(address) == true
                ? Global.SingboxHostsDNSTag : Global.SingboxLocalDNSTag;
        }
        _coreConfig.dns!.servers.AddRange(servers);
        _egressDns[tag] = servers;
        return servers;
    }

    private List<Server4Sbox> DnsForRule(RulesItem item)
    {
        var target = item.OutboundTag ?? Global.ProxyTag;
        if (target == Global.ProxyTag) return DnsForEgress(Global.ProxyTag);
        if (!context.AllProxiesMap.TryGetValue("remark:" + target, out var node))
            throw new SshConfigurationException("DNS 分流节点不存在 / DNS route node is missing: " + target);
        return DnsForEgress(EnsureDnsNode(node));
    }

    private void ApplyUdpEgressPolicy()
    {
        // Insert guards immediately before the route they protect, preserving direct routes and DNS interception.
        // Apply to all UDP, independent of DNS mode (including custom DNS).
        if (context.FullConfigTemplate?.Enabled == true) return;
        var settings = context.SimpleDnsItem;
        var rules = _coreConfig.route.rules;
        if (settings.BlockQuic)
        {
            var index = rules.FindIndex(r => r.clash_mode != null || r.outbound != null);
            rules.Insert(Math.Max(0, index), new() { network = ["udp"], port = [443], action = "reject" });
        }
        if (!settings.BlockProxyUdp) return;
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (string.IsNullOrEmpty(rule.outbound) || rule.outbound == Global.DirectTag) continue;
            var match = JsonUtils.DeepCopy(rule);
            match.outbound = null;
            match.action = null;
            rules.Insert(i++, new()
            {
                type = "logical", mode = "and", action = "reject",
                rules = [match, new() { network = ["udp"] }],
            });
        }
        if (_coreConfig.route.final != Global.DirectTag)
            rules.Add(new() { network = ["udp"], action = "reject" });
    }
}
