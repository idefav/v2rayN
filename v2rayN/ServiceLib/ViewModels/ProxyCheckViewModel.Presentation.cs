using System.Collections.Specialized;

namespace ServiceLib.ViewModels;

public partial class ProxyCheckViewModel
{
    private IDisposable? _presentation;
    private IDisposable? _observerSettings;
    private readonly HashSet<string> _expandedRows = [];
    private readonly Dictionary<string, ProxyCheckResultRow> _rowCache = [];
    [Reactive] public partial string SearchText { get; set; } = "";
    [Reactive] public partial int SelectedFilter { get; set; }
    [Reactive] public partial string LastChecked { get; set; } = ProxyCheckText.T("尚未运行", "Not run yet");
    [Reactive] public partial string NodeName { get; set; } = "—";
    [Reactive] public partial string CoreName { get; set; } = "—";
    [Reactive] public partial string RoutingName { get; set; } = "—";
    [Reactive] public partial string CaptureMode { get; set; } = "—";
    [Reactive] public partial string LocalEntry { get; set; } = "—";
    public IReadOnlyList<string> ResultFilters { get; } = [T("全部结果", "All results"), T("需关注", "Needs attention"), T("通过", "Passed"), T("未覆盖", "Not covered")];
    public ObservableCollection<ProxyCheckResultGroup> ResultGroups { get; } = [];
    public int ReachableCount => Results.Count(r => r.Connectivity == "passed" && r.ObservationType != "not-applicable");
    public int MatchedCount => Results.Count(r => r.EgressState == "passed");
    public int AttentionCount => Results.Count(r => new ProxyCheckResultRow(r).NeedsAttention);
    public int UncoveredCount => Results.Count(r => r.State is "unsupported" or "skipped");
    public int FinishedCount => Results.Count(ProxyCheckResultRow.IsCompleted);
    public int CancelledCount => Results.Count(r => r.State == "cancelled");
    public int TotalCount => Results.Count;
    public double CompletionPercent => TotalCount == 0 ? 0 : 100.0 * FinishedCount / TotalCount;
    public bool HasResults => TotalCount > 0;
    public bool IsBusy => IsRunning || IsTestingObserver;
    public bool HasVisibleResults => ResultGroups.Count > 0;
    public string ProgressLabel => $"{FinishedCount}/{TotalCount} " + T("项已完成", "checks completed")
        + (CancelledCount > 0 ? $" · {CancelledCount} " + T("项已取消", "cancelled") : "");
    public string VisibleCountLabel => ResultGroups.Sum(g => g.Rows.Count) + " / " + TotalCount;
    public string CheckStatus => IsTestingObserver ? T("正在验证观察端", "Testing observer") : IsRunning ? T("正在检查", "Checking") : !HasResults ? T("准备就绪", "Ready")
        : Results.Any(r => r.State == "cancelled") ? T("检查已取消", "Cancelled") : AttentionCount > 0 ? T("有项目需关注", "Needs attention") : T("检查完成", "Completed");
    public string MetricReachable => HasResults ? ReachableCount.ToString() : "—";
    public string MetricMatched => HasResults ? MatchedCount.ToString() : "—";
    public string MetricAttention => HasResults ? AttentionCount.ToString() : "—";
    public string MetricUncovered => HasResults ? UncoveredCount.ToString() : "—";
    public string ObserverHost
    {
        get
        {
            if (!UseObserver) return T("公共端点模式", "Public endpoints");
            try
            {
                var profile = JsonSerializer.Deserialize<ObserverProfile>(ObserverProfileJson, ProxyCheckService.Json);
                return profile == null ? T("尚未导入配置", "Profile not imported")
                    : string.IsNullOrWhiteSpace(profile.Domain) ? T("配置待完善", "Configuration incomplete") : profile.Domain;
            }
            catch (JsonException) { return T("配置待完善", "Configuration incomplete"); }
        }
    }
    public string ObserverCoverage => UseObserver ? T("IPv4 · IPv6 未覆盖", "IPv4 · IPv6 not covered") : T("系统路径与本地入口分别检查", "System paths and local ingress checked separately");
    public string ObserverConnectionLabel => !UseObserver ? T("未启用", "Disabled") : ObserverConnectionState switch
    {
        "testing" => T("验证中", "Testing"), "passed" => T("控制接口已验证", "Control verified"),
        "failed" => T("连接失败", "Connection failed"), "invalid" => T("配置无效", "Invalid configuration"), _ => T("尚未验证", "Not verified")
    };
    internal static string FormatCaptureMode(bool tun, ESysProxyType proxy)
    {
        var mode = proxy switch
        {
            ESysProxyType.ForcedChange => T("系统代理：自动配置", "System proxy: automatic"),
            ESysProxyType.ForcedClear => T("系统代理：自动清除", "System proxy: clear"),
            ESysProxyType.Unchanged => T("系统代理：保持现状", "System proxy: unchanged"),
            ESysProxyType.Pac => "PAC", _ => T("系统代理：未知", "System proxy: unknown")
        };
        return $"TUN {(tun ? T("开启", "on") : T("关闭", "off"))} · {mode}";
    }

    private void InitializePresentation()
    {
        Results.CollectionChanged += ResultsChanged;
        _presentation = this.WhenAnyValue(x => x.SearchText, x => x.SelectedFilter, x => x.IsRunning, x => x.UseObserver, x => x.ObserverProfileJson, x => x.IsTestingObserver, x => x.ObserverConnectionState)
            .Subscribe(_ => RefreshPresentation());
        _observerSettings = this.WhenAnyValue(x => x.UseObserver, x => x.ObserverProfileJson, x => x.ObserverCredential).Skip(1).Subscribe(_ =>
        {
            _observerSettingsVersion++;
            ObserverConnectionState = "unverified";
            ObserverStatus = UseObserver ? T("配置或凭证已更改，请重新测试控制接口。", "Settings or credential changed. Test the control connection again.") : "";
            _observerCancel?.Cancel();
        });
        RefreshPresentation();
    }

    private void ResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshPresentation();

    public void RefreshPresentation()
    {
        foreach (var row in ResultGroups.SelectMany(g => g.Rows))
            if (row.IsExpanded) _expandedRows.Add(row.Source.Id); else _expandedRows.Remove(row.Source.Id);
        var ids = Results.Select(r => r.Id).ToHashSet();
        foreach (var id in _rowCache.Keys.Where(id => !ids.Contains(id)).ToArray()) _rowCache.Remove(id);
        var rows = Results.Select(r =>
        {
            if (!_rowCache.TryGetValue(r.Id, out var row) || !ReferenceEquals(row.Source, r))
                _rowCache[r.Id] = row = new ProxyCheckResultRow(r) { IsExpanded = _expandedRows.Contains(r.Id) };
            return row;
        });
        rows = SelectedFilter switch { 1 => rows.Where(r => r.NeedsAttention), 2 => rows.Where(r => r.IsPassed), 3 => rows.Where(r => r.IsUncovered), _ => rows };
        if (!string.IsNullOrWhiteSpace(SearchText))
            rows = rows.Where(r => string.Join(' ', r.Name, r.Target, r.Path, r.Source.Egress, r.Source.Group, r.Source.Protocol).Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase));
        var groups = rows.GroupBy(r => r.Source.Group).OrderBy(g => g.Key switch { "TCP/TLS" => 1, "DNS" => 2, "UDP" => 3, "QUIC/HTTP3" => 4, "IPv4" => 5, "IPv6" => 6, _ => 0 }).ToArray();
        var desired = new List<ProxyCheckResultGroup>();
        foreach (var group in groups)
        {
            var existing = ResultGroups.FirstOrDefault(g => g.Name == group.Key) ?? new(group.Key, []);
            Synchronize(existing.Rows, group.ToArray()); existing.NotifySummaryChanged(); desired.Add(existing);
        }
        Synchronize(ResultGroups, desired);
        foreach (var property in new[] { nameof(ResultGroups), nameof(ReachableCount), nameof(MatchedCount), nameof(AttentionCount), nameof(UncoveredCount), nameof(FinishedCount), nameof(CancelledCount), nameof(TotalCount), nameof(CompletionPercent), nameof(HasResults), nameof(IsBusy), nameof(HasVisibleResults), nameof(ProgressLabel), nameof(VisibleCountLabel), nameof(CheckStatus), nameof(MetricReachable), nameof(MetricMatched), nameof(MetricAttention), nameof(MetricUncovered), nameof(ObserverHost), nameof(ObserverCoverage), nameof(ObserverConnectionLabel) })
            this.RaisePropertyChanged(property);
    }

    private static void Synchronize<T>(ObservableCollection<T> collection, IReadOnlyList<T> desired) where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < collection.Count && ReferenceEquals(collection[i], desired[i])) continue;
            var existing = collection.IndexOf(desired[i]);
            if (existing >= 0) collection.Move(existing, i);
            else if (i < collection.Count && !desired.Contains(collection[i])) collection[i] = desired[i];
            else collection.Insert(i, desired[i]);
        }
        while (collection.Count > desired.Count) collection.RemoveAt(collection.Count - 1);
    }
}
