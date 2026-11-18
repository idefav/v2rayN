namespace ServiceLib.Tests.CoreConfig;

[NotInParallel]
public class ConnectionProductionMatrixTests
{
    [Test]
    [Arguments(EConfigType.VLESS, false, false, false)]
    [Arguments(EConfigType.VLESS, false, true, true)]
    [Arguments(EConfigType.VLESS, true, false, true)]
    [Arguments(EConfigType.VLESS, true, true, false)]
    [Arguments(EConfigType.Shadowsocks, false, false, true)]
    [Arguments(EConfigType.Shadowsocks, false, true, false)]
    [Arguments(EConfigType.Shadowsocks, true, false, false)]
    [Arguments(EConfigType.Shadowsocks, true, true, true)]
    public async Task GeneratedNativeAndFrontTunConfigurationsPassFullPreflight(EConfigType protocol, bool frontTun, bool statistics, bool extra)
    {
        var xray = Environment.GetEnvironmentVariable("HANDOFF_TEST_XRAY");
        var singbox = Environment.GetEnvironmentVariable("TUN_TEST_SINGBOX");
        if (string.IsNullOrEmpty(xray) || string.IsNullOrEmpty(singbox)) { Skip.Test("Set HANDOFF_TEST_XRAY and TUN_TEST_SINGBOX for native config validation"); return; }
        var config = CoreConfigTestFactory.CreateConfig();
        config.TunModeItem.EnableTun = true;
        config.TunModeItem.EnableLegacyProtect = frontTun;
        config.GuiItem.EnableStatistics = statistics;
        config.Inbound[0].LocalPort = 24560;
        config.Inbound[0].SecondLocalPortEnabled = extra;
        config.Inbound[0].AllowLANConn = extra;
        config.Inbound[0].NewPort4LAN = extra;
        config.Inbound[0].User = "matrix"; config.Inbound[0].Pass = "test-password";
        config.CoreTypeItem = [new() { ConfigType = protocol, CoreType = ECoreType.Xray }];
        // The builder must use this snapshot even when the live default core differs.
        var live = JsonUtils.DeepCopy(config);
        live.CoreTypeItem[0].CoreType = ECoreType.sing_box;
        live.Inbound[0].LocalPort = 24590;
        CoreConfigTestFactory.BindAppManagerConfig(live);
        SQLiteHelper.Instance.CreateTable<RoutingItem>(); SQLiteHelper.Instance.CreateTable<SubItem>();
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>(); SQLiteHelper.Instance.CreateTable<DNSItem>();
        var route = new RoutingItem { Id = "matrix-" + Guid.NewGuid(), RuleSet = JsonUtils.Serialize(new RulesItem[]
        {
            new() { Protocol = ["stun"], OutboundTag = Global.ProxyTag },
            new() { Protocol = ["tls", "stun"], Domain = ["full:skipped.example"], OutboundTag = Global.DirectTag }
        }) };
        config.RoutingBasicItem.RoutingIndexId = route.Id;
        await SQLiteHelper.Instance.ReplaceAsync(route);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, indexId: "");
        node.CoreType = null; node.ConfigType = protocol;
        node.Address = "127.0.0.1";
        if (protocol == EConfigType.VLESS) node.SetProtocolExtra(new() { VlessEncryption = "none" });
        else node.SetProtocolExtra(new() { SsMethod = "aes-128-gcm" });
        var dir = Path.Combine(Path.GetTempPath(), "production-matrix-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var built = await CoreConfigContextBuilder.BuildAll(config, node);
            if (!built.Success) throw new Exception(string.Join(";", built.CombinedValidatorResult.Errors));
            await built.MainResult.Context.RunCoreType.Should().BeEqualTo(ECoreType.Xray);
            await (built.PreSocksResult != null).Should().BeEqualTo(frontTun);
            var contexts = new[] { built.MainResult.Context, built.PreSocksResult?.Context }.OfType<CoreConfigContext>().ToArray();
            var plans = new List<CoreListenerPlan>();
            for (var i = 0; i < contexts.Length; i++)
            {
                var context = contexts[i] with { StatisticsPort = 24600 + i, FullConfigTemplate = new(), RawDnsItem = null, RoutingItem = route };
                var path = Path.Combine(dir, i + ".json");
                var result = await CoreConfigHandler.GenerateClientConfig(context, path);
                if (!result.Success) throw new Exception(result.Msg);
                var generated = await File.ReadAllTextAsync(path);
                await generated.Should().NotContain("\"stun\"");
                await generated.Should().NotContain("skipped.example");
                var plan = CoreListenerPlan.Read(generated, context.RunCoreType);
                plans.Add(plan);
                await plan.Tuns.Count.Should().BeEqualTo(context.IsTunEnabled ? 1 : 0);
                var binary = context.RunCoreType == ECoreType.Xray ? xray : singbox;
                var environment = new Dictionary<string, string?>(CoreInfoManager.Instance.GetCoreInfo(context.RunCoreType)!.Environment);
                if (context.RunCoreType == ECoreType.Xray)
                    environment[Global.XrayLocalAsset] = Environment.GetEnvironmentVariable("CORE_TEST_ASSETS")
                        ?? Path.GetDirectoryName(Path.GetDirectoryName(xray));
                var error = await RoutingCoreCompatibility.CheckExecutableAsync(binary, context.RunCoreType, path,
                    environment, default);
                if (error != null) throw new Exception(error);
            }
            CoreListenerPlan.ValidateTogether(plans[0], plans.Count == 2 ? plans[1] : null);
            await plans[0].Ports.Any(p => p.Port == 24560).Should().BeTrue();
            await plans[0].Ports.Any(p => p.Port == 24590).Should().BeFalse();
            await plans[0].Ports.Any(p => p.Source == "metrics.listen").Should().BeEqualTo(statistics);
            if (extra) await plans[0].Ports.Any(p => p.Authentication && p.Address.Equals(IPAddress.Any)).Should().BeTrue();
            // Native check only: launching either topology changes host routes and is separately opt-in.
        }
        finally { await SQLiteHelper.Instance.DeleteAsync(route); Directory.Delete(dir, true); }
    }
}
