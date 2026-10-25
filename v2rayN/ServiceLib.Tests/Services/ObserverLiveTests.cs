using System.Net.Http;

namespace ServiceLib.Tests.Services;

// Explicit live acceptance harness. Pairing input is stdin; private material goes only to the OS store.
public class ObserverLiveTests
{
    [Test]
    public async Task PairRestoreProbeAndRevokeWhenRequested()
    {
        var mode = Environment.GetEnvironmentVariable("V2RAYN_OBSERVER_LIVE_MODE");
        if (mode is not ("pair" or "resume" or "revoke" or "webrtc" or "pair-webrtc")) return;
        var directory = Environment.GetEnvironmentVariable("V2RAYN_OBSERVER_LIVE_DIRECTORY") ?? throw new InvalidOperationException("Live artifact directory required");
        var domain = Environment.GetEnvironmentVariable("V2RAYN_OBSERVER_LIVE_DOMAIN") ?? throw new InvalidOperationException("Live domain required");
        var service = new ObserverPairingService(new ObserverSecretStore(), Path.Combine(directory, "device-metadata.json"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8)); var ct = deadline.Token;
        if (mode is "pair" or "pair-webrtc")
        {
            var code = (await Console.In.ReadToEndAsync(ct)).Trim();
            await service.PairAsync(domain, code, "v2rayN macOS acceptance", ct);
        }
        using var identity = await service.OpenAsync(ct);
        using var client = identity.CreateClient();
        using var capabilities = await client.GetAsync("/v2/capabilities", ct);
        capabilities.EnsureSuccessStatusCode();
        if (mode == "revoke")
        {
            await service.UnpairAsync(false, ct);
            using var revoked = await client.GetAsync("/v2/capabilities", ct);
            await revoked.StatusCode.Should().BeEqualTo(HttpStatusCode.Unauthorized);
            Console.WriteLine("Live device revoked; existing authenticated client rejected; local credentials removed.");
            return;
        }
        await using var session = await ObserverSession.OpenAsync(new() { Observer = identity.Connection.Profile }, ct, service);
        var profile = session.Profile;
        if (mode is "webrtc" or "pair-webrtc")
        {
            var ticket = await session.IssueAsync("webrtc", "", ct);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { tickets = new[] { ticket }, profile.PublicKey, profile.ServerId }, ProxyCheckService.Json);
            var fragment = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var link = "https://api." + profile.Domain + "/webrtc#" + fragment;
            // Short-lived browser ticket only; never print device keys or a pairing string.
            using (var stdout = new StreamWriter(Console.OpenStandardOutput(), leaveOpen: true)) { await stdout.WriteLineAsync(link); await stdout.FlushAsync(ct); }
            WebRtcProbeStatus? status = null;
            for (var attempt = 0; attempt < 80; attempt++)
            {
                status = await session.WebRtcStatusAsync(ticket, ct);
                if (status.State is "complete" or "failed") break;
                await Task.Delay(2000, ct);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "webrtc-results.json"), JsonSerializer.Serialize(status, ProxyCheckService.Json), ct);
            await status!.State.Should().BeEqualTo("complete");
            var observation = ObserverSession.Verify(profile, ticket, status.Receipt!);
            await observation.Protocol.Should().BeEqualTo("WebRTC/DTLS/SCTP");
            Console.WriteLine("Signed browser WebRTC egress: " + observation.PeerIp);
            return;
        }
        var worker = Environment.GetEnvironmentVariable("V2RAYN_OBSERVER_LIVE_WORKER") ?? throw new InvalidOperationException("Worker required");
        var cases = new List<(string Kind, string Query, string Target)> {
            ("tcp", "", profile.TcpTarget), ("tls", "", profile.TlsTarget), ("http", "", profile.HttpUrl),
            ("https", "", profile.HttpsUrl), ("h3", "", profile.Http3Url), ("udp", "", profile.UdpTarget), ("stun", "", profile.StunTarget) };
        foreach (var kind in new[] { "dns-udp", "dns-tcp" }) foreach (var query in new[] { "A", "AAAA", "NXDOMAIN", "TRUNCATED" }) cases.Add((kind, query, profile.DnsTarget));
        var rows = new List<object>(); var failures = 0;
        foreach (var (kind, query, target) in cases)
        {
            var ticket = await session.IssueAsync(kind, query, ct);
            using var process = new Process { StartInfo = new ProcessStartInfo(worker) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            process.Start();
            using var killed = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var output = process.StandardOutput.ReadToEndAsync(ct); var errors = process.StandardError.ReadToEndAsync(ct);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { version = 2, kind, target, query, family = "4", proxy = "socks5://127.0.0.1:10808", timeout = 30, observer = new { profile, ticket } }, ProxyCheckService.Json));
            process.StandardInput.Close(); await process.WaitForExitAsync(ct);
            using var result = JsonDocument.Parse(await output); _ = await errors;
            var state = result.RootElement.GetProperty("state").GetString();
            var receipts = await session.FetchAsync(ticket, ct);
            var observations = receipts.Select(receipt => ObserverSession.Verify(profile, ticket, receipt)).ToArray();
            var passed = state == "passed" && observations.Length > 0;
            if (kind == "h3") passed &= observations.All(o => o.Protocol == "HTTP/3.0" && o.Alpn == "h3");
            var expected = Environment.GetEnvironmentVariable("V2RAYN_OBSERVER_LIVE_EXPECTED");
            if (!string.IsNullOrWhiteSpace(expected)) passed &= observations.All(o => o.PeerIp == expected);
            if (!passed) failures++;
            rows.Add(new { kind, query, state, observations, receipts });
            Console.WriteLine($"Live {kind} {query}: {state}; observed {string.Join(",", observations.Select(o => o.PeerIp).Distinct())}");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, mode + "-results.json"), JsonSerializer.Serialize(rows, ProxyCheckService.Json), ct);
        await failures.Should().BeEqualTo(0);
    }
}
