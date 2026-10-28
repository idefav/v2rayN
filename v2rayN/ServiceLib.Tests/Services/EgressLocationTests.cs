using ServiceLib.ViewModels;
using System.Globalization;

namespace ServiceLib.Tests.Services;

public class EgressLocationTests
{
    [Test]
    [Arguments("https://api.ip.sb/geoip", "8.8.8.8", "https://api.ip.sb/geoip/8.8.8.8")]
    [Arguments("https://api.ipapi.is/?q=1.1.1.1&key=fixture", "8.8.8.8", "https://api.ipapi.is/?key=fixture&q=8.8.8.8")]
    [Arguments("https://lookup.example/geo/{ip}", "2001:4860:4860::8888", "https://lookup.example/geo/2001%3A4860%3A4860%3A%3A8888")]
    [Arguments("https://self-only.example/ip", "8.8.8.8", "https://api.ip.sb/geoip/8.8.8.8")]
    public async Task AlwaysQueriesTheObservedAddress(string provider, string address, string expected) =>
        await EgressLocationService.LookupUrl(provider, address).Should().BeEqualTo(expected);

    [Test]
    [Arguments("127.0.0.1")]
    [Arguments("10.0.0.1")]
    [Arguments("::1")]
    [Arguments("fd00::1")]
    [Arguments("not-an-ip")]
    public async Task NonPublicAndInvalidAddressesDoNotMakeRequests(string address)
    {
        var calls = 0;
        var service = new EgressLocationService((_, _) => { calls++; return Task.FromResult<string?>(null); });
        await (await service.LookupAsync(address, null)).Should().BeNull();
        await calls.Should().BeEqualTo(0);
    }

    [Test]
    public async Task AProviderReturningItsCallerAddressCannotMislabelTheObservedExit()
    {
        var service = new EgressLocationService((_, _) => Task.FromResult<string?>("""{"ip":"1.1.1.1","country_code":"AU"}"""));
        await (await service.LookupAsync("8.8.8.8", null)).Should().BeNull();
    }

    [Test]
    public async Task ConcurrentRowsShareLookupAndOneCancellationDoesNotCancelOthers()
    {
        var response = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new EgressLocationService((_, _) => { Interlocked.Increment(ref calls); return response.Task; });
        using var cancel = new CancellationTokenSource();
        var first = service.LookupAsync("8.8.8.8", null, cancel.Token);
        var second = service.LookupAsync("8.8.8.8", null);
        cancel.Cancel();
        var cancelled = false;
        try { await first; } catch (OperationCanceledException) { cancelled = true; }
        response.SetResult("""{"ip":"8.8.8.8","country_code":"US","country":"United States","city":"Mountain View"}""");
        var result = await second;
        await cancelled.Should().BeTrue();
        await result!.City.Should().BeEqualTo("Mountain View");
        await (await service.LookupAsync("8.8.8.8", null)).Should().BeEqualTo(result);
        await calls.Should().BeEqualTo(1);
    }

    [Test]
    public async Task SuccessfulAndFailedLookupsHaveBoundedCacheLifetimes()
    {
        var clock = new Clock();
        var calls = 0;
        var json = """{"ip":"8.8.8.8","country_code":"US"}""";
        var service = new EgressLocationService((_, _) => { calls++; return Task.FromResult<string?>(json); }, clock);
        await service.LookupAsync("8.8.8.8", null);
        clock.Now += TimeSpan.FromHours(23);
        await service.LookupAsync("8.8.8.8", null);
        await calls.Should().BeEqualTo(1);
        clock.Now += TimeSpan.FromHours(2); json = "not json";
        await (await service.LookupAsync("8.8.8.8", null)).Should().BeNull();
        await (await service.LookupAsync("8.8.8.8", null)).Should().BeNull();
        await calls.Should().BeEqualTo(2);
        clock.Now += TimeSpan.FromMinutes(11);
        await service.LookupAsync("8.8.8.8", null);
        await calls.Should().BeEqualTo(3);
    }

    [Test]
    [Arguments("en", "Japan · Tokyo")]
    [Arguments("zh-Hans", "日本 · Tokyo")]
    public async Task CountryNamesFollowTheApplicationLanguage(string language, string expected)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new(language);
            var location = new EgressLocation("8.8.8.8", "JP", "Japan", "Tokyo", "Tokyo", "fixture.invalid", DateTimeOffset.UnixEpoch);
            await location.Label.Should().BeEqualTo(expected);
            var singapore = location with { CountryCode = "SG", Country = "Singapore", Region = null, City = "Singapore" };
            await singapore.Label.Should().BeEqualTo(language == "en" ? "Singapore" : "新加坡");
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    [Test]
    public async Task IPv4MappedAddressesShareTheCanonicalCacheEntry()
    {
        var calls = 0;
        var service = new EgressLocationService((_, _) =>
        {
            calls++;
            return Task.FromResult<string?>("""{"ip":"::ffff:8.8.8.8","location":{"country":"United States","country_code":"US"}}""");
        });
        var first = await service.LookupAsync("::ffff:8.8.8.8", null);
        await first!.Ip.Should().BeEqualTo("8.8.8.8");
        await (await service.LookupAsync("8.8.8.8", null)).Should().BeEqualTo(first);
        await calls.Should().BeEqualTo(1);
    }

    [Test]
    public async Task ProviderFailureBecomesUnavailableAndIsCached()
    {
        var calls = 0;
        var service = new EgressLocationService((_, _) => { calls++; throw new HttpRequestException("fixture unavailable"); });
        await (await service.LookupAsync("8.8.8.8", null)).Should().BeNull();
        await (await service.LookupAsync("8.8.8.8", null)).Should().BeNull();
        await calls.Should().BeEqualTo(1);
        var result = new ProxyDiagnosticResult { Egress = "8.8.8.8", State = "passed", Connectivity = "passed", EgressState = "passed", LocationState = "unavailable" };
        var row = new ProxyCheckResultRow(result);
        await row.IsPassed.Should().BeTrue();
        await row.LocationLabel.Should().BeEqualTo(ProxyCheckText.T("地域未知", "Location unavailable"));
    }

    [Test]
    public async Task LocationIsSeparateFromSignedEvidenceAndMapsEachAddress()
    {
        var result = new ProxyDiagnosticResult
        {
            Id = "geo", Group = "UDP", Egress = "8.8.8.8, 2001:4860:4860:0:0:0:0:8888", State = "passed", Connectivity = "passed", EgressState = "passed",
            Evidence = "original evidence", ObservationType = "recursive-resolver",
            EgressLocations = [new("8.8.8.8", "US", "United States", "California", "Mountain View", "fixture.invalid", DateTimeOffset.UnixEpoch),
                new("2001:4860:4860::8888", "JP", "Japan", "Tokyo", "Tokyo", "fixture.invalid", DateTimeOffset.UnixEpoch)]
        };
        using var model = new ProxyCheckViewModel();
        model.Results.Add(result);
        var row = model.ResultGroups.SelectMany(g => g.Rows).Single();
        await row.LocationLabel.Contains("8.8.8.8:").Should().BeTrue();
        await row.LocationLabel.Contains("Tokyo").Should().BeTrue();
        await row.LocationLabel.Contains("Tokyo · Tokyo").Should().BeFalse();
        await row.EvidenceKind.Should().BeEqualTo(ProxyCheckText.T("递归解析器出口", "Resolver egress"));
        await result.Evidence.Should().BeEqualTo("original evidence");
        await result.EgressState.Should().BeEqualTo("passed");
        model.SearchText = "Mountain View";
        await model.HasVisibleResults.Should().BeTrue();
        var roundTrip = JsonSerializer.Deserialize<ProxyDiagnosticResult>(JsonSerializer.Serialize(result, ProxyCheckService.Json), ProxyCheckService.Json)!;
        await roundTrip.EgressLocations.Count.Should().BeEqualTo(2);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
