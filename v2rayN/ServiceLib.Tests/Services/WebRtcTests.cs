using System.Security.Cryptography;

namespace ServiceLib.Tests.Services;

public class WebRtcTests
{
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Test]
    public async Task SignedDataChannelEvidenceAndRoutingExpectationsAreRequired()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = ObserverTests.Profile(key);
        var ticket = new ObserverTicket { SessionId = "session", Id = "probe", Nonce = "nonce", Kind = "webrtc", Expires = DateTimeOffset.UtcNow.AddMinutes(3) };
        ObserverReceipt Sign(string protocol)
        {
            var observation = new EgressObservation { Version = 1, ServerId = profile.ServerId, SessionId = ticket.SessionId, ProbeId = ticket.Id, Nonce = ticket.Nonce, Kind = "webrtc", Protocol = protocol, PeerIp = "198.51.100.8", PeerPort = 12345, ObservedAt = DateTimeOffset.UtcNow };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(observation, ProxyCheckService.Json);
            return new() { Payload = Encode(bytes), Signature = Encode(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)), KeyId = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant() };
        }
        var status = new WebRtcProbeStatus { State = "complete", Receipt = Sign("WebRTC/DTLS/SCTP"), Candidates = ["host udp masked.local:1234", "host udp 192.168.1.2:1234"] };
        var options = new ProxyCheckOptions { ExpectedEgress = "198.51.100.9" };
        var result = status.ToResult(profile, ticket, options, 0);
        await result.Connectivity.Should().BeEqualTo("passed");
        await result.EgressState.Should().BeEqualTo("failed");
        await result.State.Should().BeEqualTo("failed");
        await result.Egress.Should().BeEqualTo("198.51.100.8");
        options.TargetExpectations["api.check.example.net:3480|webrtc|browser"] = "198.51.100.0/24";
        await status.ToResult(profile, ticket, options, 0).EgressState.Should().BeEqualTo("passed");
        options.TargetExpectations.Clear(); options.ExpectedEgress = "";
        await status.ToResult(profile, ticket, options, 0).EgressState.Should().BeEqualTo("not-configured");
        status.Receipt = Sign("STUN");
        var rejected = false;
        try { status.ToResult(profile, ticket, options, 0); } catch (IOException) { rejected = true; }
        await rejected.Should().BeTrue();
        status.Receipt = null; rejected = false;
        try { status.ToResult(profile, ticket, options, 0); } catch (IOException) { rejected = true; }
        await rejected.Should().BeTrue();
    }

    [Test]
    public async Task CandidateExposureDoesNotClaimEgressAndPendingWasNotExecuted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var status = new WebRtcProbeStatus { Candidates = ["host udp 192.168.1.2:1234"] };
        var row = status.ToResult(ObserverTests.Profile(key), new(), new(), 0);
        await row.Attempts.Should().BeEqualTo(0);
        await row.Egress.Should().BeEqualTo("");
        await row.EgressState.Should().BeEqualTo("unknown");
        status.State = "unsupported";
        await status.ToResult(ObserverTests.Profile(key), new(), new(), 0).State.Should().BeEqualTo("unsupported");
    }
}
