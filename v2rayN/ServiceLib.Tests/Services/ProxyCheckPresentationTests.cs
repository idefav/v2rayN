using ServiceLib.ViewModels;

namespace ServiceLib.Tests.Services;

public class ProxyCheckPresentationTests
{
    [Test]
    public async Task CapabilityChecksDoNotClaimTrafficConnectivity()
    {
        var result = new ProxyDiagnosticResult { State = "passed", Connectivity = "passed", ObservationType = "not-applicable", EgressState = "not-applicable" };
        var row = new ProxyCheckResultRow(result);
        await row.IsCommunicationPassed.Should().BeFalse();
        await row.Communication.Should().BeEqualTo(ProxyCheckText.T("已核对（非通信）", "Checked (not traffic)"));
        result.State = result.Connectivity = "skipped";
        await row.Communication.Contains(ProxyCheckText.State("skipped")).Should().BeTrue();
        result.State = result.Connectivity = "running";
        await row.Communication.Should().BeEqualTo(ProxyCheckText.State("running"));
        var traffic = new ProxyCheckResultRow(new() { State = "passed", Connectivity = "passed", ObservationType = "client-egress" });
        await traffic.IsCommunicationPassed.Should().BeTrue();
        await traffic.Communication.Should().BeEqualTo(ProxyCheckText.State("passed"));
    }

    [Test]
    public async Task IncompleteObserverProfilesNeverRenderABlankHost()
    {
        using var model = new ProxyCheckViewModel();
        model.UseObserver = true;
        foreach (var json in new[] { "{}", "{\"domain\":\"   \"}", "{", "null" })
        {
            model.ObserverProfileJson = json;
            await string.IsNullOrWhiteSpace(model.ObserverHost).Should().BeFalse();
        }
    }

    [Test]
    public async Task PresentationSeparatesConnectivityComparisonAndCoverage()
    {
        using var model = new ProxyCheckViewModel(); model.Results.Clear();
        model.Results.Add(new() { Id = "observed", Group = "UDP", State = "unknown", Connectivity = "passed", EgressState = "not-configured", Egress = "198.51.100.7" });
        model.Results.Add(new() { Id = "wrong", Group = "TCP/TLS", State = "failed", Connectivity = "passed", EgressState = "failed", Egress = "198.51.100.8" });
        model.Results.Add(new() { Id = "settings", Group = "Capture", State = "passed", Connectivity = "passed", EgressState = "not-applicable", ObservationType = "not-applicable" });
        model.Results.Add(new() { Id = "v6", Group = "IPv6", State = "skipped", Connectivity = "skipped" });
        await model.ReachableCount.Should().BeEqualTo(2);
        await model.MatchedCount.Should().BeEqualTo(0);
        await model.AttentionCount.Should().BeEqualTo(1);
        await model.UncoveredCount.Should().BeEqualTo(1);
        model.SelectedFilter = 1;
        await model.ResultGroups.SelectMany(g => g.Rows).Single().Source.Id.Should().BeEqualTo("wrong");
        await model.Results.Count.Should().BeEqualTo(4);
    }

    [Test]
    public async Task SearchAndUpdatesPreserveEvidenceExpansionWithoutChangingResults()
    {
        using var model = new ProxyCheckViewModel(); model.Results.Clear(); model.SearchText = ""; model.SelectedFilter = 0;
        model.Results.Add(new() { Id = "dns", Group = "DNS", Target = "dns.example.net:53", Path = "SOCKS5", State = "passed", Connectivity = "passed", EgressState = "passed", Egress = "198.51.100.7" });
        model.ResultGroups[0].Rows[0].IsExpanded = true;
        model.Results.Add(new() { Id = "http", Group = "TCP/TLS", Target = "https://example.net/", State = "running", Connectivity = "pending" });
        await model.ResultGroups.SelectMany(g => g.Rows).Single(r => r.Source.Id == "dns").IsExpanded.Should().BeTrue();
        model.SearchText = "198.51.100.7";
        await model.ResultGroups.SelectMany(g => g.Rows).Single().Source.Id.Should().BeEqualTo("dns");
        await model.TotalCount.Should().BeEqualTo(2);
        await model.FinishedCount.Should().BeEqualTo(1);
        model.SearchText = "missing";
        await model.HasVisibleResults.Should().BeFalse();
        await model.Results.Count.Should().BeEqualTo(2);
    }

    [Test]
    public async Task CancelledChecksAreSeparateFromCompletedChecks()
    {
        using var model = new ProxyCheckViewModel(); model.Results.Clear(); model.SelectedFilter = 0; model.SearchText = "";
        model.Results.Add(new() { Id = "not-started", Group = "UDP", State = "cancelled", Connectivity = "cancelled", Attempts = 0 });
        await model.FinishedCount.Should().BeEqualTo(0);
        await model.CancelledCount.Should().BeEqualTo(1);
        await model.CompletionPercent.Should().BeEqualTo(0.0);
        await model.ProgressLabel.StartsWith("0/1 ").Should().BeTrue();
        await model.ResultGroups[0].Summary.StartsWith("0/1 ").Should().BeTrue();

        model.Results.Add(new() { Id = "interrupted", Group = "UDP", State = "cancelled", Connectivity = "cancelled", Attempts = 1 });
        model.Results.Add(new() { Id = "success", Group = "UDP", State = "passed", Connectivity = "passed", EgressState = "passed", Attempts = 3 });
        model.Results.Add(new() { Id = "failure", Group = "UDP", State = "failed", Connectivity = "failed", Attempts = 3 });
        await model.FinishedCount.Should().BeEqualTo(2);
        await model.CancelledCount.Should().BeEqualTo(2);
        await model.CompletionPercent.Should().BeEqualTo(50.0);
        await model.ProgressLabel.StartsWith("2/4 ").Should().BeTrue();
        await model.ResultGroups[0].Summary.StartsWith("2/4 ").Should().BeTrue();
    }

    [Test]
    public async Task PassedFilterRequiresBothConnectivityAndEgressMatch()
    {
        using var model = new ProxyCheckViewModel(); model.Results.Clear(); model.SelectedFilter = 0; model.SearchText = "";
        model.Results.Add(new() { Id = "settings", Group = "Capture", State = "passed", Connectivity = "passed", EgressState = "not-applicable", ObservationType = "not-applicable" });
        model.Results.Add(new() { Id = "resolver", Group = "DNS", State = "passed", Connectivity = "passed", EgressState = "not-applicable", ObservationType = "recursive-resolver", Egress = "198.51.100.7" });
        model.Results.Add(new() { Id = "no-expectation", Group = "DNS", State = "unknown", Connectivity = "passed", EgressState = "not-configured", Egress = "198.51.100.7" });
        model.Results.Add(new() { Id = "mismatch", Group = "UDP", State = "failed", Connectivity = "passed", EgressState = "failed" });
        model.Results.Add(new() { Id = "return-failed", Group = "UDP", State = "failed", Connectivity = "failed", EgressState = "passed" });
        model.Results.Add(new() { Id = "verified", Group = "TCP/TLS", State = "passed", Connectivity = "passed", EgressState = "passed", Egress = "198.51.100.7" });
        model.SelectedFilter = 2;
        await model.ResultGroups.SelectMany(g => g.Rows).Single().Source.Id.Should().BeEqualTo("verified");
        await model.Results.Count.Should().BeEqualTo(6);
        // Egress matches remain independent of return-path success in the overview.
        await model.MatchedCount.Should().BeEqualTo(2);
    }
}
