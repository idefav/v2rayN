using System.Security.Cryptography;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.Services;

public class ObserverTests
{
    private static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static ObserverProfile Profile(ECDsa key) => new()
    {
        Version = 1, ServerId = "fixture", PublicKey = Encode(key.ExportSubjectPublicKeyInfo()), Domain = "check.example.net", Ipv4 = "203.0.113.10",
        HttpsUrl = "https://api.check.example.net/v1/echo", HttpUrl = "http://http.check.example.net/v1/echo", Http3Url = "https://h3.check.example.net/v1/echo",
        TcpTarget = "tcp.check.example.net:18080", TlsTarget = "tls.check.example.net:18443", UdpTarget = "udp.check.example.net:3479", StunTarget = "stun.check.example.net:3478", DnsTarget = "203.0.113.10:53"
    };

    [Test]
    public async Task SignedReceiptBindsTheActualSample()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var profile = Profile(key); ObserverSession.Validate(profile);
        var ticket = new ObserverTicket { SessionId = "session", Id = "probe", Nonce = "nonce", Kind = "udp", Expires = DateTimeOffset.UtcNow.AddMinutes(3) };
        var observation = new EgressObservation { Version = 1, ServerId = profile.ServerId, SessionId = ticket.SessionId, ProbeId = ticket.Id, Nonce = ticket.Nonce, Kind = "udp", Protocol = "UDP", PeerIp = "198.51.100.8", PeerPort = 12345, ObservedAt = DateTimeOffset.UtcNow };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(observation, ProxyCheckService.Json);
        var receipt = new ObserverReceipt { Payload = Encode(bytes), Signature = Encode(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)), KeyId = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant() };
        await ObserverSession.Verify(profile, ticket, receipt).PeerIp.Should().BeEqualTo("198.51.100.8");
        ticket.Nonce = "another"; bool rejected = false;
        try { ObserverSession.Verify(profile, ticket, receipt); } catch (IOException) { rejected = true; }
        await rejected.Should().BeTrue();
        ticket.Nonce = "nonce"; receipt.Signature = Encode(new byte[64]); rejected = false;
        try { ObserverSession.Verify(profile, ticket, receipt); } catch (IOException) { rejected = true; }
        await rejected.Should().BeTrue();
    }

    [Test]
    public async Task PreciseRulesOverrideHostRulesAndExposeMismatchesWithMissingSamples()
    {
        var o = new ProxyCheckOptions { ExpectedEgress = "198.51.100.1", TargetExpectations = new() { ["example.com"] = "198.51.100.2", ["https://example.com/echo|https|socks"] = "198.51.100.3" } };
        await ProxyCheckService.ExpectedFor("https://example.com/echo", o, "https", "socks").Should().BeEqualTo("198.51.100.3");
        var result = new ProxyDiagnosticResult { Expected = "198.51.100.1", Attempts = 3, Successes = 3 };
        ProxyCheckService.Evaluate(result, ["198.51.100.2"]);
        await result.EgressState.Should().BeEqualTo("failed");
    }

    [Test]
    public async Task ObserverMatrixIncludesAllPathsAndExplicitIPv6Boundary()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var probes = ProxyCheckService.CreateProbes(new() { Observer = Profile(key), LocalDnsTarget = "127.0.0.1:1053" }, 10808);
        foreach (var kind in new[] { "tcp", "tls", "udp", "stun", "h3", "dns-udp", "dns-tcp" })
            await probes.Any(p => p.Kind == kind && p.Path == "socks").Should().BeTrue();
        await probes.Any(p => p.Kind == "uncovered" && p.Family == "6").Should().BeTrue();
        await probes.Any(p => p.Path == "local-dns").Should().BeTrue();
    }

    [Test]
    public async Task CredentialsNeverSerializeAndLegacyBaselineStillLoads()
    {
        var options = new ProxyCheckOptions { ObserverCredential = "a-secret-api-credential-never-export" };
        await JsonSerializer.Serialize(options, ProxyCheckService.Json).Contains(options.ObserverCredential).Should().BeFalse();
        var report = JsonSerializer.Deserialize<ProxyCheckReport>("{\"version\":1,\"results\":[]}", ProxyCheckService.Json)!;
        await report.Version.Should().BeEqualTo(1);
        using var model = new ProxyCheckViewModel(); model.ImportBaseline("{\"version\":1,\"results\":[]}", false);
        await model.BaselineText.Length.Should().BeGreaterThan(0);
    }
}
