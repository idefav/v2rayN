namespace ServiceLib.Tests.CoreConfig.Singbox;

[NotInParallel]
public class SshUdpTests
{
    [Test]
    public async Task OptInPreservesDefaultAndMapsToCore()
    {
        var node = SshTests.Node();
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        string Generate() => new CoreConfigSingboxService(CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box)).GenerateClientConfigContent().Data!.ToString()!;
        var original = JsonNode.Parse(Generate())!["outbounds"]!.AsArray().Single(x => x?["type"]?.ToString() == "ssh")!;
        await (original["udp_forwarding"] == null).Should().BeTrue();
        node.SetProtocolExtra(node.GetProtocolExtra() with { SshUdpForwarding = true, SshAllowHttp3 = true });
        var enabled = JsonNode.Parse(Generate())!["outbounds"]!.AsArray().Single(x => x?["type"]?.ToString() == "ssh")!;
        await enabled["udp_forwarding"]!.GetValue<bool>().Should().BeTrue();
        await (enabled["host_key"] != null).Should().BeTrue();
    }

    [Test]
    public async Task Http3SkipsOnlyExplicitlyOptedInUnqualifiedDefaultBlock()
    {
        var node = SshTests.Node();
        var rule = new RulesItem { Remarks = "阻断udp443", OutboundTag = Global.BlockTag, Network = "udp", Port = "443" };
        await SshUdpPolicy.SkipDefaultUdp443Block(node, rule).Should().BeFalse();
        node.SetProtocolExtra(node.GetProtocolExtra() with { SshUdpForwarding = true });
        await SshUdpPolicy.SkipDefaultUdp443Block(node, rule).Should().BeFalse();
        node.SetProtocolExtra(node.GetProtocolExtra() with { SshUdpForwarding = true, SshAllowHttp3 = true });
        await SshUdpPolicy.SkipDefaultUdp443Block(node, rule).Should().BeTrue();
        rule.Domain = ["example.com"];
        await SshUdpPolicy.SkipDefaultUdp443Block(node, rule).Should().BeFalse();
        rule.Domain = null; rule.Remarks = "custom block";
        await SshUdpPolicy.SkipDefaultUdp443Block(node, rule).Should().BeFalse();
    }

    [Test]
    public async Task GeneratedRoutingKeepsCustomBlocksAndDnsPolicy()
    {
        var node = SshTests.Node();
        node.SetProtocolExtra(node.GetProtocolExtra() with { SshUdpForwarding = true, SshAllowHttp3 = true });
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);
        context.RoutingItem.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Remarks = "阻断udp443", Network = "udp", Port = "443", OutboundTag = Global.BlockTag },
            new() { Remarks = "custom", Network = "udp", Port = "443", OutboundTag = Global.BlockTag, Domain = ["full:blocked.example"] }
        });
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        await result.Success.Should().BeTrue();
        var json = JsonNode.Parse(result.Data!.ToString()!)!;
        var rejected = json["route"]!["rules"]!.AsArray().Where(r => r?["action"]?.ToString() == "reject").ToArray();
        await rejected.Length.Should().BeEqualTo(1);
        await rejected[0]!["domain"]![0]!.ToString().Should().BeEqualTo("blocked.example");
        await json["route"]!["rules"]!.AsArray().Any(r => r?["action"]?.ToString() == "hijack-dns").Should().BeTrue();
    }
}
