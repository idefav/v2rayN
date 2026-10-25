using System.Globalization;

namespace ServiceLib.Models.Dto;

public static class ProxyCheckText
{
    public static string T(string zh, string en) => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
    public static string Title => T("代理检查", "Proxy check");
    public static string State(string state) => state switch
    {
        "passed" => T("通过", "Passed"), "failed" => T("失败", "Failed"),
        "unsupported" => T("不支持", "Unsupported"), "unknown" => T("无法判定", "Inconclusive"),
        "cancelled" => T("已取消", "Cancelled"), "running" => T("检查中", "Checking"),
        "skipped" => T("跳过", "Skipped"), _ => T("等待检查", "Pending")
    };
}

public sealed class ProxyCheckOptions
{
	public ObserverProfile? Observer { get; set; }
	[JsonIgnore] public string ObserverCredential { get; set; } = "";
    public string ExpectedEgress { get; set; } = "";
    // Exact hostname or host:port -> comma-separated IP addresses / CIDRs.
    public Dictionary<string, string> TargetExpectations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string HttpsUrl { get; set; } = "https://www.cloudflare.com/cdn-cgi/trace";
    public string HttpUrl { get; set; } = "http://www.cloudflare.com/cdn-cgi/trace";
    public string StunTarget { get; set; } = "stun.cloudflare.com:3478";
    public string DnsTarget { get; set; } = "1.1.1.1:53";
    public string LocalDnsTarget { get; set; } = "";
    public int Samples { get; set; } = 3;
    public int Timeout { get; set; } = 5;
    public int Http3Timeout { get; set; } = 10;
}

public sealed class ProxyDiagnosticResult
{
	public List<ProxyProbeSample> Samples { get; set; } = [];
    public List<string> RelatedProbeIds { get; set; } = [];
	public string ObservationType { get; set; } = "unavailable";
    public string Id { get; set; } = "";
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Target { get; set; } = "";
    public string Family { get; set; } = "";
    public string State { get; set; } = "pending";
    public string Connectivity { get; set; } = "pending";
    public string EgressState { get; set; } = "unknown";
    public string Egress { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Protocol { get; set; } = "";
    public int Attempts { get; set; }
    public int Successes { get; set; }
    public double Elapsed { get; set; }
    public string Evidence { get; set; } = "";
    public string Advice { get; set; } = "";
    public string BaselineComparison { get; set; } = "";
    [JsonIgnore] public bool IsFailed => State == "failed";
    [JsonIgnore] public bool IsPassed => State == "passed";
    [JsonIgnore] public string Label => $"{Group} · {Name} · {Path} · {Family}";
    [JsonIgnore] public string StatusLabel => $"{(IsFailed ? "✕ " : IsPassed ? "✓ " : "")}{ProxyCheckText.T("通信", "Connectivity")}: {ProxyCheckText.State(State is "pending" or "running" ? State : Connectivity)} · {Successes}/{Attempts} · {Elapsed:F0} ms";
    [JsonIgnore] public string ExitLabel => $"{(ObservationType == "recursive-resolver" ? ProxyCheckText.T("递归解析器出口", "Recursive resolver egress") : ProxyCheckText.T("出口", "Egress"))}: {(ObservationType == "not-applicable" ? ProxyCheckText.T("不适用", "Not applicable") : string.IsNullOrEmpty(Egress) ? "—" : Egress)} · {(EgressState == "not-configured" ? ProxyCheckText.T("已观测，未配置预期", "Observed; expectation not configured") : EgressState == "not-applicable" ? ProxyCheckText.T("不适用", "Not applicable") : ProxyCheckText.State(EgressState))}";
    [JsonIgnore] public string Detail => $"{Target}\n{Protocol} · {ProxyCheckText.T("通信", "Connectivity")}: {ProxyCheckText.State(Connectivity)}\n{ProxyCheckText.T("预期出口", "Expected egress")}: {(string.IsNullOrEmpty(Expected) ? "—" : Expected)}\n{Evidence}\n{Advice}\n{BaselineComparison}\n" + string.Join("\n", Samples.SelectMany(s => s.Observations.Select(o => $"{s.Id} · {o.Protocol} · {o.PeerIp}:{o.PeerPort} · {o.ObservedAt:O} · {o.Query} · {o.QueryType}"))) + (RelatedProbeIds.Count > 0 ? "\n" + ProxyCheckText.T("关联流量检查：", "Related flow checks: ") + string.Join(", ", RelatedProbeIds) : "");
}

public sealed class ProxyCheckReport
{
    public int Version { get; set; } = 2;
	public ObserverProfile? Observer { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Environment { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string OptionsFingerprint { get; set; } = "";
    public string RuntimeState { get; set; } = "";
    public string Completion { get; set; } = "pending";
    public List<ProxyDiagnosticResult> Results { get; set; } = [];
}
