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
    [JsonIgnore] public string StatusLabel => $"{(IsFailed ? "✕ " : IsPassed ? "✓ " : "")}{ProxyCheckText.State(State)} · {Successes}/{Attempts} · {Elapsed:F0} ms";
    [JsonIgnore] public string ExitLabel => $"{ProxyCheckText.T("出口", "Egress")}: {(string.IsNullOrEmpty(Egress) ? "—" : Egress)} · {ProxyCheckText.State(EgressState)}";
    [JsonIgnore] public string Detail => $"{Target}\n{Protocol} · {ProxyCheckText.T("通信", "Connectivity")}: {ProxyCheckText.State(Connectivity)}\n{ProxyCheckText.T("预期出口", "Expected egress")}: {(string.IsNullOrEmpty(Expected) ? "—" : Expected)}\n{Evidence}\n{Advice}\n{BaselineComparison}";
}

public sealed class ProxyCheckReport
{
    public int Version { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Environment { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string OptionsFingerprint { get; set; } = "";
    public string RuntimeState { get; set; } = "";
    public string Completion { get; set; } = "pending";
    public List<ProxyDiagnosticResult> Results { get; set; } = [];
}
