namespace ServiceLib.Tests.CoreConfig.Singbox;

[NotInParallel]
public class DnsEgressTests
{
    private static CoreConfigContext Context()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.SimpleDNSItem.EgressMode = DnsEgressMode.FollowRouting;
        config.SimpleDNSItem.RemoteDNS = "https://1.1.1.1/dns-query";
        config.SimpleDNSItem.DirectDNS = "223.5.5.5";
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var a = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "a", "A");
        var b = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, "b", "B");
        var context = CoreConfigTestFactory.CreateContext(config, a, ECoreType.sing_box);
        context.AllProxiesMap["b"] = b;
        context.AllProxiesMap["remark:B"] = b;
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Id = "a", Domain = ["full:a.example"], OutboundTag = Global.ProxyTag },
            new() { Id = "b", Domain = ["full:b.example"], OutboundTag = "B", RuleType = ERuleType.Routing },
            new() { Id = "direct", Domain = ["full:direct.example"], OutboundTag = Global.DirectTag },
            new() { Id = "blocked", Domain = ["full:blocked.example"], OutboundTag = Global.BlockTag },
            new() { Id = "port", Domain = ["full:port.example"], Port = "443", OutboundTag = "B" },
        });
        return context;
    }

    private static SingboxConfig Generate(CoreConfigContext context)
    {
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        if (!result.Success) throw new Exception(result.Msg);
        return JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString()!)!;
    }

    [Test]
    public async Task DomainBranchMatchesEvenWhenRuleAlsoHasIpAndProcessAlternatives()
    {
        var context = Context();
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Id = "mixed", Domain = ["full:mixed.example"], Ip = ["192.0.2.0/24"],
                Process = ["example-app"], OutboundTag = "B" },
        });
        var config = Generate(context);
        var traffic = config.route.rules.Single(r => r.domain?.Contains("mixed.example") == true);
        await traffic.outbound.Should().BeEqualTo("b-proxy-B");
        await (traffic.ip_cidr == null && traffic.process_name == null).Should().BeTrue();
        var dns = config.dns!.rules.Single(r => r.domain?.Contains("mixed.example") == true);
        await config.dns.servers.Single(s => s.tag == dns.server).detour.Should().BeEqualTo(traffic.outbound);

        // A shared port condition still makes the DNS query ambiguous.
        context.RoutingItem.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Id = "mixed-port", Domain = ["full:mixed.example"], Ip = ["192.0.2.0/24"],
                Process = ["example-app"], Port = "443", OutboundTag = "B" },
        });
        config = Generate(context);
        dns = config.dns!.rules.Single(r => r.domain?.Contains("mixed.example") == true);
        await config.dns.servers.Single(s => s.tag == dns.server).detour.Should().BeEqualTo(Global.ProxyTag);
    }

    [Test]
    public async Task MalformedManagedRoutingCannotReturnPartialSuccess()
    {
        foreach (var rules in new[] { "not-json", "{}", "[null]",
            "[{\"Enabled\":true,\"Domain\":[null],\"OutboundTag\":\"block\"}]" })
        {
            var context = Context();
            context.RoutingItem!.RuleSet = rules;
            var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
            await result.Success.Should().BeFalse();
            await (result.Data == null).Should().BeTrue();
        }
    }

    [Test]
    public async Task DnsOnlyRuleGenerationFailureCannotReturnPartialSuccess()
    {
        var context = Context();
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Id = "invalid-dns", RuleType = ERuleType.DNS, Domain = [null!], OutboundTag = Global.BlockTag },
        });
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        await result.Success.Should().BeFalse();
        await (result.Data == null).Should().BeTrue();
    }

    [Test]
    public async Task NoRoutingConfigurationStillUsesExplicitDefaultDnsEgress()
    {
        var context = Context() with { RoutingItem = null };
        context.SimpleDnsItem.DefaultEgressNodeId = "b";
        var config = Generate(context);
        await config.dns!.servers.Single(s => s.tag == config.dns.final).detour.Should().BeEqualTo("b-proxy-B");
        await config.route.rules.Any(r => r.clash_mode == nameof(ERuleMode.Global)).Should().BeTrue();
    }

    [Test]
    public async Task ManagedUpstreamsRetainHostsBootstrapForGlobalAndNodeOverrides()
    {
        var context = Context();
        context.SimpleDnsItem.Hosts = "resolver.example 1.1.1.1\nnode-resolver.example 9.9.9.9";
        context.SimpleDnsItem.RemoteDNS = "https://resolver.example/dns-query";
        context.SimpleDnsItem.NodeDnsOverrides["b"] = "https://node-resolver.example/dns-query";
        var config = Generate(context);
        var servers = config.dns!.servers.Where(s => s.tag.StartsWith("egress-dns-")).ToList();
        await (servers.Count >= 2).Should().BeTrue();
        await servers.All(s => s.domain_resolver == Global.SingboxHostsDNSTag).Should().BeTrue();
        context.SimpleDnsItem.NodeDnsOverrides["b"] = "https://public-resolver.example/dns-query";
        var withoutHost = Generate(context).dns!.servers.Single(s => s.detour == "b-proxy-B");
        await withoutHost.domain_resolver.Should().BeEqualTo(Global.SingboxLocalDNSTag);
    }

    [Test]
    public async Task Ipv6UpstreamsAreAcceptedAndSerializedWithoutUriBrackets()
    {
        var context = Context();
        context.SimpleDnsItem.BootstrapDNS = "2001:4860:4860::8844";
        foreach (var address in new[] { "2001:4860:4860::8888", "tcp://[2001:4860:4860::8888]:5353", "https://[2001:4860:4860::8888]/dns-query" })
        {
            context.SimpleDnsItem.RemoteDNS = address;
            var config = Generate(context);
            var server = config.dns!.servers.Single(s => s.tag == config.dns.final);
            await server.server.Should().BeEqualTo("2001:4860:4860::8888");
            await config.dns.servers.Single(s => s.tag == Global.SingboxLocalDNSTag).server.Should().BeEqualTo("2001:4860:4860::8844");
            if (address.StartsWith("tcp:")) await server.server_port.Should().BeEqualTo(5353);
        }
    }

    [Test]
    public async Task NonHttpDnsCannotSilentlyDiscardPathsOrQueries()
    {
        foreach (var address in new[] { "tcp://1.1.1.1/ignored", "udp://1.1.1.1?ignored=true", "tls://dns.example/path" })
            await (DnsEgressPolicy.ValidateUpstreams(address, true) != null).Should().BeTrue();
        await (DnsEgressPolicy.ValidateUpstreams("https://dns.example/dns-query?key=value", true) == null).Should().BeTrue();
    }

    [Test]
    public async Task SubscriptionChainKeepsSelectedNodeOverrideAndDefaultEgress()
    {
        var context = Context();
        var original = context.Node;
        var chain = CoreConfigTestFactory.CreateProxyChainNode(ECoreType.sing_box, "inner-chain", "chain", ["b", original.IndexId]);
        context.AllProxiesMap[chain.IndexId] = chain;
        context = context with { Node = chain, SourceNodeId = original.IndexId };
        context.SimpleDnsItem.DefaultEgressNodeId = original.IndexId;
        context.SimpleDnsItem.NodeDnsOverrides[original.IndexId] = "tcp://9.9.9.9";
        var config = Generate(context);
        var server = config.dns!.servers.Single(s => s.tag == config.dns.final);
        await server.server.Should().BeEqualTo("9.9.9.9");
        await server.detour.Should().BeEqualTo(Global.ProxyTag);
        await config.outbounds.Single(o => o.tag == Global.ProxyTag).detour!.StartsWith("chain-proxy-").Should().BeTrue();
        await config.outbounds.Any(o => o.tag == "a-proxy-A").Should().BeFalse();
    }

    [Test]
    public async Task ReusingGeneratorRebuildsDnsServersAndChangedOverrides()
    {
        var context = Context();
        var generator = new CoreConfigSingboxService(context);
        var first = generator.GenerateClientConfigContent();
        var second = generator.GenerateClientConfigContent();
        await first.Success.Should().BeTrue();
        await second.Success.Should().BeTrue();
        await second.Data!.ToString().Should().BeEqualTo(first.Data!.ToString());
        context.SimpleDnsItem.NodeDnsOverrides[context.Node.IndexId] = "tcp://9.9.9.9";
        var changed = generator.GenerateClientConfigContent();
        await changed.Success.Should().BeTrue();
        var config = JsonUtils.Deserialize<SingboxConfig>(changed.Data!.ToString())!;
        await config.dns!.servers.Single(s => s.tag == config.dns.final).server.Should().BeEqualTo("9.9.9.9");
    }

    [Test]
    public async Task DomainRulesUseTheirOwnEgressAndCannotMatchPortsPrematurely()
    {
        var config = Generate(Context());
        string? Detour(string domain)
        {
            var rule = config.dns!.rules.First(r => r.domain?.Contains(domain) == true);
            return config.dns.servers.Single(s => s.tag == rule.server).detour;
        }
        await Detour("a.example").Should().BeEqualTo(Global.ProxyTag);
        await Detour("b.example").Should().BeEqualTo("b-proxy-B");
        await (Detour("direct.example") == null).Should().BeTrue();
        await Detour("port.example").Should().BeEqualTo(Global.ProxyTag);
        await config.dns.rules.Any(r => r.domain?.Contains("blocked.example") == true && r.rcode == "NXDOMAIN").Should().BeTrue();
        await config.dns.independent_cache.Should().BeEqualTo(true);
        await config.dns.rules[0].domain_regex![0].Should().BeEqualTo(@"(?i)\.local\.?$");
    }

    [Test]
    public async Task AmbiguousRuleUsesDefaultBeforeLaterBroadDomainRule()
    {
        var context = Context();
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Id = "port", Domain = ["full:shared.example"], Port = "443", OutboundTag = "B" },
            new() { Id = "broad", Domain = ["full:shared.example"], OutboundTag = Global.DirectTag },
        });
        var config = Generate(context);
        var rule = config.dns!.rules.First(r => r.domain?.Contains("shared.example") == true);
        await config.dns.servers.Single(s => s.tag == rule.server).detour.Should().BeEqualTo(Global.ProxyTag);
    }

    [Test]
    public async Task ManagedDirectDnsDoesNotUseExpectedIpProxyFallback()
    {
        var context = Context();
        context.SimpleDnsItem.DirectExpectedIPs = "geoip:cn";
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Id = "cn", Domain = ["geosite:cn"], OutboundTag = Global.DirectTag },
        });
        var config = Generate(context);
        var rules = config.dns!.rules.Where(r => r.rule_set?.Contains("geosite-cn") == true).ToList();
        await (rules.Count > 0).Should().BeTrue();
        await rules.All(r => r.server == Global.SingboxDirectDNSTag).Should().BeTrue();
    }

    [Test]
    public async Task OverridesAndDefaultUseStableNodeIdAndConfigurationIsDeterministic()
    {
        var context = Context();
        context.SimpleDnsItem.DefaultEgressNodeId = "b";
        context.SimpleDnsItem.NodeDnsOverrides["b"] = "tcp://9.9.9.9:53";
        var config = Generate(context);
        var final = config.dns!.servers.Single(s => s.tag == config.dns.final);
        await final.detour.Should().BeEqualTo("b-proxy-B");
        await final.server.Should().BeEqualTo("9.9.9.9");
        await final.type.Should().BeEqualTo("tcp");
        await JsonUtils.Serialize(Generate(context)).Should().BeEqualTo(JsonUtils.Serialize(config));
    }

    [Test]
    public async Task LegacyRemainsOptInAndCurrentProxyDoesNotCopyBusinessRoutes()
    {
        var context = Context();
        context.SimpleDnsItem.EgressMode = DnsEgressMode.Legacy;
        var legacy = Generate(context);
        await legacy.dns!.servers.Any(s => s.tag.StartsWith("egress-dns-")).Should().BeFalse();
        context.SimpleDnsItem.EgressMode = DnsEgressMode.CurrentProxy;
        var uniform = Generate(context);
        await uniform.dns!.rules.Any(r => r.domain?.Contains("b.example") == true).Should().BeFalse();
        await uniform.dns.servers.Single(s => s.tag == uniform.dns.final).detour.Should().BeEqualTo(Global.ProxyTag);
    }

    [Test]
    public async Task MissingNodeAndUnsafeUpstreamFailGeneration()
    {
        var context = Context();
        context.SimpleDnsItem.DefaultEgressNodeId = "missing";
        await new CoreConfigSingboxService(context).GenerateClientConfigContent().Success.Should().BeFalse();
        context.SimpleDnsItem.DefaultEgressNodeId = "";
        context.SimpleDnsItem.RemoteDNS = "local";
        await new CoreConfigSingboxService(context).GenerateClientConfigContent().Success.Should().BeFalse();
    }

    [Test]
    public async Task UpstreamExhaustionTerminatesInsideMatchedRule()
    {
        var context = Context();
        context.SimpleDnsItem.RemoteDNS = "tcp://1.1.1.1,tcp://9.9.9.9";
        var config = Generate(context);
        var stop = config.dns!.rules.FindIndex(r => r.action == "reject" && r.domain?.Contains("b.example") == true);
        await (stop > 0).Should().BeTrue();
        await config.dns.rules.Take(stop).Where(r => r.domain?.Contains("b.example") == true)
            .All(r => r.server == null || config.dns.servers.Single(s => s.tag == r.server).detour == "b-proxy-B").Should().BeTrue();
    }

    [Test]
    public async Task GlobalModeUsesAllUpstreamsAndTerminatesBeforeDirectMode()
    {
        var context = Context();
        context.SimpleDnsItem.RemoteDNS = "tcp://1.1.1.1,tcp://9.9.9.9";
        var config = Generate(context);
        var global = config.dns!.rules.Where(r => r.clash_mode == nameof(ERuleMode.Global)).ToList();
        await global.Count(r => r.action == "evaluate").Should().BeEqualTo(2);
        await global.Last().action.Should().BeEqualTo("reject");
        await global.Where(r => r.server != null).All(r =>
            config.dns.servers.Single(s => s.tag == r.server).detour == Global.ProxyTag).Should().BeTrue();
    }

    [Test]
    public async Task UdpGuardAppliesToEveryProxyRouteWithoutBlockingEarlierDirectRules()
    {
        var context = Context();
        context.SimpleDnsItem.BlockProxyUdp = true;
        var config = Generate(context);
        var index = config.route.rules.FindIndex(r => r.domain?.Contains("b.example") == true && r.outbound == "b-proxy-B");
        var guard = config.route.rules[index - 1];
        await guard.action.Should().BeEqualTo("reject");
        await guard.rules![1].network![0].Should().BeEqualTo("udp");
        await guard.rules[0].domain![0].Should().BeEqualTo("b.example");
        await config.route.rules.Any(r => r.outbound == Global.DirectTag && r.domain?.Contains("direct.example") == true).Should().BeTrue();
        await config.route.rules.Last().network![0].Should().BeEqualTo("udp");
    }

    [Test]
    public async Task CustomDnsKeepsItsOwnServersWhileUdpPolicyStillApplies()
    {
        var context = Context() with
        {
            RawDnsItem = new DNSItem { Enabled = true, NormalDNS = "{\"servers\":[{\"tag\":\"custom\",\"type\":\"tcp\",\"server\":\"9.9.9.9\",\"detour\":\"proxy\"}],\"final\":\"custom\",\"rules\":[]}" },
        };
        context.SimpleDnsItem.BlockProxyUdp = true;
        context.SimpleDnsItem.DefaultEgressNodeId = "missing-but-inactive";
        var config = Generate(context);
        await config.dns!.servers.Any(s => s.tag == "custom").Should().BeTrue();
        await config.dns.servers.Any(s => s.tag.StartsWith("egress-dns-")).Should().BeFalse();
        await config.route.rules.Last().network![0].Should().BeEqualTo("udp");
    }

    [Test]
    public async Task SshUdpDnsRequiresExplicitNodeCapabilityAndDoesNotSilentlyChangeProtocol()
    {
        var node = SshTests.Node();
        var context = Context() with { Node = node };
        context.AllProxiesMap[node.IndexId] = node;
        context.SimpleDnsItem.RemoteDNS = "udp://1.1.1.1";
        await new CoreConfigSingboxService(context).GenerateClientConfigContent().Success.Should().BeFalse();
        node.SetProtocolExtra(node.GetProtocolExtra() with { SshUdpForwarding = true });
        var config = Generate(context);
        await config.dns!.servers.Where(s => s.detour == Global.ProxyTag).All(s => s.type == "udp").Should().BeTrue();
    }

    [Test]
    public async Task FakeIpRetainsLocalPriorityAndPerEgressResolvers()
    {
        var context = Context();
        context.SimpleDnsItem.FakeIP = true;
        var config = Generate(context);
        await config.dns!.rules[0].domain_regex![0].Should().BeEqualTo(@"(?i)\.local\.?$");
        await config.dns.servers.Any(s => s.type == "fakeip").Should().BeTrue();
        await config.dns.servers.Any(s => s.detour == "b-proxy-B").Should().BeTrue();
    }

    [Test]
    public async Task CoreAcceptsManagedDnsAndUdpGuards()
    {
        var executable = Environment.GetEnvironmentVariable("DNS_EGRESS_TEST_CORE");
        if (string.IsNullOrEmpty(executable)) { Skip.Test("Set DNS_EGRESS_TEST_CORE for config validation."); return; }
        var context = Context();
        context.SimpleDnsItem.BlockProxyUdp = true;
        context.SimpleDnsItem.BlockQuic = true;
        context.SimpleDnsItem.RemoteDNS = "tcp://1.1.1.1,tcp://9.9.9.9";
        var path = Path.Combine(Path.GetTempPath(), "dns-egress-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, JsonUtils.Serialize(Generate(context)));
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                ArgumentList = { "check", "-c", path }, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
            })!;
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            if (process.ExitCode != 0) throw new Exception(await errors);
        }
        finally { File.Delete(path); }
    }
}
