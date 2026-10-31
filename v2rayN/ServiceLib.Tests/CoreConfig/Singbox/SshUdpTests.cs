namespace ServiceLib.Tests.CoreConfig.Singbox;

[NotInParallel]
public class SshUdpTests
{
    [Test]
    [Arguments("custom")]
    [Arguments("tun2proxy")]
    public async Task BothModesPassBundledCoreCheck(string mode)
    {
        var executable = Environment.GetEnvironmentVariable("TUN_TEST_SINGBOX")
            ?? Environment.GetEnvironmentVariable("SSH_TEST_SINGBOX");
        if (string.IsNullOrEmpty(executable)) { Skip.Test("Set SSH_TEST_SINGBOX for dual-mode config validation."); return; }
        var node = SshTests.Node();
        node.SetProtocolExtra(node.GetProtocolExtra() with { SshUdpForwarding = true, SshUdpMode = mode, SshUdpGateway = "127.0.0.1:7300", SshAllowHttp3 = true });
        var config = CoreConfigTestFactory.CreateConfig();
        config.SimpleDNSItem.RemoteDNS = "1.1.1.1";
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var result = new CoreConfigSingboxService(CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box)).GenerateClientConfigContent();
        await result.Success.Should().BeTrue();
        var directory = Path.Combine(Path.GetTempPath(), "ssh-modes-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, result.Data!.ToString());
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                ArgumentList = { "check", "-c", path }, WorkingDirectory = directory,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(true); }
            if (process.ExitCode != 0) throw new Exception(await stderr + await stdout);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task Tun2ProxyPersistsAndGeneratesOnlyItsGatewayFields()
    {
        var node = SshTests.Node();
        node.SetProtocolExtra(node.GetProtocolExtra() with { SshUdpForwarding = true, SshUdpMode = SshUdpPolicy.Tun2Proxy, SshUdpGateway = "[::1]:7300", SshAllowHttp3 = true });
        var restored = JsonUtils.Deserialize<ProfileItem>(JsonUtils.Serialize(node))!;
        await SshUdpPolicy.Mode(restored.GetProtocolExtra()).Should().BeEqualTo(SshUdpPolicy.Tun2Proxy);
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var vm = new ServiceLib.ViewModels.AddSshServerViewModel(restored);
        await vm.UdpModeIndex.Should().BeEqualTo(1);
        await vm.IsUdpGateway.Should().BeTrue();
        await vm.CreateDraftSnapshot().GetProtocolExtra().SshUdpGateway.Should().BeEqualTo("[::1]:7300");
        vm.CancelOperation();
        string Generate() => new CoreConfigSingboxService(CoreConfigTestFactory.CreateContext(config, restored, ECoreType.sing_box)).GenerateClientConfigContent().Data!.ToString()!;
        var outbound = JsonNode.Parse(Generate())!["outbounds"]!.AsArray().Single(o => o?["type"]?.ToString() == "ssh")!;
        await outbound["udp_mode"]!.GetValue<string>().Should().BeEqualTo("tun2proxy");
        await outbound["udp_gateway"]!.GetValue<string>().Should().BeEqualTo("[::1]:7300");
        restored.SetProtocolExtra(restored.GetProtocolExtra() with { SshUdpMode = SshUdpPolicy.Custom });
        outbound = JsonNode.Parse(Generate())!["outbounds"]!.AsArray().Single(o => o?["type"]?.ToString() == "ssh")!;
        await outbound["udp_mode"]!.GetValue<string>().Should().BeEqualTo("custom");
        await (outbound["udp_gateway"] == null).Should().BeTrue();
        restored.SetProtocolExtra(restored.GetProtocolExtra() with { SshUdpForwarding = false });
        outbound = JsonNode.Parse(Generate())!["outbounds"]!.AsArray().Single(o => o?["type"]?.ToString() == "ssh")!;
        await (outbound["udp_mode"] == null && outbound["udp_gateway"] == null && outbound["udp_forwarding"] == null).Should().BeTrue();
    }

    [Test]
    public async Task ModesValidateWithoutSilentlyFallingBack()
    {
        var extra = new ProtocolExtraItem { SshUdpForwarding = true };
        await SshUdpPolicy.Mode(extra).Should().BeEqualTo(SshUdpPolicy.Custom);
        await (SshUdpPolicy.Validate(extra with { SshUdpMode = "unknown" }) != null).Should().BeTrue();
        foreach (var address in new[] { "127.0.0.1:7300", "[::1]:7300", "gateway.example:7300" })
            await (SshUdpPolicy.Validate(extra with { SshUdpMode = "tun2proxy", SshUdpGateway = address }) == null).Should().BeTrue();
        foreach (var address in new[] { "http://127.0.0.1:7300", "host:0", "host:65536", "::1:7300", "user@host:7300", "host:7300/path" })
            await (SshUdpPolicy.Validate(extra with { SshUdpMode = "tun2proxy", SshUdpGateway = address }) != null).Should().BeTrue();
        await SshUdpPolicy.CoreUdpEnabled(JsonNode.Parse("{\"udp_mode\":\"tun2proxy\"}")!).Should().BeTrue();
        await SshUdpPolicy.CoreUdpEnabled(JsonNode.Parse("{\"udp_mode\":\"disabled\",\"udp_forwarding\":true}")!).Should().BeFalse();
    }

    [Test]
    public async Task GatewaySupportsUdpDnsAndHttp3Policy()
    {
        var node = SshTests.Node();
        node.SetProtocolExtra(node.GetProtocolExtra() with { SshUdpForwarding = true, SshUdpMode = "tun2proxy", SshAllowHttp3 = true });
        await SshUdpPolicy.SkipDefaultUdp443Block(node, new RulesItem { Remarks = "阻断udp443", OutboundTag = Global.BlockTag, Network = "udp", Port = "443" }).Should().BeTrue();
        const string json = "{\"outbounds\":[{\"type\":\"ssh\",\"tag\":\"proxy\",\"udp_mode\":\"tun2proxy\"}],\"dns\":{\"servers\":[{\"type\":\"udp\",\"server\":\"1.1.1.1\",\"detour\":\"proxy\"}]}}";
        var result = JsonNode.Parse(SshDnsPolicy.Apply(json, true, true))!;
        await result["dns"]!["servers"]![0]!["type"]!.GetValue<string>().Should().BeEqualTo("udp");
    }

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
