namespace ServiceLib.Models.Dto;

// Presentation-only wrappers. Reports retain the original diagnostic model.
public sealed class ProxyCheckResultRow(ProxyDiagnosticResult source)
{
    public ProxyDiagnosticResult Source { get; } = source;
    private static string T(string zh, string en) => ProxyCheckText.T(zh, en);
    public string Name => Source.Name;
    public string Target => Source.Target;
    public string Path => Source.Path;
    public string Family => Source.Family;
    public string Detail => Source.Detail + (Source.EgressLocations.Count == 0 ? "" : "\n" + LocationHint)
        + "\n" + ProxyCheckText.T("本轮预期地域（独立比较）：", "Run region expectation (independent): ")
        + (Source.ExpectedCountryCodes is { Count: > 0 } ? string.Join(" / ", Source.ExpectedCountryCodes.Select(ProxyCheckCountries.Name)) : T("未配置", "Not configured"))
        + " · " + ProxyCheckCountries.Label(Source.RegionComparison, Source.ObservationType == "recursive-resolver");
    public IReadOnlyList<ProxyCheckExitAddress> ExitAddresses => HasLocation
        ? Source.Egress.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct()
            .Select(ip => new ProxyCheckExitAddress(Source, ip)).ToArray() : [];
    public bool HasLocation => Source.ObservationType != "not-applicable" && Source.Egress.Length > 0;
    public string LocationLabel => Source.EgressLocations.Count > 0
        ? string.Join("\n", Source.Egress.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct()
            .Select(ip => (Source.Egress.Contains(',') ? ip + ": " : "")
                + (Source.EgressLocations.FirstOrDefault(l => SameAddress(l.Ip, ip))?.Label ?? T("地域未知", "Location unavailable"))))
        : Source.LocationState == "loading" ? T("地域查询中…", "Looking up location…") : T("地域未知", "Location unavailable");
    public string LocationHint => T("IP 地域为第三方数据库信息，仅供参考；不属于服务端签名证据。", "IP location is approximate third-party metadata, separate from signed server evidence.")
        + string.Concat(Source.EgressLocations.Select(l => $"\n{l.Ip} · {l.Label} · {l.Provider} · {l.CheckedAt.ToLocalTime():g}"));
    private static bool SameAddress(string first, string second) => IPAddress.TryParse(first, out var a) && IPAddress.TryParse(second, out var b)
        && (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(b.IsIPv4MappedToIPv6 ? b.MapToIPv4() : b);
    public string Conclusion => (Source.State switch { "passed" => "✓ ", "failed" => "× ", "unknown" => "? ", "unsupported" => "⊘ ", "skipped" => "— ", _ => "" }) + ProxyCheckText.State(Source.State);
    public bool HasExpectation => !string.IsNullOrWhiteSpace(Source.Expected);
    public string ExpectedLabel => HasExpectation ? T("预期：", "Expected: ") + Source.Expected : "";
    public bool IsExpanded { get; set; }
    public bool IsFailed => Source.State == "failed";
    public bool IsPassed => Source.State == "passed" && Source.Connectivity == "passed" && Source.EgressState == "passed";
    public bool IsFinished => IsCompleted(Source);
    internal static bool IsCompleted(ProxyDiagnosticResult result) => result.State is "passed" or "failed" or "unknown" or "unsupported" or "skipped";
    public bool IsCommunicationPassed => Source.ObservationType != "not-applicable" && Source.Connectivity == "passed";
    public bool IsCommunicationFailed => Source.ObservationType != "not-applicable" && Source.Connectivity == "failed";
    public bool IsRunning => Source.State == "running";
    public bool IsUncovered => Source.State is "unsupported" or "skipped";
    public bool NeedsAttention => IsFailed || Source.State == "cancelled" || Source.State == "unknown" && Source.EgressState != "not-configured";
    public bool HasComparisonFailure => Source.EgressState == "failed";
    public bool HasComparisonMatch => Source.EgressState == "passed";
    public string Communication => Source.State is "running" or "pending" ? ProxyCheckText.State(Source.State)
        : Source.ObservationType != "not-applicable" ? ProxyCheckText.State(Source.Connectivity)
        : Source.State == "passed" ? T("已核对（非通信）", "Checked (not traffic)")
        : ProxyCheckText.State(Source.Connectivity) + T("（能力检查）", " (capability check)");
    public string Sampling => Source.Attempts > 0 ? $"{Source.Successes}/{Source.Attempts}" : "—";
    public string Elapsed => Source.Attempts > 0 && Source.State is not ("pending" or "running") ? $"{Source.Elapsed:F0} ms" : "—";
    public string Address => Source.ObservationType == "not-applicable" || Source.EgressState == "not-applicable" && Source.Egress.Length == 0 ? T("不适用", "Not applicable") : Source.Egress.Length > 0 ? Source.Egress : "—";
    public string EvidenceKind => Source.ObservationType == "recursive-resolver" ? T("递归解析器出口", "Resolver egress")
        : Source.ObservationType == "not-applicable" ? T("配置 / 能力检查", "Configuration / capability")
        : Source.Samples.Any(s => s.Observations.Count > 0) ? T("服务端签名观测", "Signed server observation")
        : Source.Egress.Length > 0 ? T("端点观测", "Endpoint observation") : T("暂无出口证据", "No egress evidence");
    public string Comparison => Source.EgressState switch
    {
        "passed" => T("符合预期", "Matches expected"), "failed" => T("不符合预期", "Unexpected egress"),
        "not-configured" => T("已观测 · 未配置预期", "Observed · no expectation"),
        "not-applicable" => T("不适用", "Not applicable"),
        _ when IsUncovered => T("未覆盖", "Not covered"),
        _ when Source.State is "pending" or "running" => T("等待观测", "Awaiting observation"),
        _ => T("证据不足", "Insufficient evidence")
    };
}

public sealed class ProxyCheckResultGroup(string name, IReadOnlyList<ProxyCheckResultRow> rows) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isExpanded;
    private IReadOnlyList<ProxyCheckResultRow> _summaryRows = rows;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); }
    }
    public string Name { get; } = name;
    public ObservableCollection<ProxyCheckResultRow> Rows { get; } = new(rows);
    public string CountLabel => Rows.Count == _summaryRows.Count ? $"{Rows.Count}" : $"{Rows.Count}/{_summaryRows.Count} " + ProxyCheckText.T("匹配", "matching");
    public int AttentionCount => _summaryRows.Count(r => r.NeedsAttention);
    public bool HasAttention => AttentionCount > 0;
    public string AttentionLabel => ProxyCheckText.T("需关注 ", "Needs attention ") + AttentionCount;
    public string Summary => $"{_summaryRows.Count(r => r.IsFinished)}/{_summaryRows.Count} " + ProxyCheckText.T("项已完成", "completed")
        + (_summaryRows.Any(r => r.Source.State == "cancelled") ? $" · {_summaryRows.Count(r => r.Source.State == "cancelled")} " + ProxyCheckText.T("项已取消", "cancelled") : "");
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void NotifySummaryChanged(IReadOnlyList<ProxyCheckResultRow>? allRows = null)
    {
        _summaryRows = allRows ?? Rows;
        PropertyChanged?.Invoke(this, new(nameof(CountLabel)));
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
        PropertyChanged?.Invoke(this, new(nameof(AttentionCount)));
        PropertyChanged?.Invoke(this, new(nameof(HasAttention)));
        PropertyChanged?.Invoke(this, new(nameof(AttentionLabel)));
    }
}
