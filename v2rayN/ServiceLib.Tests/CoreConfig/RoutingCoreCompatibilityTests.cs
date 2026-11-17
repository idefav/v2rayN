namespace ServiceLib.Tests.CoreConfig;

[NotInParallel]
public class RoutingCoreCompatibilityTests
{
    [Test]
    public async Task ConflictsKeepRuleIdentityAndAllUnsupportedValuesWithoutMutation()
    {
        var routing = new RoutingItem { RuleSet = JsonUtils.Serialize(new[]
        {
            new RulesItem { Id = "disabled", Enabled = false, Protocol = ["stun"] },
            new RulesItem { Id = "dns", RuleType = ERuleType.DNS, Protocol = ["stun"] },
            new RulesItem { Id = "mixed", Remarks = "STUN PROXY", Protocol = ["tls", "stun", "future-protocol"] }
        }) };
        var before = routing.RuleSet;
        var conflicts = RoutingProtocolPolicy.Conflicts(routing, ECoreType.Xray);
        await conflicts.Should().HaveCount(1);
        await conflicts[0].RuleIndex.Should().BeEqualTo(2);
        await conflicts[0].RuleId.Should().BeEqualTo("mixed");
        await conflicts[0].Protocols.SequenceEqual(["stun", "future-protocol"]).Should().BeTrue();
        await routing.RuleSet.Should().BeEqualTo(before);
        await RoutingProtocolPolicy.Conflicts(routing, ECoreType.sing_box)[0].Protocols.SequenceEqual(["future-protocol"]).Should().BeTrue();
    }

    [Test]
    [Arguments("xhttp")]
    [Arguments("raw")]
    [Arguments("unknown-protocol")]
    [Arguments("core-rejects")]
    public async Task AlternativeChecksCopiesAndNeverPersistsChanges(string scenario)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<SubItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        await SQLiteHelper.Instance.DeleteAllAsync<RoutingItem>();
        var routing = new RoutingItem { Id = "compatibility-routing", IsActive = true, Remarks = "fixture",
            RuleSet = JsonUtils.Serialize(new[] { new RulesItem { Id = "stun", Remarks = "STUN PROXY",
                Protocol = [scenario == "unknown-protocol" ? "future-protocol" : "stun"], OutboundTag = Global.ProxyTag } }) };
        await SQLiteHelper.Instance.ReplaceAsync(routing);
        var profile = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        profile.Network = scenario == "xhttp" ? "xhttp" : "raw";
        await SQLiteHelper.Instance.ReplaceAsync(profile);
        var before = JsonUtils.Serialize(profile);
        var configBefore = JsonUtils.Serialize(config);
        string? candidatePath = null;
        try
        {
            var original = await CoreConfigContextBuilder.BuildAll(config, profile);
            await original.Success.Should().BeFalse();
            await original.CombinedValidatorResult.Errors[0].Should().Contain("STUN PROXY");
            var result = await RoutingCoreCompatibility.CheckAlternativeAsync(config, profile, CancellationToken.None, async (context, path, token) =>
            {
                candidatePath = path;
                await context.RunCoreType.Should().BeEqualTo(ECoreType.sing_box);
                var generated = await File.ReadAllTextAsync(path, token);
                await generated.Should().Contain("stun");
                return scenario == "core-rejects" ? "fixture core rejection" : null;
            });
            await (result == null).Should().BeEqualTo(scenario == "raw");
            await (candidatePath != null).Should().BeEqualTo(scenario is "raw" or "core-rejects");
            if (candidatePath != null) await File.Exists(candidatePath).Should().BeFalse();
            await JsonUtils.Serialize(profile).Should().BeEqualTo(before);
            await JsonUtils.Serialize(await AppManager.Instance.GetProfileItem(profile.IndexId)).Should().BeEqualTo(before);
            await JsonUtils.Serialize(config).Should().BeEqualTo(configBefore);
            await (await ConfigHandler.GetDefaultRouting(config)).RuleSet.Should().BeEqualTo(routing.RuleSet);
        }
        finally
        {
            await SQLiteHelper.Instance.DeleteAsync(routing);
            await SQLiteHelper.Instance.DeleteAsync(profile);
        }
    }

    [Test]
    public async Task CertificatePinsAndVlessEncryptionCannotDisappearDuringConversion()
    {
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        node.CertSha = "fixture-pin";
        await RoutingCoreCompatibility.ConversionError(node)!.Should().Contain("CertSha");
        node.CertSha = "";
        node.SetProtocolExtra(new() { VlessEncryption = "unsupported-encryption" });
        await RoutingCoreCompatibility.ConversionError(node)!.Should().Contain("VlessEncryption");
    }

    [Test]
    public async Task IncompatibleRouteDoesNotStopTheExistingProcessOrLaunchAnother()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix owned-process fixture."); return; }
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        using var process = new ProcessService("/bin/sleep", "30", Path.GetTempPath(), false, false, null, null);
        await process.StartAsync();
        process.IsReady = true;
        var manager = new CoreManager(_ => throw new Exception("An incompatible route must not launch a core"));
        typeof(CoreManager).GetField("_processService", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, process);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem { RuleSet = JsonUtils.Serialize(new[] { new RulesItem { Remarks = "STUN PROXY", Protocol = ["stun"] } }) }
        };
        try
        {
            await (await manager.LoadCore(context, null)).Should().BeEqualTo(CoreLoadResult.Failed);
            await manager.LastError!.Should().Contain("STUN PROXY");
            await (await manager.IsCoreRunningAsync()).Should().BeTrue();
            await (await process.HasExitedAsync()).Should().BeFalse();
        }
        finally { await process.StopAndWaitAsync(); }
    }

    [Test]
    public async Task CancelledCheckDoesNotStartAnExecutable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await RoutingCoreCompatibility.CheckExecutableAsync("does-not-exist", ECoreType.sing_box, "unused",
                new Dictionary<string, string?>(), cancellation.Token);
            throw new Exception("Cancelled check returned instead of throwing cancellation");
        }
        catch (OperationCanceledException) { }
    }

    [Test]
    public async Task BundledCoreCheckAcceptsValidConfigAndRejectsInvalidConfig()
    {
        var executable = Environment.GetEnvironmentVariable("TUN_TEST_SINGBOX");
        if (string.IsNullOrEmpty(executable)) { Skip.Test("Set TUN_TEST_SINGBOX for real core checks."); return; }
        var path = Path.Combine(Path.GetTempPath(), $"compatibility-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\"}]}");
            await (await RoutingCoreCompatibility.CheckExecutableAsync(executable, ECoreType.sing_box, path,
                new Dictionary<string, string?>(), CancellationToken.None)).Should().BeNull();
            await File.WriteAllTextAsync(path, "{\"outbounds\":[{\"type\":\"invalid-type\"}]}");
            await (await RoutingCoreCompatibility.CheckExecutableAsync(executable, ECoreType.sing_box, path,
                new Dictionary<string, string?>(), CancellationToken.None)).Should().NotBeNull();
        }
        finally { File.Delete(path); }
    }
}
