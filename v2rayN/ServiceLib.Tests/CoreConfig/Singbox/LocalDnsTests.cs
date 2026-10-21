namespace ServiceLib.Tests.CoreConfig.Singbox;

[NotInParallel]
public class LocalDnsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LocalNamesUseMdnsBeforeModesFakeIpAndPublicResolvers(bool customDns)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.SimpleDNSItem.FakeIP = true;
        config.SimpleDNSItem.GlobalFakeIp = true;
        config.SimpleDNSItem.DirectDNS = "https://dns.alidns.com/dns-query";
        config.SimpleDNSItem.Strategy4Freedom = "UseIPv4";
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = SshTests.Node();
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);
        if (customDns)
        {
            context = context with
            {
                RawDnsItem = new DNSItem
                {
                    Enabled = true,
                    DomainDNSAddress = "223.5.5.5",
                    NormalDNS = """
                        {"servers":[{"type":"https","tag":"mdns-local","server":"dns.alidns.com"}],
                         "rules":[],"final":"mdns-local"}
                        """,
                },
            };
        }

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        await result.Success.Should().BeTrue().Because(result.Msg);
        var generated = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var mdns = generated.dns.servers.Single(server =>
            server.type == (OperatingSystem.IsMacOS() ? "local" : "mdns"));
        await string.IsNullOrEmpty(mdns.detour).Should().BeTrue();
        await generated.dns.servers.Count(server => server.tag == mdns.tag).Should().BeEqualTo(1);
        await generated.dns.rules[0].server.Should().BeEqualTo(mdns.tag);
        if (customDns)
        {
            await generated.dns.servers.Should().Contain(server =>
                server.tag == "mdns-local" && server.type == "https");
        }

        var resolveIndex = generated.route.rules.FindIndex(rule => rule.server == mdns.tag);
        var resolve = generated.route.rules[resolveIndex];
        await resolve.action.Should().BeEqualTo("resolve");
        await generated.route.rules[resolveIndex + 1].outbound.Should().BeEqualTo(Global.DirectTag);
        await (resolveIndex < generated.route.rules.FindIndex(rule => rule.clash_mode != null)).Should().BeTrue();
        await (resolveIndex > generated.route.rules.FindIndex(rule => rule.action == "hijack-dns")).Should().BeTrue();

        foreach (var name in new[] { "printer.local", "printer.local.", "IDEFAV.LOCAL.", "room.printer.local" })
        {
            await Regex.IsMatch(name, resolve.domain_regex!.Single()).Should().BeTrue();
            await Regex.IsMatch(name, generated.dns.rules[0].domain_regex!.Single()).Should().BeTrue();
        }
        foreach (var name in new[] { "local", "localhost", "notlocal", "printer.local.example.com", "google.com" })
        {
            await Regex.IsMatch(name, resolve.domain_regex!.Single()).Should().BeFalse();
        }
    }
}
