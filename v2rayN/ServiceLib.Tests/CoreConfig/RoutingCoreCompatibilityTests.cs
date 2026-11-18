namespace ServiceLib.Tests.CoreConfig;

[NotInParallel]
public class RoutingCoreCompatibilityTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SwitchingCoresReusesStoredRulesInOrderAndSkipsUnsupportedOutbounds(bool frontTun)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.TunModeItem.EnableTun = frontTun;
        config.TunModeItem.EnableLegacyProtect = frontTun;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var db = SQLiteHelper.Instance;
        db.CreateTable<RoutingItem>();
        db.CreateTable<ProfileItem>();
        db.CreateTable<SubItem>();
        db.CreateTable<DNSItem>();
        db.CreateTable<FullConfigTemplateItem>();
        var previous = await db.TableAsync<RoutingItem>().FirstOrDefaultAsync(item => item.IsActive);
        var originalTemplate = await AppManager.Instance.GetFullConfigTemplateItem(ECoreType.Xray);
        var template = new FullConfigTemplateItem { Id = originalTemplate?.Id ?? Guid.NewGuid().ToString(),
            CoreType = ECoreType.Xray, Enabled = true, TunConfig = "{}" };
        var linked = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box, Guid.NewGuid().ToString(), "STUN test outbound");
        linked.Address = "stun-outbound.example";
        var json = JsonUtils.Serialize(new RulesItem[]
        {
            new() { Protocol = ["tls"], OutboundTag = Global.DirectTag },
            new() { Remarks = "STUN PROXY", Protocol = ["stun"], OutboundTag = Global.ProxyTag },
            new() { Domain = ["full:skipped.example"], Protocol = ["tls", "stun"], OutboundTag = linked.Remarks },
            new() { Domain = ["full:kept.example"], OutboundTag = Global.DirectTag },
            new() { Port = "12345", OutboundTag = Global.ProxyTag },
            new() { Protocol = ["future-protocol"], OutboundTag = "missing outbound for skipped rule" }
        });
        var routing = new RoutingItem { Id = previous?.Id ?? Guid.NewGuid().ToString(), IsActive = true, RuleSet = json };
        try
        {
            await db.ReplaceAsync(routing);
            await db.InsertAsync(linked);
            if (frontTun) await db.ReplaceAsync(template);
            foreach (var core in new[] { ECoreType.sing_box, ECoreType.Xray, ECoreType.sing_box })
            {
                var node = CoreConfigTestFactory.CreateVmessNode(core, indexId: "");
                var built = await CoreConfigContextBuilder.BuildAll(config, node);
                await built.Success.Should().BeTrue();
                await built.CombinedValidatorResult.Warnings.Should().BeEmpty();
                await built.MainResult.Context.RunCoreType.Should().BeEqualTo(core);
                await built.MainResult.Context.AllProxiesMap.ContainsKey($"remark:{linked.Remarks}").Should().BeEqualTo(core == ECoreType.sing_box);
                await (built.PreSocksResult != null).Should().BeEqualTo(frontTun && core == ECoreType.Xray);
                if (built.PreSocksResult is { } front)
                {
                    await front.Context.AllProxiesMap.ContainsKey($"remark:{linked.Remarks}").Should().BeFalse();
                    var preConfig = await CoreConfigHandler.GenerateClientConfig(front.Context, null);
                    await preConfig.Success.Should().BeTrue();
                    await preConfig.Data!.ToString()!.Should().NotContain("skipped.example");
                    await preConfig.Data!.ToString()!.Should().NotContain("\"stun\"");
                }
                var result = await CoreConfigHandler.GenerateClientConfig(built.MainResult.Context, null);
                await result.Success.Should().BeTrue();
                var document = JsonNode.Parse(result.Data!.ToString()!)!;
                var ruleArray = (document["route"] ?? document["routing"])!["rules"]!.AsArray();
                var rules = ruleArray.Select(rule => rule!.ToJsonString()).ToList();
                var tls = rules.FindIndex(rule => rule.Contains("\"tls\""));
                var stun = rules.FindIndex(rule => rule.Contains("\"stun\""));
                var domain = rules.FindIndex(rule => rule.Contains("kept.example"));
                var port = rules.FindIndex(rule => rule.Contains("12345"));
                await (tls >= 0 && tls < domain && domain < port).Should().BeTrue();
                if (core == ECoreType.sing_box)
                {
                    var sniff = rules.FindIndex(rule => rule.Contains("\"sniff\""));
                    await (sniff >= 0 && sniff < stun && tls < stun && stun < domain).Should().BeTrue();
                    await ruleArray[stun]!["outbound"]!.GetValue<string>().Should().BeEqualTo(Global.ProxyTag);
                }
                else await stun.Should().BeEqualTo(-1);
                await rules.Any(rule => rule.Contains("skipped.example")).Should().BeEqualTo(core == ECoreType.sing_box);
                await document["dns"]!.ToJsonString().Contains("skipped.example").Should().BeEqualTo(core == ECoreType.sing_box);
                await document["outbounds"]!.ToJsonString().Contains(linked.Address).Should().BeEqualTo(core == ECoreType.sing_box);
                await (await ConfigHandler.GetDefaultRouting(config)).RuleSet.Should().BeEqualTo(json);
            }
        }
        finally
        {
            if (frontTun)
            {
                if (originalTemplate == null) await db.DeleteAsync(template);
                else await db.ReplaceAsync(originalTemplate);
            }
            await db.DeleteAsync(linked);
            if (previous == null) await db.DeleteAsync(routing);
            else await db.ReplaceAsync(previous);
        }
    }

    [Test]
    [Arguments("{")]
    [Arguments("[null]")]
    [Arguments("""[{"Protocol":[null]}]""")]
    public async Task InvalidRuleSetDoesNotStopTheExistingProcessOrLaunchAnother(string invalidJson)
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix owned-process fixture."); return; }
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        using var process = new ProcessService("/bin/sleep", "30", Path.GetTempPath(), false, false, null, null);
        await process.StartAsync();
        process.IsReady = true;
        var manager = new CoreManager(_ => throw new Exception("An invalid rule set must not launch a core"));
        typeof(CoreManager).GetField("_processService", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, process);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            RoutingItem = new RoutingItem { RuleSet = invalidJson }
        };
        try
        {
            await (await manager.LoadCore(context, null)).Should().BeEqualTo(CoreLoadResult.Failed);
            await manager.LastError!.Should().Contain(RoutingProtocolPolicy.InvalidRulesMessage);
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
