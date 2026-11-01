using ReactiveUI.Primitives;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.CoreConfig;

[NotInParallel]
public class RoutingProtocolTests
{
    [Test]
    [Arguments("dns")]
    [Arguments("stun")]
    [Arguments("dtls")]
    [Arguments("ssh")]
    [Arguments("rdp")]
    [Arguments("ntp")]
    public async Task SingboxEditorSavesAndGeneratesAdditionalProtocols(string protocol)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var original = new RulesItem { Remarks = "protocol-route" };
        var editor = new RoutingRuleDetailsViewModel(original, ECoreType.sing_box);
        await editor.AvailableProtocols.Should().Contain(protocol);
        editor.ProtocolItems = [protocol];
        var closed = false;
        editor.RequestClose += (_, _) => closed = true;
        await editor.SaveCmd.Execute().ToTask();
        await closed.Should().BeTrue();

        // Exercise persisted rule data, not only the list shown by the editor.
        var restored = JsonUtils.Deserialize<RulesItem>(JsonUtils.Serialize(original))!;
        var result = Generate(config, ECoreType.sing_box, restored);
        await result.Success.Should().BeTrue();
        var generated = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var route = generated.route.rules.Single(r => r.protocol?.SequenceEqual([protocol]) == true && r.outbound == Global.ProxyTag);
        await (route.domain == null && route.ip_cidr == null && route.port == null).Should().BeTrue();
        await (generated.route.rules.FindIndex(r => r.action == "sniff") < generated.route.rules.IndexOf(route)).Should().BeTrue();
        var reopened = new RoutingRuleDetailsViewModel(restored, ECoreType.sing_box);
        await reopened.ProtocolItems.Should().Contain(protocol);
    }

    [Test]
    [Arguments(ECoreType.Xray)]
    [Arguments(ECoreType.v2fly)]
    [Arguments(ECoreType.v2fly_v5)]
    public async Task OtherCoreCannotSilentlyDropStunOrBroadenMixedRule(ECoreType core)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var original = new RulesItem { Id = "existing", Remarks = "privacy-stun", Protocol = ["tls", "stun"], OutboundTag = Global.ProxyTag };
        var before = JsonUtils.Serialize(original);
        var editor = new RoutingRuleDetailsViewModel(original, core);
        await editor.AvailableProtocols.Should().Contain("stun");
        await editor.ProtocolItems.Should().Contain("stun");
        await editor.ProtocolSupportText.Should().Contain("stun");
        var closed = false;
        editor.RequestClose += (_, _) => closed = true;
        editor.Remarks = "draft-name";
        await editor.SaveCmd.Execute().ToTask();
        await closed.Should().BeFalse();
        await JsonUtils.Serialize(original).Should().BeEqualTo(before);

        var result = Generate(config, core, original);
        await result.Success.Should().BeFalse();
        await result.Data.Should().BeNull();
        await result.Msg.Should().Contain("stun");
        await result.Msg.Should().Contain("privacy-stun");

        editor.ProtocolItems = ["tls"];
        await editor.SaveCmd.Execute().ToTask();
        await closed.Should().BeTrue();
        await original.Protocol!.SequenceEqual(["tls"]).Should().BeTrue();
        var newEditor = new RoutingRuleDetailsViewModel(new RulesItem(), core);
        await newEditor.AvailableProtocols.Contains("stun").Should().BeFalse();
    }

    [Test]
    public async Task ExistingXrayProtocolsStillGenerateUnchanged()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var rule = new RulesItem { Protocol = ["http", "tls", "quic", "bittorrent"], OutboundTag = Global.ProxyTag };
        var result = Generate(config, ECoreType.Xray, rule);
        await result.Success.Should().BeTrue();
        var generated = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        await generated.routing.rules.Any(r => r.protocol?.SequenceEqual(rule.Protocol) == true && r.outboundTag == Global.ProxyTag).Should().BeTrue();
    }

    [Test]
    public async Task DisabledAndDnsOnlyRulesDoNotBlockAnotherCore()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var disabled = new RulesItem { Id = "disabled", Enabled = false, Protocol = ["stun"], OutboundTag = Global.ProxyTag };
        var dnsOnly = new RulesItem { RuleType = ERuleType.DNS, Protocol = ["stun"], Domain = ["full:example.org"], OutboundTag = Global.ProxyTag };
        var result = Generate(config, ECoreType.Xray, disabled, dnsOnly);
        await result.Success.Should().BeTrue();
        var generated = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        await generated.routing.rules.Any(r => r.protocol?.Contains("stun") == true).Should().BeFalse();
        var editor = new RoutingRuleDetailsViewModel(disabled, ECoreType.Xray);
        var closed = false;
        editor.RequestClose += (_, _) => closed = true;
        await editor.SaveCmd.Execute().ToTask();
        await closed.Should().BeTrue();
        await disabled.Protocol!.Contains("stun").Should().BeTrue();
    }

    [Test]
    public async Task UnknownImportedProtocolsRemainVisibleAndFailValidation()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.Inbound[0].SniffingEnabled = false;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var rule = new RulesItem { Id = "imported", Protocol = ["future-protocol"], OutboundTag = Global.ProxyTag };
        var editor = new RoutingRuleDetailsViewModel(rule, ECoreType.sing_box);
        await editor.AvailableProtocols.Should().Contain("future-protocol");
        await editor.ProtocolItems.Should().Contain("future-protocol");
        await editor.ProtocolSupportText.Should().BeEqualTo(RoutingProtocolPolicy.Describe(ECoreType.sing_box, rule.Protocol, false));
        var result = Generate(config, ECoreType.sing_box, rule);
        await result.Success.Should().BeFalse();
        await result.Msg.Should().Contain("future-protocol");
    }

    [Test]
    public async Task ClearingTheOnlyConditionDoesNotMutateTheRuleWhenSaveIsRejected()
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig(ECoreType.sing_box));
        var rule = new RulesItem { Id = "saved", Remarks = "STUN proxy", Protocol = ["stun"], OutboundTag = Global.ProxyTag };
        var before = JsonUtils.Serialize(rule);
        var editor = new RoutingRuleDetailsViewModel(rule, ECoreType.sing_box) { ProtocolItems = [], Remarks = "unsaved" };
        var closed = false;
        editor.RequestClose += (_, _) => closed = true;
        await editor.SaveCmd.Execute().ToTask();
        await closed.Should().BeFalse();
        await JsonUtils.Serialize(rule).Should().BeEqualTo(before);
    }

    [Test]
    public async Task BundledCoreAcceptsAllExposedSingboxProtocols()
    {
        var executable = Environment.GetEnvironmentVariable("TUN_TEST_SINGBOX");
        if (string.IsNullOrEmpty(executable)) { Skip.Test("Set TUN_TEST_SINGBOX to validate routing protocols with the bundled core."); return; }
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var result = Generate(config, ECoreType.sing_box,
            new RulesItem { Protocol = RoutingProtocolPolicy.Supported(ECoreType.sing_box).ToList(), OutboundTag = Global.ProxyTag });
        await result.Success.Should().BeTrue();
        var directory = Path.Combine(Path.GetTempPath(), "routing-protocol-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, result.Data!.ToString());
            var start = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = directory };
            foreach (var arg in new[] { "check", "-c", path, "--disable-color" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(true); }
            if (process.ExitCode != 0) throw new Exception(await stderr + await stdout);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static RetResult Generate(Config config, ECoreType core, params RulesItem[] rules)
    {
        var node = CoreConfigTestFactory.CreateVmessNode(core);
        var context = CoreConfigTestFactory.CreateContext(config, node, core) with
        {
            RoutingItem = new RoutingItem { RuleSet = JsonUtils.Serialize(rules) }
        };
        return core == ECoreType.sing_box
            ? new CoreConfigSingboxService(context).GenerateClientConfigContent()
            : new CoreConfigV2rayService(context).GenerateClientConfigContent();
    }
}
