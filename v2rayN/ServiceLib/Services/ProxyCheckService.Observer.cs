using System.Net.Http;
using System.Net.Http.Json;
using DiagnosticResult = ServiceLib.Models.Dto.ProxyDiagnosticResult;

namespace ServiceLib.Services;

public sealed partial class ProxyCheckService
{
    private static List<Probe> CreateObserverProbes(ProxyCheckOptions options, int port)
    {
        var o = options.Observer!; var probes = new List<Probe>();
        foreach (var path in new[] { "socks", "native", "http-connect" })
        {
            var proxy = path == "native" ? "" : $"{(path == "socks" ? "socks5" : "http")}://127.0.0.1:{port}";
            foreach (var (kind, target) in new[] { ("tcp", o.TcpTarget), ("tls", o.TlsTarget), ("http", o.HttpUrl), ("https", o.HttpsUrl), ("udp", o.UdpTarget), ("stun", o.StunTarget), ("h3", o.Http3Url) })
                if (!ProxyCheckPathCapabilities.IsFixedUnsupported(path, kind))
                    probes.Add(new($"observer-{path}-{kind}", kind is "udp" or "stun" ? "UDP" : kind == "h3" ? "QUIC/HTTP3" : "TCP/TLS", kind, target, path, proxy, "4"));
            foreach (var kind in new[] { "dns-udp", "dns-tcp" })
                if (!ProxyCheckPathCapabilities.IsFixedUnsupported(path, kind))
                    foreach (var query in new[] { "A", "AAAA", "NXDOMAIN", "TRUNCATED" })
                        probes.Add(new($"observer-{path}-{kind}-{query}", "DNS", kind, o.DnsTarget, path, proxy, "4", query));
        }
        probes.Add(new("system-proxy", T("网络接管", "Network capture"), "system-proxy", o.HttpsUrl, "system"));
        probes.Add(new("observer-system-http", "TCP/TLS", "system-http", o.HttpUrl, "system", "", "4"));
        probes.Add(new("observer-system-https", "TCP/TLS", "system-http", o.HttpsUrl, "system", "", "4"));
        foreach (var query in new[] { "A", "AAAA", "NXDOMAIN" }) probes.Add(new("observer-system-dns-" + query, "DNS", "system-dns", o.Domain, "system", "", query == "AAAA" ? "6" : "4", query));
        var local = string.IsNullOrWhiteSpace(options.LocalDnsTarget) ? DiscoverLocalDns() : options.LocalDnsTarget;
        if (local.Length > 0)
            foreach (var kind in new[] { "dns-udp", "dns-tcp" })
                foreach (var query in new[] { "A", "AAAA", "NXDOMAIN" }) probes.Add(new($"observer-local-{kind}-{query}", "DNS", kind, local, "local-dns", "", "", query));
        else probes.Add(new("local-dns-missing", "DNS", "missing", "local DNS", "local-dns"));
        probes.Add(new("observer-ipv6-uncovered", "IPv6", "uncovered", o.Domain, "native", "", "6"));
        if (ReadSshOutbound() != null) probes.Add(new("ssh-udp-capability", "UDP", "ssh-capability", "SSH upstream", "ssh"));
        return probes;
    }

    private static async Task<DiagnosticResult> CheckObserverAsync(Probe p, ProxyCheckOptions options, ObserverSession session, CancellationToken ct)
    {
        if (p.Kind is "system-proxy" or "ssh-capability" or "missing")
        {
            var configRow = await CheckAsync(p, options, ct);
            configRow.RelatedProbeIds = p.Kind == "system-proxy" ? ["observer-system-http", "observer-system-https"] : p.Kind == "ssh-capability" ? ["observer-socks-udp", "observer-socks-stun", "observer-socks-h3"] : [];
            return configRow;
        }
        var row = NewResult(p, options);
        if (p.Kind == "uncovered") { row.State = row.Connectivity = "skipped"; row.EgressState = "not-applicable"; row.Evidence = T("未覆盖：独立探测服务首版仅提供 IPv4。AAAA 查询不代表 IPv6 传输出口已验证。", "Not covered: this observer is IPv4 only. AAAA queries do not verify IPv6 transport."); return row; }
        if (ProxyCheckPathCapabilities.IsFixedUnsupported(p.Path, p.Kind)) { row.State = row.Connectivity = "unsupported"; row.EgressState = "not-applicable"; row.Evidence = T("HTTP CONNECT 不支持普通 UDP，未回退到其他路径。", "HTTP CONNECT does not support UDP; no fallback."); return row; }
        var recursive = p.Kind == "system-dns" || p.Path == "local-dns";
        row.ObservationType = recursive ? "recursive-resolver" : "direct-peer";
        row.Expected = recursive ? "" : ExpectedFor(p.Target, options, p.Kind, p.Path);
        for (var i = 0; i < options.Samples; i++)
        {
            if (ct.IsCancellationRequested) { row.State = row.Connectivity = "cancelled"; return row; }
            var sample = new ProxyProbeSample { Kind = p.Kind, Path = p.Path, Target = p.Target, Expected = row.Expected };
            row.Samples.Add(sample); row.Attempts++;
            ObserverTicket? ticket = null;
            string transportEgress = "";
            try
            {
                sample.FailureStage = "control-ticket";
                var kind = recursive ? "dns-system" : p.Kind == "system-http" ? new Uri(p.Target).Scheme : p.Kind;
                ticket = await session.IssueAsync(kind, p.Query, ct); sample.Id = ticket.Id;
                sample.FailureStage = "transport";
                WorkerResult result;
                if (p.Kind == "system-http") result = await SystemObserverHttpAsync(p, session.Profile, ticket, options.Timeout, ct);
                else if (p.Kind == "system-dns") result = await SystemProbeAsync(p with { Target = ticket.QueryName.TrimEnd('.') }, options.Timeout, ct);
                else result = await WorkerAsync(p, p.Kind == "h3" ? options.Http3Timeout : options.Timeout, ct, new { profile = session.Profile, ticket });
                sample.Elapsed = result.Elapsed; sample.Connectivity = result.State; sample.Error = result.Error;
                transportEgress = result.Egress;
                sample.Receipts = result.Receipts;
                row.Protocol = result.Protocol;
                if (result.State == "passed") row.Successes++;
                sample.FailureStage = result.State == "passed" ? "observer-evidence" : result.FailureStage.Length > 0 ? result.FailureStage : "transport";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { sample.Connectivity = row.State = row.Connectivity = "cancelled"; return row; }
            catch (Exception ex) { sample.Connectivity = "failed"; sample.Error = ex is OperationCanceledException ? "Probe timed out" : ex.Message; }
            if (ticket != null)
            {
                try
                {
                    // Even a failed return path can have signed inbound evidence.
                    var receipts = await session.FetchAsync(ticket, ct);
                    foreach (var receipt in receipts.Concat(sample.Receipts).DistinctBy(r => r.Signature))
                    {
                        var observed = ObserverSession.Verify(session.Profile, ticket, receipt);
                        if (!ObservationMatches(p, ticket, observed)) throw new IOException("Observed protocol/query does not match this probe");
                        if (p.Kind == "stun" && sample.Connectivity == "passed" && observed.PeerIp != transportEgress) throw new IOException("STUN mapped address differs from the signed observation");
                        sample.Observations.Add(observed);
                    }
                    sample.Receipts = receipts.Concat(sample.Receipts).DistinctBy(r => r.Signature).ToList();
                    if (sample.Connectivity == "passed" && sample.Observations.Count == 0 && !recursive) { row.Successes--; sample.Connectivity = "failed"; sample.Error = "Protocol succeeded without signed egress evidence"; }
                    if (sample.Observations.Count > 0 && sample.Connectivity == "passed") sample.FailureStage = "";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { sample.Connectivity = row.State = row.Connectivity = "cancelled"; return row; }
                catch (Exception ex) { sample.Observations.Clear(); sample.Connectivity = "failed"; sample.FailureStage = "observer-evidence"; sample.Error = ex is OperationCanceledException ? "Observer control timed out" : ex.Message; }
            }
            sample.Comparison = recursive ? "not-applicable" : row.Expected.Length == 0 ? "not-configured"
                : sample.Observations.Any(o => !Matches(o.PeerIp, row.Expected)) ? "failed"
                : sample.Observations.Count > 0 ? "passed" : "unknown";
        }
        row.Elapsed = row.Samples.Average(s => s.Elapsed);
        row.Successes = row.Samples.Count(s => s.Connectivity == "passed");
        row.Connectivity = row.Successes == row.Attempts ? "passed" : "failed";
        row.Egress = string.Join(", ", row.Samples.SelectMany(s => s.Observations).Select(o => o.PeerIp).Distinct().Order());
        row.EgressState = recursive ? "not-applicable" : row.Samples.Any(s => s.Comparison == "failed") ? "failed"
            : row.Samples.Any(s => s.Observations.Count == 0) ? "unknown" : row.Expected.Length == 0 ? "not-configured" : "passed";
        row.State = row.Connectivity == "failed" || row.EgressState == "failed" ? "failed"
            : row.EgressState == "passed" || recursive && row.Egress.Length > 0 ? "passed" : "unknown";
        row.Evidence = string.Join("\n", row.Samples.Select(s => $"{s.Id}: {s.Connectivity}; {s.FailureStage}; {s.Error}"));
        row.Advice = recursive ? T("以上地址仅代表到达权威服务的递归解析器。客户端访问现有 DNS 的出口未取得接收端证据；本地合成/FakeIP 可能不触发权威查询。", "Addresses identify recursive resolvers reaching the authority. Client-to-resolver egress is unobserved; synthetic/FakeIP answers may not reach the authority.")
            : row.Connectivity == "failed" ? T("超时不代表泄漏；若已有签名观测，表示请求到达但完整协议或回程尚未通过。", "Timeout does not establish leakage. Signed observations can prove arrival even when protocol completion or the return path fails.") : "";
        return row;
    }

    internal static bool ObservationMatches(Probe probe, ObserverTicket ticket, EgressObservation observation)
    {
        if (ticket.Kind.StartsWith("dns-"))
        {
            if (observation.Query != ticket.QueryName || observation.Protocol is not ("DNS/UDP" or "DNS/TCP")) return false;
            // Recursive resolvers can change transport and issue additional question types.
            if (ticket.Kind == "dns-system") return true;
            var expectedType = ticket.Query == "AAAA" ? "TypeAAAA" : "TypeA";
            if (observation.QueryType != expectedType) return false;
            return ticket.Kind == "dns-tcp" ? observation.Protocol == "DNS/TCP"
                : observation.Protocol == "DNS/UDP" || ticket.Query == "TRUNCATED";
        }
        return ticket.Kind switch { "h3" => observation.Protocol == "HTTP/3.0" && observation.Alpn == "h3", "udp" => observation.Protocol == "UDP", "stun" => observation.Protocol == "STUN/UDP", "tcp" => observation.Protocol == "TCP", "tls" => observation.Protocol == "TLS", _ => observation.Protocol.StartsWith("HTTP/") && !observation.Protocol.StartsWith("HTTP/3") };
    }

    private static async Task<WorkerResult> SystemObserverHttpAsync(Probe probe, ObserverProfile profile, ObserverTicket ticket, int timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = true, Proxy = HttpClient.DefaultProxy, AllowAutoRedirect = false }) { MaxResponseContentBufferSize = 16384 };
        using var request = new HttpRequestMessage(HttpMethod.Get, probe.Target);
        request.Headers.Add("X-Probe-ID", ticket.Id); request.Headers.Add("X-Probe-Nonce", ticket.Nonce);
        var watch = Stopwatch.StartNew(); using var response = await client.SendAsync(request, deadline.Token); response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<ObserverReceipt>(Json, deadline.Token) ?? throw new IOException("Missing receipt");
        var observation = ObserverSession.Verify(profile, ticket, receipt);
        return new WorkerResult { Version = 2, State = "passed", Egress = observation.PeerIp, Elapsed = watch.Elapsed.TotalMilliseconds, Protocol = "HTTP/" + response.Version, Receipts = [receipt] };
    }
}
