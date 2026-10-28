using ServiceLib.ViewModels;

namespace ServiceLib.Tests.Services;

public class ProxyCheckRegionTests
{
    private static ProxyDiagnosticResult Row() => new()
    {
        Id = "geo", Group = "UDP", Egress = "8.8.8.8, 1.1.1.1", ObservationType = "direct-peer",
        State = "failed", Connectivity = "passed", EgressState = "failed", Expected = "203.0.113.1",
        ExpectedCountryCodes = ["SG", "JP"],
        EgressLocations = [new("8.8.8.8", "SG", "Singapore", null, null, "fixture.invalid", DateTimeOffset.UnixEpoch),
            new("1.1.1.1", "JP", "Japan", null, "Tokyo", "fixture.invalid", DateTimeOffset.UnixEpoch)]
    };

    [Test]
    public async Task MultipleRegionsAreOrAndEachAddressRetainsItsOwnFlagAndVerdict()
    {
        var result = Row(); var row = new ProxyCheckResultRow(result);
        await result.RegionComparison.Should().BeEqualTo("passed");
        await row.ExitAddresses[0].CountryCode.Should().BeEqualTo("SG");
        await row.ExitAddresses[1].CountryCode.Should().BeEqualTo("JP");
        result.ExpectedCountryCodes = ["SG"];
        await row.ExitAddresses[0].RegionMatches.Should().BeTrue();
        await row.ExitAddresses[1].RegionDiffers.Should().BeTrue();
        await result.RegionComparison.Should().BeEqualTo("failed");
        await result.State.Should().BeEqualTo("failed");
        await result.EgressState.Should().BeEqualTo("failed");
        await result.Connectivity.Should().BeEqualTo("passed");
    }

    [Test]
    public async Task RegionDifferencesNeverChangeIpVerdictsCountersOrRetryEligibility()
    {
        using var model = new ProxyCheckViewModel(); model.Results.Clear();
        var result = Row(); result.State = result.EgressState = "passed";
        result.ExpectedCountryCodes = ["US"]; model.Results.Add(result);
        await result.RegionComparison.Should().BeEqualTo("failed");
        await model.AttentionCount.Should().BeEqualTo(0);
        await model.MatchedCount.Should().BeEqualTo(1);
        await model.ResultGroups[0].AttentionCount.Should().BeEqualTo(0);
        await model.ResultGroups[0].Rows[0].IsPassed.Should().BeTrue();
        model.SelectedFilter = 1;
        await model.HasVisibleResults.Should().BeFalse();
        await result.State.Should().BeEqualTo("passed");
    }

    [Test]
    public async Task UnknownPendingPartialAndUnconfiguredRegionsNeverBecomeFalseMatches()
    {
        var result = Row(); result.EgressLocations.Clear();
        result.LocationState = "loading";
        await result.RegionComparison.Should().BeEqualTo("loading");
        await new ProxyCheckResultRow(result).ExitAddresses[0].CountryCode.Should().BeNull();
        result.LocationState = "unavailable";
        await result.RegionComparison.Should().BeEqualTo("unknown");
        result.EgressLocations = [new("8.8.8.8", "SG", "Singapore", null, null, "fixture.invalid", DateTimeOffset.UnixEpoch)];
        await result.RegionComparison.Should().BeEqualTo("unknown");
        result.EgressLocations[0] = result.EgressLocations[0] with { CountryCode = "US" };
        await result.RegionComparison.Should().BeEqualTo("failed");
        result.ExpectedCountryCodes.Clear();
        await result.RegionComparison.Should().BeEqualTo("not-configured");
    }

    [Test]
    public async Task ResolverAndCapabilityRowsAreNotComparedAsClientRegions()
    {
        var result = Row(); result.ObservationType = "recursive-resolver";
        await result.RegionComparison.Should().BeEqualTo("not-applicable");
        await new ProxyCheckResultRow(result).ExitAddresses.All(a => a.RegionState == "not-applicable").Should().BeTrue();
        result.ObservationType = "not-applicable";
        await new ProxyCheckResultRow(result).ExitAddresses.Count.Should().BeEqualTo(0);
    }

    [Test]
    public async Task CountrySelectionIsSearchableAndCannotRewriteExistingReportSnapshots()
    {
        using var model = new ProxyCheckViewModel(); model.ClearCountrySelection();
        model.CountrySearch = "SG";
        await model.FilteredCountries.Any(c => c.Code == "SG").Should().BeTrue();
        model.CountryChoices.Single(c => c.Code == "SG").IsSelected = true;
        var options = new ProxyCheckOptions { ExpectedCountryCodes = model.SelectedCountryCodes };
        var row = ProxyCheckService.NewResult(new("test", "UDP", "stun", "stun.example:3478", "socks"), options);
        options.ExpectedCountryCodes.Clear(); model.ClearCountrySelection();
        await row.ExpectedCountryCodes.Single().Should().BeEqualTo("SG");
        await model.SelectedCountryCodes.Count.Should().BeEqualTo(0);
        model.CountrySearch = "does-not-exist";
        await model.HasCountryMatches.Should().BeFalse();
        model.CountrySearch = "Japan";
        await model.FilteredCountries.Any(c => c.Code == "JP").Should().BeTrue();
    }

    [Test]
    public async Task ReportsRetainExpectedRegionsAndSeparateAdvisoryDataFromIpEvidence()
    {
        var row = Row(); row.Evidence = "signed evidence unchanged";
        var json = JsonSerializer.Serialize(row, ProxyCheckService.Json);
        await json.Contains("\"regionComparison\": \"passed\"").Should().BeTrue();
        var restored = JsonSerializer.Deserialize<ProxyDiagnosticResult>(json, ProxyCheckService.Json)!;
        await restored.ExpectedCountryCodes.Count.Should().BeEqualTo(2);
        await restored.RegionComparison.Should().BeEqualTo("passed");
        await restored.State.Should().BeEqualTo("failed");
        await restored.Evidence.Should().BeEqualTo("signed evidence unchanged");
        var legacy = JsonSerializer.Deserialize<ProxyDiagnosticResult>("{\"egress\":\"8.8.8.8\"}", ProxyCheckService.Json)!;
        await legacy.RegionComparison.Should().BeEqualTo("not-configured");
        var options = new ProxyCheckOptions { ExpectedCountryCodes = [" sg ", "jp"] };
        ProxyCheckService.Validate(options);
        await ProxyCheckService.NewResult(new("test", "UDP", "udp", "example:3479", "socks"), options).ExpectedCountryCodes[0].Should().BeEqualTo("SG");
        var rejected = false;
        try { ProxyCheckService.Validate(new() { ExpectedCountryCodes = ["not-a-country"] }); } catch (ArgumentException) { rejected = true; }
        await rejected.Should().BeTrue();
    }
}
