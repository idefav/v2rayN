namespace ServiceLib.Tests.Services;

public class EffectiveCoreConfigTests
{
    [Test]
    public async Task IdenticalEffectiveConfig_ShouldPreserveConnectionDespiteJsonFormatting()
    {
        var running = Snapshot("""{"outbounds":[{"server":"example.com","password":"secret"}],"route":{"final":"proxy"}}""");
        var updated = Snapshot("""{ "route": { "final": "proxy" }, "outbounds": [{ "password": "secret", "server": "example.com" }] }""");
        await updated.ReloadReason(running, true, false).Should().BeNull();
    }

    [Test]
    [Arguments("server", "other.example.com")]
    [Arguments("password", "new-secret")]
    [Arguments("server_port", "443")]
    public async Task ActiveConnectionChange_ShouldRequireReload(string field, string value)
    {
        var running = Snapshot("""{"outbounds":[{"server":"example.com","password":"secret","server_port":"22"}]}""");
        var json = JsonNode.Parse("""{"outbounds":[{"server":"example.com","password":"secret","server_port":"22"}]}""")!;
        json["outbounds"]![0]![field] = value;
        await Snapshot(json.ToJsonString()).ReloadReason(running, true, false).Should().BeEqualTo("主内核有效配置发生变化");
    }

    [Test]
    public async Task ReferencedRouteOrChainChange_ShouldRequireReload()
    {
        var running = Snapshot("""{"route":{"final":"proxy"},"outbounds":[{"tag":"proxy","detour":"hop-a"}]}""");
        var updated = Snapshot("""{"route":{"final":"proxy"},"outbounds":[{"tag":"proxy","detour":"hop-b"}]}""");
        await updated.ReloadReason(running, true, false).Should().BeEqualTo("主内核有效配置发生变化");
        await Snapshot("""{"rules":[1,2]}""").Main.Should().NotBeEqualTo(Snapshot("""{"rules":[2,1]}""").Main);
    }

    [Test]
    public async Task PreCoreChangesAndFailure_ShouldNotBeSkipped()
    {
        var running = Snapshot("{}", """{"server":"hop-a"}""");
        await running.ReloadReason(running, true, true).Should().BeNull();
        await Snapshot("{}", """{"server":"hop-b"}""").ReloadReason(running, true, true).Should().BeEqualTo("前置内核有效配置发生变化");
        await running.ReloadReason(running, true, false).Should().BeEqualTo("前置内核未运行");
        await Snapshot("{}").ReloadReason(running, true, true).Should().BeEqualTo("内核类型或前置内核发生变化");
    }

    [Test]
    public async Task MissingSnapshotOrDeadCore_ShouldNotBeSkipped()
    {
        var config = Snapshot("{}");
        await config.ReloadReason(null, true, false).Should().BeEqualTo("缺少运行配置快照");
        await config.ReloadReason(config, false, false).Should().BeEqualTo("主内核未运行");
        await (config with { MainType = ECoreType.Xray }).ReloadReason(config, true, false).Should().BeEqualTo("内核类型或前置内核发生变化");
    }

    [Test]
    public async Task GeneratedLogDateAndTunName_ShouldNotInterruptConnections()
    {
        const string before = """{"log":{"output":"/logs/sbox_2026-10-24.txt"},"inbounds":[{"protocol":"tun","settings":{"name":"utun12"}}]}""";
        const string after = """{"log":{"output":"/logs/sbox_2026-10-25.txt"},"inbounds":[{"protocol":"tun","settings":{"name":"utun34"}}]}""";
        await EffectiveCoreConfig.Fingerprint(before, true).Should().BeEqualTo(EffectiveCoreConfig.Fingerprint(after, true));
        // User-authored names are intentional and must still be compared exactly.
        await EffectiveCoreConfig.Fingerprint(before).Should().NotBeEqualTo(EffectiveCoreConfig.Fingerprint(after));
    }

    [Test]
    public async Task CustomYamlAndJsonNull_ShouldBeSupported()
    {
        await EffectiveCoreConfig.Fingerprint("null").Should().BeEqualTo(EffectiveCoreConfig.Fingerprint(" null "));
        await EffectiveCoreConfig.Fingerprint("server: host-a").Should().NotBeEqualTo(EffectiveCoreConfig.Fingerprint("server: host-b"));
    }

    private static EffectiveCoreConfig Snapshot(string main, string? pre = null) => new(
        EffectiveCoreConfig.Fingerprint(main), ECoreType.sing_box,
        pre == null ? null : EffectiveCoreConfig.Fingerprint(pre), pre == null ? null : ECoreType.sing_box);
}
