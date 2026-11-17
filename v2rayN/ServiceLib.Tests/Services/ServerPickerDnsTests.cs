using ServiceLib.Tests.CoreConfig;
using ServiceLib.ViewModels;
using System.Text.Json.Nodes;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class ServerPickerDnsTests
{
    [Test]
    public async Task DnsRequestIsOneBoundedJsonLine()
    {
        var bytes = SshGatewayRemote.SerializeDnsRequest(new { version = 1, action = "inspect", values = new[] { "one", "two" } });
        var text = Encoding.UTF8.GetString(bytes);
        await text.Count(c => c == '\n').Should().BeEqualTo(1);
        await JsonNode.Parse(text)!["version"]!.GetValue<int>().Should().BeEqualTo(1);
    }
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DnsComparisonUsesObservedSourcesAndFreshLabels(bool additionalResolver)
    {
        var names = new HashSet<string>();
        var result = await SshDnsService.ObserveAsync((url, ct) =>
        {
            if (url.Contains("/json/")) return Task.FromResult(new JsonObject { ["ip"] = "203.0.113.10" });
            var name = new Uri(url).Host;
            names.Add(name);
            var sources = new JsonObject { ["203.0.113.10"] = 1 };
            if (additionalResolver) sources["203.0.113.11"] = 1;
            return Task.FromResult(new JsonObject { ["session"] = name[..40], ["ip"] = sources });
        }, null, default);
        await names.Count.Should().BeEqualTo(3);
        await result.Probes.Should().BeEqualTo(3);
        await result.Verdict.Should().BeEqualTo(additionalResolver ? SshDnsVerdict.Mismatch : SshDnsVerdict.Match);
    }

    [Test]
    public async Task MissingObserverEvidenceNeverCountsAsSuccess()
    {
        var result = await SshDnsService.ObserveAsync((url, ct) => Task.FromResult(url.Contains("/json/")
            ? new JsonObject { ["ip"] = "203.0.113.10" } : new JsonObject { ["ip"] = new JsonObject() }), null, default);
        await result.Verdict.Should().BeEqualTo(SshDnsVerdict.Unknown);
        await result.Probes.Should().BeEqualTo(0);
    }

    [Test]
    public async Task ExitChangeAndCancellationAreNotMismatch()
    {
        var exits = 0;
        Task<JsonObject> Fetch(string url, CancellationToken ct)
        {
            if (url.Contains("/json/")) return Task.FromResult(new JsonObject { ["ip"] = ++exits == 1 ? "203.0.113.10" : "203.0.113.11" });
            return Task.FromResult(new JsonObject { ["session"] = new Uri(url).Host[..40], ["ip"] = new JsonObject { ["203.0.113.10"] = 1 } });
        }
        var result = await SshDnsService.ObserveAsync(Fetch, null, default);
        await result.Verdict.Should().BeEqualTo(SshDnsVerdict.Unknown);
        await result.Error.Should().BeEqualTo("E_EXIT_CHANGED");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        result = await SshDnsService.ObserveAsync(Fetch, null, cancel.Token);
        await result.Verdict.Should().BeEqualTo(SshDnsVerdict.Cancelled);
    }

    [Test]
    public async Task SameNamedGroupsStaySeparateAndFiltersCompose()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        config.UiItem.ConnectionFavorites = ["b"];
        using var picker = new ServerPickerViewModel(config);
        picker.Load([Node("a", "one"), Node("b", "two"), Node("c", "")],
            [new() { Id = "one", Remarks = "JMS", Sort = 1 }, new() { Id = "two", Remarks = "JMS", Sort = 2 }], new Dictionary<string, ProfileExItem>());
        picker.GroupId = "*"; picker.Refresh();
        await picker.Rows.Count(r => r.IsHeader).Should().BeEqualTo(3);
        picker.GroupId = "one"; picker.Refresh();
        await picker.VisibleCount.Should().BeEqualTo(1);
        await picker.Rows.Single().Node.IndexId.Should().BeEqualTo("a");
        picker.GroupId = "*"; picker.FavoritesOnly = true; picker.Search = "JMS"; picker.Refresh();
        await picker.VisibleCount.Should().BeEqualTo(1);
        await picker.Rows.Last().Node.IndexId.Should().BeEqualTo("b");
        picker.GroupId = ""; picker.FavoritesOnly = false; picker.Search = ""; picker.Refresh();
        await picker.Rows.Single().Node.IndexId.Should().BeEqualTo("c");
    }

    private sealed class DelayedProbe : IServerProbeService
    {
        internal int Calls, Active, Peak;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ServerProbeResult> TestAsync(ProfileItem node, Config config, Guid id, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref Active);
            Peak = Math.Max(Peak, active);
            Started.TrySetResult();
            try { await Release.Task; }
            finally { Interlocked.Decrement(ref Active); }
            return new(node.IndexId, ServerProbeService.Identity(node), id, DateTimeOffset.UtcNow,
                new(ServerProbeState.Passed, 2), new(ServerProbeState.Passed, 15));
        }
    }

    [Test]
    public async Task CancelledBatchDropsLateResultsAndDuplicateClick()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        SQLiteHelper.Instance.CreateTable<ProfileExItem>();
        var node = Node("cancel-fixture", "");
        var probe = new DelayedProbe();
        using var picker = new ServerPickerViewModel(config, probe, _ => Task.FromResult<ProfileItem?>(node));
        picker.Load([node], [], new Dictionary<string, ProfileExItem>());
        var first = picker.TestVisibleAsync();
        await probe.Started.Task;
        await picker.TestVisibleAsync();
        picker.Close();
        probe.Release.SetResult();
        await first;
        await probe.Calls.Should().BeEqualTo(1);
        await picker.Busy.Should().BeFalse();
        await picker.Rows.Single(r => !r.IsHeader).Results.Contains("15 ms").Should().BeFalse();
    }

    [Test]
    public async Task IdentityChangesWhenCredentialsOrSubscriptionNodeChange()
    {
        var node = Node("a", "group");
        var original = ServerProbeService.Identity(node);
        node.Password = "updated";
        await (original == ServerProbeService.Identity(node)).Should().BeFalse();
        node = Node("b", "group");
        await (original == ServerProbeService.Identity(node)).Should().BeFalse();
    }
    [Test]
    public async Task EditedNodeCannotReceiveThePreviousConfigurationResult()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = Node("edited-fixture", "");
        var probe = new DelayedProbe();
        using var picker = new ServerPickerViewModel(config, probe, _ => Task.FromResult<ProfileItem?>(node));
        picker.Load([node], [], new Dictionary<string, ProfileExItem>());
        var test = picker.TestVisibleAsync();
        await probe.Started.Task;
        node.Password = "changed-during-probe";
        probe.Release.SetResult();
        await test;
        await picker.Rows.Single(r => !r.IsHeader).Results.Contains("15 ms").Should().BeFalse();
    }
    private static ProfileItem Node(string id, string group) => new()
    { IndexId = id, Subid = group, Remarks = id, Address = "127.0.0.1", Port = 12345, ConfigType = EConfigType.SOCKS };
}
