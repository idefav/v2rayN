using System.Buffers.Binary;
using ServiceLib.UdpTest.Tester;

namespace ServiceLib.Tests.Services;

public class ProxyCheckTests
{
    [Test]
    public async Task PublicMatrixExcludesStaticLimitsAndKeepsRealUdpProbes()
    {
        var probes = ProxyCheckService.CreateProbes(new() { LocalDnsTarget = "127.0.0.1:1053" }, 10808);
        await probes.Any(p => ProxyCheckPathCapabilities.IsFixedUnsupported(p.Path, p.Kind)).Should().BeFalse();
        foreach (var path in new[] { "socks", "native" })
            foreach (var kind in new[] { "stun", "dns-udp", "h3" })
                await probes.Any(p => p.Path == path && p.Kind == kind).Should().BeTrue();
        await probes.Any(p => p.Path == "http-connect" && p.Kind == "https").Should().BeTrue();
    }

    [Test]
    public async Task DefaultOptionsAndIPv6TargetsAreValid()
    {
        ProxyCheckService.Validate(new());
        var options = new ProxyCheckOptions { LocalDnsTarget = "[::1]:1053", HttpsUrl = "https://[::1]/trace" };
        ProxyCheckService.Validate(options);
        var probes = ProxyCheckService.CreateProbes(options, 10808);
        await probes.First(p => p.Kind == "tcp").Target.Should().BeEqualTo("[::1]:443");
        await probes.Count(p => p.Group == "DNS").Should().BeGreaterThan(10);
    }
    [Test]
    [Arguments("203.0.113.7", "203.0.113.0/24", true)]
    [Arguments("203.0.114.7", "203.0.113.0/24", false)]
    [Arguments("2001:db8::19", "2001:db8::/32", true)]
    [Arguments("2001:db9::19", "2001:db8::/32", false)]
    [Arguments("203.0.113.7", "2001:db8::/32", false)]
    [Arguments("203.0.113.7", "203.0.113.8, 203.0.113.7", true)]
    [Arguments("203.0.113.7", "203.0.113.7/33", false)]
    [Arguments("203.0.113.7", "0.0.0.0/0", true)]
    public async Task EgressMatches(string ip, string expected, bool match)
        => await ProxyCheckService.Matches(ip, expected).Should().BeEqualTo(match);

    [Test]
    public async Task TargetExpectationsOverrideGlobal()
    {
        var options = new ProxyCheckOptions { ExpectedEgress = "203.0.113.1", TargetExpectations = new() { ["example.com"] = "203.0.113.2", ["https://example.com/trace"] = "203.0.113.3" } };
        await ProxyCheckService.ExpectedFor("https://example.com/other", options).Should().BeEqualTo("203.0.113.2");
        await ProxyCheckService.ExpectedFor("https://example.com/trace", options).Should().BeEqualTo("203.0.113.3");
        await ProxyCheckService.ExpectedFor("other.example:3478", options).Should().BeEqualTo("203.0.113.1");
    }

    [Test]
    public async Task StunRejectsMismatchedTransactionAndErrorResponses()
    {
        var service = new StunService(); var request = service.BuildUdpRequestPacket();
        var second = new StunService().BuildUdpRequestPacket();
        await request.AsSpan(8).SequenceEqual(second.AsSpan(8)).Should().BeFalse();
        var packet = new byte[32]; request.CopyTo(packet, 0); packet[0] = 1; packet[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 0x20);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), 8);
        packet[25] = 1;
        byte[] ip = [203, 0, 113, 19];
        for (var i = 0; i < 4; i++) packet[28 + i] = (byte)(ip[i] ^ request[4 + i]);
        await service.VerifyAndExtractUdpResponse(packet).Should().BeTrue();
        await StunService.TryExtractMappedAddress(packet, request.AsSpan(8), out var address).Should().BeTrue();
        await address!.ToString().Should().BeEqualTo("203.0.113.19");
        packet[8] ^= 1; await service.VerifyAndExtractUdpResponse(packet).Should().BeFalse();
        packet[8] ^= 1; packet[1] = 0x11; await service.VerifyAndExtractUdpResponse(packet).Should().BeFalse();
        await service.VerifyAndExtractUdpResponse(packet[..15]).Should().BeFalse();
    }

    [Test]
    public async Task ReportsRoundTripWithoutProxyCredentials()
    {
        var report = new ProxyCheckReport { Results = [new() { Id = "udp", State = "unknown", Connectivity = "passed", Egress = "203.0.113.7", EgressState = "unknown" }] };
        var json = JsonSerializer.Serialize(report, ProxyCheckService.Json);
        var back = JsonSerializer.Deserialize<ProxyCheckReport>(json, ProxyCheckService.Json)!;
        await back.Results[0].State.Should().BeEqualTo("unknown");
        await json.Contains("password", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        await json.Contains("proxy\":", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Test]
    [Arguments("", "203.0.113.7", 3, "unknown")]
    [Arguments("203.0.113.0/24", "203.0.113.7", 3, "passed")]
    [Arguments("203.0.113.8", "203.0.113.7", 3, "failed")]
    [Arguments("203.0.113.0/24", "", 3, "unknown")]
    [Arguments("203.0.113.0/24", "203.0.113.7", 2, "failed")]
    public async Task ReachableIsNotAutomaticallyCorrect(string expected, string observed, int successes, string state)
    {
        var row = new ProxyDiagnosticResult { Expected = expected, Attempts = 3, Successes = successes };
        var exits = observed.Length > 0 ? Enumerable.Repeat(observed, successes).ToList() : new List<string>();
        ProxyCheckService.Evaluate(row, exits);
        await row.State.Should().BeEqualTo(state);
    }
}
