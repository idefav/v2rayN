using ServiceLib.Tests.CoreConfig;
using ServiceLib.Tests.CoreConfig.Singbox;

namespace ServiceLib.Tests.Handler;

[NotInParallel]
public class RouteSubscriptionTests
{
    [Test]
    public async Task RefreshKeepsIdentityAndPublishesOnlyCommittedSnapshots()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var db = SQLiteHelper.Instance;
        db.CreateTable<RoutingItem>();
        db.CreateTable<ProfileItem>();
        db.CreateTable<SubItem>();
        db.CreateTable<DNSItem>();
        db.CreateTable<FullConfigTemplateItem>();
        var previous = await db.TableAsync<RoutingItem>().FirstOrDefaultAsync(r => r.IsActive);
        var sub = new SubItem { Id = Guid.NewGuid().ToString(), Remarks = "JMS" };
        var route = new RoutingItem { Id = previous?.Id ?? Guid.NewGuid().ToString(), IsActive = true };
        await db.InsertAsync(sub);
        try
        {
            await Import("socks://127.0.0.1:4001#A\nsocks://127.0.0.1:4002#B", 2);
            var old = (await AppManager.Instance.ProfileItems(sub.Id))!.ToDictionary(p => p.Remarks);
            var fixedTarget = new RouteOutboundTarget { ProfileIds = [old["A"].IndexId, old["B"].IndexId] };
            var ssh = SshTests.Node();
            ssh.IndexId = Guid.NewGuid().ToString();
            ssh.Subid = sub.Id;
            ssh.IsSub = false;
            var sshOnly = new RouteOutboundTarget { ProfileIds = [ssh.IndexId] };
            var follow = new RouteOutboundTarget { SubscriptionId = sub.Id, SubscriptionName = sub.Remarks };
            route.RuleSet = JsonUtils.Serialize(new RulesItem[] {
                new() { Domain = ["full:manual.example"], OutboundTag = Global.BlockTag, OutboundTarget = fixedTarget },
                new() { Domain = ["full:follow.example"], OutboundTag = Global.BlockTag, OutboundTarget = follow },
                new() { Domain = ["full:other.example"], OutboundTag = Global.DirectTag },
                new() { Port = "5999", OutboundTag = Global.BlockTag, OutboundTarget = sshOnly }
            });
            await db.ReplaceAsync(route);
            await Import("socks://127.0.0.1:4001#Renamed\nsocks://127.0.0.1:4003#C", 2);
            var changed = (await AppManager.Instance.ProfileItems(sub.Id))!;
            await changed.Single(p => p.Remarks == "Renamed").IndexId.Should().BeEqualTo(old["A"].IndexId);
            await Import("not a subscription", -1);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await ConfigHandler.AddBatchServers(config, "[]", sub.Id, true, cancelled.Token);
                throw new InvalidOperationException("Cancelled update committed");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            await (await AppManager.Instance.ProfileItems(sub.Id))!.Select(p => p.IndexId).Order().SequenceEqual(changed.Select(p => p.IndexId).Order()).Should().BeTrue();

            await db.InsertAsync(ssh);
            var collision = new ProfileItem { IndexId = ssh.IndexId, Remarks = "collision", Subid = sub.Id,
                ConfigType = EConfigType.SOCKS, Port = 4010, Address = "127.0.0.1" };
            try
            {
                await db.ReplaceSubscriptionAsync(sub.Id, [collision], CancellationToken.None);
                throw new InvalidOperationException("An insert conflicting with a manual profile should fail");
            }
            catch (SQLiteException) { }
            var retained = (await AppManager.Instance.ProfileItems(sub.Id))!;
            await retained.Count.Should().BeEqualTo(3);
            await retained.Any(p => p.IndexId == old["A"].IndexId).Should().BeTrue();
            config.TunModeItem.EnableTun = config.TunModeItem.EnableLegacyProtect = true;
            var xray = await CoreConfigContextBuilder.BuildAll(config, CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray));
            await xray.Success.Should().BeTrue();
            await xray.MainResult.Context.RouteTargets[fixedTarget.Tag()].Count.Should().BeEqualTo(1);
            await xray.MainResult.Context.RouteTargets[follow.Tag()].Count.Should().BeEqualTo(2);
            await xray.PreSocksResult!.Context.RouteTargets[follow.Tag()].Count.Should().BeEqualTo(2);
            await xray.MainResult.Context.RouteTargets[sshOnly.Tag()].Count.Should().BeEqualTo(0);
            var xrayJson = await CoreConfigHandler.GenerateClientConfig(xray.MainResult.Context, null);
            var rules = JsonNode.Parse(xrayJson.Data!.ToString()!)!["routing"]!["rules"]!.AsArray();
            await rules.Single(r => (string?)r!["port"] == "5999")!["outboundTag"]!.GetValue<string>().Should().BeEqualTo(Global.BlockTag);

            config.TunModeItem.EnableTun = false;
            config.SimpleDNSItem.EgressMode = DnsEgressMode.FollowRouting;
            config.SimpleDNSItem.RemoteDNS = "https://1.1.1.1/dns-query";
            config.SimpleDNSItem.DirectDNS = "223.5.5.5";
            var main = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);
            var sing = await CoreConfigContextBuilder.Build(config, main);
            await sing.Context.RouteTargets[follow.Tag()].Count.Should().BeEqualTo(3);
            var document = await Generate(sing.Context);
            await document.dns!.servers.Any(s => s.detour == follow.Tag()).Should().BeTrue();
            await document.route.rules.Any(r => r.domain?.Contains("follow.example") == true && r.outbound == follow.Tag()).Should().BeTrue();
            await document.outbounds.Any(o => o.tag == fixedTarget.Tag() && o.type == "socks").Should().BeTrue();

            sub.Filter = "^does-not-match$";
            await db.UpdateAsync(sub);
            await Import("socks://127.0.0.1:4001#Renamed", 0);
            // Manually added machines in the same subscription survive imported snapshot replacement.
            await (await AppManager.Instance.ProfileItems(sub.Id))!.Single().IndexId.Should().BeEqualTo(ssh.IndexId);
            await db.DeleteAsync(ssh);
            sub.Filter = "";
            await db.UpdateAsync(sub);
            await Import("[]", 0);
            await AssertBlocked();
            await Import("socks://127.0.0.1:4004#Recovered", 1);
            await (await CoreConfigContextBuilder.Build(config, main)).Context.RouteTargets[follow.Tag()].Count.Should().BeEqualTo(1);
            await ConfigHandler.DeleteSubItem(config, sub.Id);
            await AssertBlocked();

            async Task AssertBlocked()
            {
                var result = await CoreConfigContextBuilder.Build(config, main);
                await result.Success.Should().BeTrue();
                var doc = await Generate(result.Context);
                var matchingRules = doc.route.rules.Where(r => r.domain?.Any(domain => domain.EndsWith(".example")) == true).ToList();
                await matchingRules.Select(r => r.action ?? r.outbound).SequenceEqual(["reject", "reject", "direct"]).Should().BeTrue();
                await doc.dns!.rules.Any(r => r.domain?.Contains("follow.example") == true && r.rcode == "NXDOMAIN").Should().BeTrue();
            }

            async Task Import(string content, int count) =>
                await (await ConfigHandler.AddBatchServers(config, content, sub.Id, true)).Should().BeEqualTo(count);
        }
        finally
        {
            await ConfigHandler.DeleteSubItem(config, sub.Id);
            if (previous == null) await db.DeleteAsync(route); else await db.ReplaceAsync(previous);
        }
    }

    [Test]
    public async Task AmbiguousMachineIsNeverReboundByName()
    {
        ProfileItem Row(string id, int port) => new() { IndexId = id, ConfigType = EConfigType.SOCKS,
            Remarks = "same", Address = "localhost", Port = port };
        var replacement = new List<ProfileItem> { Row("new-a", 80), Row("new-b", 80), Row("new-c", 82) };
        SubscriptionProfileIdentity.Preserve([Row("old-a", 80), Row("old-b", 80), Row("old-c", 81)], replacement);
        await replacement.Select(p => p.IndexId).SequenceEqual(["new-a", "new-b", "new-c"]).Should().BeTrue();
    }

    private static async Task<SingboxConfig> Generate(CoreConfigContext context)
    {
        var result = await CoreConfigHandler.GenerateClientConfig(context, null);
        if (!result.Success) throw new InvalidOperationException(result.Msg);
        return JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
    }
}
