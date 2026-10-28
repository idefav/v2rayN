using ServiceLib.Tests.CoreConfig;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class TunSettingsTests
{
    [Test]
    [Arguments("macos", "gvisor", false)]
    [Arguments("macos", "system", true)]
    [Arguments("macos", "mixed", true)]
    [Arguments("windows", "gvisor", true)]
    [Arguments("windows", "system", false)]
    [Arguments("windows", "mixed", true)]
    [Arguments("linux", "gvisor", false)]
    [Arguments("linux", "system", true)]
    [Arguments("linux", "mixed", true)]
    public void EmptyAdvancedOptionsPreserveTheExistingEffectiveConfig(string platform, string stack, bool ipv6)
    {
        var config = Configuration();
        config.TunModeItem.Stack = stack;
        config.TunModeItem.EnableIPv6Address = ipv6;
        var before = Generate(config, platform);
        var values = TunSettingsCodec.Read(config.TunModeItem);
        var errors = TunSettingsCodec.Parse(config.TunModeItem, values, Platform(platform), out var migrated);
        Check(errors.Count == 0, string.Join("; ", errors));
        config.TunModeItem = migrated;
        var after = Generate(config, platform);
        Check(EffectiveCoreConfig.Fingerprint(before, true) == EffectiveCoreConfig.Fingerprint(after, true), "migration changed the effective configuration");
    }

    [Test]
    public void LinuxMappingRetainsTypesAndLoadsTunOnlyRulesetReferences()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tun-rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"version":3,"rules":[{"ip_cidr":["203.0.113.0/24"]}]}""");
        try
        {
            var config = Configuration();
            config.TunModeItem.Singbox = new()
            {
                InterfaceName = "test_tun", AdditionalAddresses = ["10.77.0.1/30"], DnsMode = "native",
                DnsAddress = ["172.18.0.2"], UdpTimeout = "10m", UdpMapping = "address_dependent",
                UdpFiltering = "address_and_port_dependent", UdpNatMax = 8192,
                AutoRedirect = true, AutoRedirectInputMark = 0x2023, AutoRedirectOutputMark = 0x2024,
                AutoRedirectResetMark = 0x2025, AutoRedirectNfQueue = 101, IpRoute2TableIndex = 2023,
                IpRoute2RuleIndex = 9001, AutoRedirectFallbackRuleIndex = 32769,
                IncludeInterface = ["br-lan"], IncludeUid = [0, 1000], ExcludeUid = [1001],
                IncludeUidRange = ["2000:2999"], ExcludeUidRange = ["2100:2199"],
                IncludeMacAddress = ["00:11:22:33:44:55"], ExcludeMptcp = true, NetNs = "test-ns",
                LoopbackAddress = ["10.7.0.1"], RouteAddress = ["203.0.113.0/24"],
                RouteAddressSet = ["office-net"], RuleSets = [new() { Tag = "office-net", Type = "local", Format = "source", Path = path }]
            };
            var json = JsonNode.Parse(Generate(config, "linux"))!;
            var inbound = json["inbounds"]!.AsArray().Single(i => i!["type"]!.ToString() == "tun")!;
            Check(inbound["auto_redirect_input_mark"]!.ToString() == "0x2023", "firewall mark wire format");
            Check(inbound["udp_nat_max"]!.GetValue<uint>() == 8192, "UDP session count is numeric");
            Check(inbound["include_uid"]![0]!.GetValue<uint>() == 0, "UID zero must not be treated as automatic");
            Check(inbound["address"]!.AsArray().Count == 2, "additional IPv4 address was lost");
            Check(inbound["netns"]!.ToString() == "test-ns", "namespace mapping");
            Check(json["route"]!["rule_set"]!.AsArray().Any(r => r!["tag"]!.ToString() == "office-net" && r["path"]!.ToString() == path),
                "a rule set referenced only by TUN must be loaded");
            var read = TunSettingsCodec.Read(config.TunModeItem);
            var issues = TunSettingsCodec.Parse(config.TunModeItem, read, TunPlatform.Linux, out var parsed);
            Check(issues.Count == 0, "typed round trip failed");
            Check(JsonUtils.Serialize(parsed.Singbox) == JsonUtils.Serialize(config.TunModeItem.Singbox), "advanced fields were lost in the editor codec");
        }
        finally { File.Delete(path); }
    }

    [Test]
    [Arguments("macos")]
    [Arguments("windows")]
    public void LinuxValuesRemainStoredButAreNotEmittedElsewhere(string platform)
    {
        var config = Configuration();
        config.TunModeItem.Singbox = new()
        {
            AutoRedirect = true, NetNs = "other-platform", IncludeUid = [0], IncludeInterface = ["eth0"],
            ExcludeMptcp = true, IncludeMacAddress = ["00:11:22:33:44:55"], IpRoute2TableIndex = 42,
            AutoRedirectInputMark = 123, UdpTimeout = "1m"
        };
        var generated = JsonNode.Parse(Generate(config, platform))!;
        var tun = generated["inbounds"]!.AsArray().Single(i => i!["type"]!.ToString() == "tun")!;
        foreach (var key in new[] { "auto_redirect", "netns", "include_uid", "include_interface", "exclude_mptcp", "include_mac_address", "iproute2_table_index", "auto_redirect_input_mark" })
            Check(tun[key] == null, "Linux field emitted on " + platform + ": " + key);
        Check(tun["udp_timeout"]!.ToString() == "1m", "cross-platform field lost");
        Check(config.TunModeItem.Singbox.NetNs == "other-platform" && config.TunModeItem.Singbox.AutoRedirect == true, "stored values were modified");
    }

    [Test]
    [Arguments("native", false, true)]
    [Arguments("disabled", false, false)]
    [Arguments("hijack", true, true)]
    public void ExplicitDnsModesChangeTunWithoutChangingMixedPorts(string mode, bool publicDns, bool virtualDns)
    {
        var config = Configuration();
        config.TunModeItem.Singbox = new() { DnsMode = mode, DnsAddress = ["172.18.0.2"] };
        var parsed = JsonUtils.Deserialize<SingboxConfig>(Generate(config, "macos"))!;
        var rules = parsed.route.rules.Where(r => r.action == "hijack-dns").ToArray();
        Check(rules.Any(r => Matches(r, "socks", "8.8.8.8")) , "mixed port DNS changed");
        Check(rules.Any(r => Matches(r, "tun", "8.8.8.8")) == publicDns, "TUN public DNS handling disagrees with selected mode");
        Check(rules.Any(r => Matches(r, "tun", "172.18.0.2")) == virtualDns, "TUN virtual DNS handling disagrees with selected mode");
        if (mode == "disabled") Check(parsed.inbounds.Single(i => i.type == "tun").dns_address == null, "disabled DNS still configures addresses");
    }

    [Test]
    public void AdditionalAddressesHavePreciseSelfProtectionWithoutBlockingDnsPeers()
    {
        var config = Configuration();
        config.TunModeItem.EnableIPv6Address = true;
        config.TunModeItem.Singbox = new() { AdditionalAddresses = ["10.77.0.1/30", "fd00:77::1/126"] };
        var parsed = JsonUtils.Deserialize<SingboxConfig>(Generate(config, "macos"))!;
        var rule = parsed.route.rules.Single(r => r.action == "reject" && r.method == "drop" && r.ip_cidr?.Count > 0);
        Check(rule.ip_cidr!.Contains("10.77.0.1/32") && rule.ip_cidr.Contains("fd00:77::1/128"), "extra TUN address is not protected");
        Check(!rule.ip_cidr.Contains("172.18.0.2/32"), "DNS peer is blocked");
        config.TunModeItem.EnableIPv6Address = false;
        var ipv4 = JsonUtils.Deserialize<SingboxConfig>(Generate(config, "macos"))!.inbounds.Single(i => i.type == "tun");
        Check(ipv4.address!.All(a => !a.Contains(':')), "IPv6 addresses emitted while disabled");
        Check(config.TunModeItem.Singbox.AdditionalAddresses.Count == 2, "disabled IPv6 data lost");
    }

    [Test]
    public void DisablingIpv6RetainsButDoesNotAdvertiseCustomIpv6Dns()
    {
        var config = Configuration();
        config.TunModeItem.Singbox = new() { DnsMode = "native", DnsAddress = ["172.18.0.2", "fdfe:dcba:9876::2"] };
        var tun = JsonUtils.Deserialize<SingboxConfig>(Generate(config, "macos"))!.inbounds.Single(i => i.type == "tun");
        Check(tun.dns_address!.SequenceEqual(["172.18.0.2"]), "inactive IPv6 DNS was advertised");
        Check(config.TunModeItem.Singbox.DnsAddress.Count == 2, "inactive IPv6 DNS was lost");
    }

    [Test]
    public void DependencyAndFormatErrorsAreActionable()
    {
        var config = Configuration();
        config.TunModeItem.AutoRoute = false;
        config.TunModeItem.Singbox = new()
        {
            AutoRedirect = true, IncludeInterface = ["eth0"], ExcludeInterface = ["eth1"],
            IncludeMacAddress = ["bad-mac"], IncludeUidRange = ["2000:1000"], DnsMode = "native",
            DnsAddress = ["172.18.0.1"], UdpTimeout = "-5m", InterfaceName = "invalid-for-macos",
        };
        var linux = SingboxTunSettings.Validate(config.TunModeItem, TunPlatform.Linux);
        foreach (var key in new[] { "auto_redirect", "include_interface", "include_mac_address", "include_uid_range", "dns_address", "udp_timeout" })
            Check(linux.Any(e => e.Key == key), "missing validation for " + key);
        Check(SingboxTunSettings.Validate(config.TunModeItem, TunPlatform.MacOS).Any(e => e.Key == "interface_name"), "invalid utun name accepted");
        var context = Context(config, "linux");
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        Check(!result.Success && result.Msg.Contains("auto_redirect"), "invalid options were silently discarded");
    }

    [Test]
    public void NumericOverflowAndMalformedDurationsDoNotChangeTheOriginal()
    {
        var original = Configuration().TunModeItem;
        var values = TunSettingsCodec.Read(original);
        values["auto_redirect_nfqueue"] = "65536";
        values["udp_nat_max"] = "4294967296";
        values["include_uid"] = "-1";
        var issues = TunSettingsCodec.Parse(original, values, TunPlatform.Linux, out _);
        Check(issues.Count == 3 && original.Singbox == null, "parse errors mutated original settings");
        Check(SingboxTunSettings.ValidDuration("1h30m") && SingboxTunSettings.ValidDuration("0.5s"), "valid custom duration rejected");
        foreach (var text in new[] { "0s", "-5m", "5minutes", "1m garbage", "999999999999999999999h" })
            Check(!SingboxTunSettings.ValidDuration(text), "invalid duration accepted: " + text);
    }

    [Test]
    public void DraftListsAreTrackedAndInactiveGroupValuesCanBeReset()
    {
        var config = Configuration();
        config.TunModeItem.EnableTun = false;
        config.TunModeItem.Singbox = new() { DnsMode = "disabled", DnsAddress = ["172.18.0.2"] };
        var vm = new TunSettingsViewModel(config, TunPlatform.MacOS);
        Check(!vm.IsDirty && !vm["dns_address"].Enabled && !vm["auto_redirect"].Enabled, "initial editor availability");
        vm.Status = "Saved";
        vm["additional_addresses"].Value = "10.77.0.1/30";
        Check(vm.IsDirty && vm.CanSave, "list edit was not tracked");
        Check(vm.Status.Length == 0, "an old saved message concealed a new unsaved draft");
        Check(config.TunModeItem.Singbox.AdditionalAddresses == null, "draft edit changed live config");
        vm.ResetGroup("dns");
        Check(vm["dns_mode"].Value == "" && vm["dns_address"].Value == "", "group reset retained inactive custom values");
        vm.Discard();
        Check(!vm.IsDirty && vm["dns_mode"].Value == "disabled", "discard failed");
        var timeout = vm["udp_timeout"];
        timeout.SelectedChoice = timeout.Choices.Single(c => c.Value == "__custom__");
        timeout.Value = "45s";
        Check(timeout.IsCustom, "custom duration editor is unavailable");
        vm.ResetGroup("udp");
        Check(!timeout.IsCustom && timeout.SelectedChoice?.Value == "", "reset did not restore automatic editor mode");
    }

    [Test]
    public async Task FailedApplyRestoresPreviousSettingsAndReportsTheOutcome()
    {
        var config = Configuration();
        var previous = JsonUtils.Serialize(config.TunModeItem);
        var draft = JsonUtils.DeepCopy(config.TunModeItem)!;
        draft.Singbox = new() { UdpTimeout = "10m" };
        var saves = 0;
        var restarts = 0;
        var result = await TunSettingsTransaction.ApplyAsync(config, draft,
            () => Task.FromResult(++saves > 0), _ => Task.FromResult(++restarts > 1));
        Check(!result.Success && result.Restored && saves == 2 && restarts == 2, "failed apply was not rolled back");
        Check(JsonUtils.Serialize(config.TunModeItem) == previous, "previous TUN settings were not restored");
    }

    [Test]
    public async Task SavingWhileTunIsOffNeverStartsOrReloadsTheCore()
    {
        var config = Configuration();
        config.TunModeItem.EnableTun = false;
        var draft = JsonUtils.DeepCopy(config.TunModeItem)!;
        draft.Singbox = new() { UdpNatMax = 4096 };
        var reloads = 0;
        var result = await TunSettingsTransaction.ApplyAsync(config, draft, () => Task.FromResult(true),
            _ => { reloads++; return Task.FromResult(true); });
        Check(result.Success && reloads == 0 && !config.TunModeItem.EnableTun, "saving enabled or reloaded TUN");
    }

    [Test]
    [Arguments("hijack", "gvisor")]
    [Arguments("native", "system")]
    [Arguments("disabled", "mixed")]
    public async Task GeneratedConfigPassesTheInstalledCoreCheck(string mode, string stack)
    {
        var executable = Environment.GetEnvironmentVariable("TUN_TEST_SINGBOX");
        if (string.IsNullOrEmpty(executable)) { Skip.Test("Set TUN_TEST_SINGBOX to check generated configs with the actual core."); return; }
        var config = Configuration();
        config.TunModeItem.Stack = stack;
        config.TunModeItem.Singbox = new() { DnsMode = mode, DnsAddress = ["172.18.0.2"], UdpTimeout = "5m", UdpMapping = "endpoint_independent" };
        var directory = Path.Combine(Path.GetTempPath(), "tun-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, Generate(config, OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux"));
            var start = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = directory };
            foreach (var arg in new[] { "check", "-c", path, "--disable-color" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(true); }
            Check(process.ExitCode == 0, await stderr + await stdout);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool Matches(Rule4Sbox rule, string inbound, string destination)
    {
        if (rule.process_path?.Count > 0 || rule.process_name?.Count > 0) return false;
        if (rule.type == "logical")
            return rule.mode == "and" ? rule.rules!.All(r => Matches(r, inbound, destination)) : rule.rules!.Any(r => Matches(r, inbound, destination));
        return (rule.inbound == null || rule.inbound.Contains(inbound))
            && (rule.ip_cidr == null || rule.ip_cidr.Contains(destination + "/32"))
            && (rule.port == null || rule.port.Contains(53))
            && (rule.protocol == null || rule.protocol.Contains("dns"));
    }

    private static Config Configuration()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.TunModeItem = new() { EnableTun = true, AutoRoute = true, StrictRoute = true, Stack = "gvisor", Mtu = 9000, IcmpRouting = "default" };
        CoreConfigTestFactory.BindAppManagerConfig(config);
        return config;
    }

    private static CoreConfigContext Context(Config config, string platform) =>
        CoreConfigTestFactory.CreateContext(config, CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box), ECoreType.sing_box) with
        { IsTunEnabled = true, IsMacOS = platform == "macos", IsWindows = platform == "windows", IsLinux = platform == "linux" };

    private static string Generate(Config config, string platform)
    {
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var result = new CoreConfigSingboxService(Context(config, platform)).GenerateClientConfigContent();
        Check(result.Success, result.Msg);
        return result.Data!.ToString()!;
    }
    private static TunPlatform Platform(string platform) => platform == "macos" ? TunPlatform.MacOS : platform == "windows" ? TunPlatform.Windows : TunPlatform.Linux;
    private static void Check(bool success, string message) { if (!success) throw new Exception(message); }
}
