namespace ServiceLib.Services;

internal sealed class WebRtcProbeStatus
{
    public string State { get; set; } = "pending";
    public string[] Candidates { get; set; } = [];
    public string Browser { get; set; } = "";
    public string Error { get; set; } = "";
    public double Elapsed { get; set; }
    public ObserverReceipt? Receipt { get; set; }

    internal ProxyDiagnosticResult ToResult(ObserverProfile profile, ObserverTicket ticket, ProxyCheckOptions options, int index)
    {
        var target = new Uri(profile.HttpsUrl).Host + ":3480";
        var row = new ProxyDiagnosticResult
        {
            Id = $"webrtc-browser-{index}", Group = "WebRTC", Name = $"DataChannel #{index + 1}", Path = "browser",
            Target = target, Family = "IPv4", Protocol = "WebRTC/DTLS/SCTP", Attempts = State == "pending" ? 0 : 1,
            State = State == "unsupported" ? "unsupported" : "failed", Connectivity = State == "unsupported" ? "unsupported" : "failed",
            Expected = ProxyCheckService.ExpectedFor(target, options, "webrtc", "browser"),
            ExpectedCountryCodes = options.ExpectedCountryCodes.Select(c => ProxyCheckCountries.Normalize(c)!).Distinct().ToList(),
            Elapsed = double.IsFinite(Elapsed) ? Math.Clamp(Elapsed, 0, 180000) : 0,
            Evidence = ProxyCheckText.T("浏览器自报（非签名证据）：", "Browser claims (not signed evidence): ") + Browser + "\n" + Error + "\n" + string.Join("\n", Candidates.Take(64).Select(c => c.Length > 256 ? c[..256] : c)),
            Advice = ProxyCheckText.T("仅验证本次浏览器数据通道。host 候选不等于公网泄漏；.local 是 mDNS 隐私地址。未覆盖 TURN、IPv6 传输、媒体与浏览器 DoH。", "Only this browser data channel is tested. Host candidates alone do not prove public leakage; .local masks addresses with mDNS. TURN, IPv6 transport, media and browser DoH are not covered.")
        };
        if (State != "complete") return row;
        if (Receipt == null) throw new IOException("WebRTC completion lacks signed receipt");
        var observation = ObserverSession.Verify(profile, ticket, Receipt);
        if (observation.Protocol != "WebRTC/DTLS/SCTP") throw new IOException("Incorrect WebRTC evidence protocol");
        row.ObservationType = "signed-peer"; row.Egress = observation.PeerIp;
        row.Connectivity = "passed"; row.Successes = row.Attempts = 1;
        row.EgressState = row.Expected.Length == 0 ? "not-configured" : ProxyCheckService.Matches(row.Egress, row.Expected) ? "passed" : "failed";
        row.State = row.EgressState == "failed" ? "failed" : "passed";
        row.Samples.Add(new() { Id = ticket.Id, Path = "browser", Target = target, Kind = "webrtc", Connectivity = "passed", Observations = [observation], Receipts = [Receipt], Expected = row.Expected, Comparison = row.EgressState });
        return row;
    }
}
