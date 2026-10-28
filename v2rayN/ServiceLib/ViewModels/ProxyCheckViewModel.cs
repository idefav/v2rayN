using DiagnosticResult = ServiceLib.Models.Dto.ProxyDiagnosticResult;

namespace ServiceLib.ViewModels;

public partial class ProxyCheckViewModel : MyReactiveObject, IDisposable
{
    private CancellationTokenSource? _cancel;
    private CancellationTokenSource? _observerCancel;
    private readonly Func<ProxyCheckOptions, CancellationToken, Task<IAsyncDisposable>> _connectObserver;
    private bool _disposed;
    private bool _starting;
    private int _observerSettingsVersion;
    private readonly IDisposable _exit;
    private ProxyCheckReport? _report;
    private ProxyCheckReport? _baseline;
    private int _generation;
    [Reactive] public partial bool UseObserver { get; set; }
    [Reactive] public partial string ObserverProfileJson { get; set; } = "";
    [Reactive] public partial string ObserverStatus { get; set; } = "";
    [Reactive] public partial bool IsTestingObserver { get; set; }
    [Reactive] public partial string ObserverConnectionState { get; set; } = "unverified";
    public ObservableCollection<DiagnosticResult> Results { get; } = [];
    [Reactive] public partial bool IsRunning { get; set; }
    [Reactive] public partial bool CanStart { get; set; } = true;
    [Reactive] public partial bool CanExport { get; set; }
    [Reactive] public partial bool CanRetry { get; set; }
    [Reactive] public partial string EnvironmentText { get; set; } = "";
    [Reactive] public partial string Summary { get; set; } = "";
    [Reactive] public partial string BaselineText { get; set; } = "";
    [Reactive] public partial string ExpectedEgress { get; set; } = "";
    [Reactive] public partial string TargetRules { get; set; } = "";
    [Reactive] public partial string HttpsUrl { get; set; } = "https://www.cloudflare.com/cdn-cgi/trace";
    [Reactive] public partial string HttpUrl { get; set; } = "http://www.cloudflare.com/cdn-cgi/trace";
    [Reactive] public partial string StunTarget { get; set; } = "stun.cloudflare.com:3478";
    [Reactive] public partial string DnsTarget { get; set; } = "1.1.1.1:53";
    [Reactive] public partial string LocalDnsTarget { get; set; } = "";
    [Reactive] public partial string Samples { get; set; } = "3";
    [Reactive] public partial string Timeout { get; set; } = "5";
    [Reactive] public partial string Http3Timeout { get; set; } = "10";
    private static string T(string zh, string en) => ProxyCheckText.T(zh, en);

    public ProxyCheckViewModel() : this(async (options, ct) => await ObserverSession.OpenAsync(options, ct))
    {
        try { if (ObserverPairingService.Instance.Metadata() is { } metadata) ApplyPairedConnection(metadata); }
        catch { ObserverStatus = T("配对配置无效，请重新配对。", "Invalid pairing metadata; pair again."); }
    }

    internal ProxyCheckViewModel(Func<ProxyCheckOptions, CancellationToken, Task<IAsyncDisposable>> connectObserver)
    {
        _connectObserver = connectObserver;
        InitializeCountrySelection();
        InitializePresentation();
        _exit = AppEvents.AppExitRequested.AsObservable().Subscribe(_ => Cancel());
        try
        {
            var path = Utils.GetConfigPath("proxy-check-options.json");
            if (File.Exists(path))
            {
                var o = JsonSerializer.Deserialize<ProxyCheckOptions>(File.ReadAllText(path), ProxyCheckService.Json);
                if (o != null)
                {
                    ProxyCheckService.Validate(o);
                    UseObserver = o.Observer != null;
                    ObserverProfileJson = o.Observer == null ? "" : JsonSerializer.Serialize(o.Observer, ProxyCheckService.Json);
                    ExpectedEgress = o.ExpectedEgress; HttpsUrl = o.HttpsUrl; HttpUrl = o.HttpUrl; StunTarget = o.StunTarget;
                    foreach (var choice in CountryChoices) choice.IsSelected = o.ExpectedCountryCodes.Contains(choice.Code, StringComparer.OrdinalIgnoreCase);
                    DnsTarget = o.DnsTarget; LocalDnsTarget = o.LocalDnsTarget;
                    Samples = o.Samples.ToString(); Timeout = o.Timeout.ToString(); Http3Timeout = o.Http3Timeout.ToString();
                    TargetRules = string.Join("\n", o.TargetExpectations.Select(p => p.Key + "=" + p.Value));
                }
            }
            var baseline = Utils.GetConfigPath("proxy-check-baseline.json");
            if (File.Exists(baseline)) ImportBaseline(File.ReadAllText(baseline), false);
        }
        catch (Exception ex) { Summary = ex.Message; }
    }

    public async Task RefreshEnvironmentAsync()
    {
        try
        {
            EnvironmentText = await ProxyCheckService.DescribeEnvironmentAsync();
            var config = AppManager.Instance.Config;
            NodeName = (await AppManager.Instance.GetProfileItem(config.IndexId))?.Remarks ?? "—";
            CoreName = ProxyCheckService.DescribeRunningCore();
            RoutingName = (await AppManager.Instance.GetRoutingItem(config.RoutingBasicItem.RoutingIndexId))?.Remarks ?? "—";
            CaptureMode = FormatCaptureMode(config.TunModeItem.EnableTun, config.SystemProxyItem.SysProxyType);
            LocalEntry = $"127.0.0.1:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}";
        }
        catch (Exception ex) { Summary = ex.Message; }
    }

    public async Task StartAsync(bool retry = false)
    {
        if (_disposed || _starting || IsRunning || IsTestingObserver) return;
        _starting = true; CanStart = CanRetry = false; _cancel = new();
        var generation = Interlocked.Increment(ref _generation);
        try
        {
            var options = new ProxyCheckOptions
            {
                Observer = ReadObserver(),
                ExpectedEgress = ExpectedEgress, ExpectedCountryCodes = SelectedCountryCodes, HttpsUrl = HttpsUrl, HttpUrl = HttpUrl, StunTarget = StunTarget,
                DnsTarget = DnsTarget, LocalDnsTarget = LocalDnsTarget,
                Samples = int.Parse(Samples), Timeout = int.Parse(Timeout), Http3Timeout = int.Parse(Http3Timeout)
            };
            foreach (var line in TargetRules.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) throw new ArgumentException(T("分流预期格式：目标=IP/CIDR，每行一条。", "Routing expectations: target=IP/CIDR, one per line."));
                options.TargetExpectations.Add(parts[0], parts[1]);
            }
            ProxyCheckService.Validate(options);
            var configuredCountries = options.ExpectedCountryCodes;
            // Retrying a report retains its advisory region snapshot, even if the next-run selection changed.
            if (retry && _report?.Results.FirstOrDefault() is { } previous)
                options.ExpectedCountryCodes = previous.ExpectedCountryCodes?.ToList() ?? [];
            ISet<string>? ids = retry ? Results.Where(r => r.State is "failed" or "cancelled").Select(r => r.Id).ToHashSet() : null;
            var runtime = retry ? await ProxyCheckService.RuntimeStateAsync(_cancel.Token) : "";
            if (retry && (ids?.Count == 0 || _report?.Fingerprint != ProxyCheckService.Fingerprint() || _report.OptionsFingerprint != ProxyCheckService.OptionsFingerprint(options)
                || runtime.Length > 0 && _report.RuntimeState.Length > 0 && runtime != _report.RuntimeState))
            {
                Summary = T("没有可重试项目，或运行配置已变化；请开始完整检查。", "No failed checks to retry, or configuration changed. Start a full check."); return;
            }
            _cancel.Token.ThrowIfCancellationRequested();
            var storedOptions = JsonSerializer.SerializeToNode(options, ProxyCheckService.Json)!;
            storedOptions["expectedCountryCodes"] = JsonSerializer.SerializeToNode(configuredCountries);
            AtomicSave(Utils.GetConfigPath("proxy-check-options.json"), storedOptions.ToJsonString(ProxyCheckService.Json));
            BeginLocationLookups();
            IsRunning = true; CanExport = false;
            LastChecked = DateTimeOffset.Now.ToString("g");
            if (!retry) { Results.Clear(); _report = null; }
            await RefreshEnvironmentAsync();
            Summary = T("正在检查；出口判断仅适用于当前探测目标。", "Checking; egress conclusions apply only to the probed targets.");
            var progress = new Progress<DiagnosticResult>(row =>
            {
                if (_disposed || !IsRunning || generation != _generation) return;
                var old = Results.FirstOrDefault(r => r.Id == row.Id);
                if (old == null) Results.Add(row); else Results[Results.IndexOf(old)] = row;
            });
            var report = await new ProxyCheckService().RunAsync(options, _baseline, progress, _cancel.Token, ids);
            // Use the final report as the source of truth, independently of queued progress callbacks.
            var merged = retry && _report != null ? _report.Results.Where(r => ids?.Contains(r.Id) != true).Concat(report.Results).ToList() : report.Results;
            report.Results = merged;
            _report = report;
            foreach (var row in merged)
            {
                var old = Results.FirstOrDefault(r => r.Id == row.Id);
                if (old == null) Results.Add(row); else Results[Results.IndexOf(old)] = row;
            }
            Summary = report.Completion == "configuration-changed" ? T("运行配置已变化，检查已取消。请重新检查。", "Configuration changed. Checks cancelled; run again.")
                : $"{(report.Completion == "cancelled" ? T("已取消", "Cancelled") : T("已完成", "Completed"))}: {ReachableCount} {T("项流量检查通信正常", "traffic checks reachable")} · {report.Results.Count(r => r.State == "failed")} {T("失败", "failed")} · {report.Results.Count(r => r.State == "unknown")} {T("无法判定", "inconclusive")}";
        }
        catch (OperationCanceledException) when (_cancel?.IsCancellationRequested == true) { Summary = T("检查已取消。", "Checks cancelled."); }
        catch (Exception ex) { Summary = ex.Message; }
        finally { _cancel?.Dispose(); _cancel = null; _starting = false; IsRunning = false; CanStart = !_disposed; CanExport = _report != null && !_disposed; CanRetry = !_disposed && Results.Any(r => r.State is "failed" or "cancelled"); }
    }

    public void Cancel()
    {
        _cancel?.Cancel();
        _observerCancel?.Cancel();
        CancelLocationLookups();
    }
    public string ExportReport()
    {
        if (_report == null || IsRunning) throw new InvalidOperationException(T("请先完成一次检查。", "Complete a check first."));
        return JsonSerializer.Serialize(_report, ProxyCheckService.Json);
    }
    public void SaveBaseline()
    {
        var json = ExportReport(); ImportBaseline(json);
        Summary = T("已保存当前报告为基线。直连基线需在关闭代理后采集；程序不会自动关闭代理。", "Current report saved as baseline. Capture a direct baseline with your proxy off; the app does not disable it automatically.");
    }
    public void ImportBaseline(string json, bool persist = true)
    {
        if (json.Length > 2 * 1024 * 1024) throw new ArgumentException("Baseline exceeds limit");
        var baseline = JsonSerializer.Deserialize<ProxyCheckReport>(json, ProxyCheckService.Json);
        if (baseline?.Version is not (1 or 2) || baseline.Results == null || baseline.Results.Any(r => r == null || string.IsNullOrEmpty(r.Id) || r.Egress == null || r.State == null)) throw new ArgumentException(T("无效的基线报告。", "Invalid baseline report."));
        _baseline = baseline;
        BaselineText = T("基线：", "Baseline: ") + baseline.StartedAt.ToLocalTime().ToString("g") + (baseline.Version == 1 ? T(" · 旧版证据，未经签名验证", " · Legacy evidence, not signature verified") : "");
        if (persist) AtomicSave(Utils.GetConfigPath("proxy-check-baseline.json"), JsonSerializer.Serialize(baseline, ProxyCheckService.Json));
    }
    private static void AtomicSave(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text); File.Move(temp, path, true);
    }
    public void ImportObserverProfile(string json)
    {
        if (json.Length > 16384) throw new ArgumentException("Observer profile exceeds limit");
        var profile = JsonSerializer.Deserialize<ObserverProfile>(json, ProxyCheckService.Json) ?? throw new ArgumentException("Invalid profile");
        ObserverSession.Validate(profile); ObserverProfileJson = JsonSerializer.Serialize(profile, ProxyCheckService.Json); UseObserver = true;
        ObserverStatus = $"{profile.ServerId} · IPv4: {profile.Ipv4} · " + T("IPv6 未覆盖；签名公钥 SHA-256：", "IPv6 not covered; signing key SHA-256: ") + Convert.ToHexString(SHA256.HashData(ObserverSession.Decode(profile.PublicKey)));
    }
    private ObserverProfile? ReadObserver()
    {
        if (!UseObserver) return null;
        var profile = JsonSerializer.Deserialize<ObserverProfile>(ObserverProfileJson, ProxyCheckService.Json);
        if (!string.IsNullOrWhiteSpace(ObserverDomain) && ObserverPairingService.NormalizeDomain(ObserverDomain) != profile?.Domain)
            throw new ArgumentException(T("服务域名已修改，请先配对新服务。", "The service domain changed. Pair the new service first."));
        ImportObserverProfile(ObserverProfileJson);
        return JsonSerializer.Deserialize<ObserverProfile>(ObserverProfileJson, ProxyCheckService.Json);
    }
    public async Task TestObserverAsync()
    {
        if (_disposed || _starting || IsRunning || IsTestingObserver) return;
        IsTestingObserver = true; CanStart = CanRetry = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var version = _observerSettingsVersion;
        var connecting = false;
        try
        {
            var profile = ReadObserver() ?? throw new ArgumentException(T("请先启用受控服务。", "Enable the observer first."));
            version = _observerSettingsVersion;
            _observerCancel = timeout;
            connecting = true;
            ObserverConnectionState = "testing";
            await using var session = await _connectObserver(new() { Observer = profile }, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (_disposed || version != _observerSettingsVersion) return;
            ObserverConnectionState = "passed";
            ObserverStatus += "\n" + T("双向认证连接成功；各协议端口将在完整检查时单独验证。", "Mutually authenticated control connection works; protocol ports are checked separately in a full run.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!_disposed && version == _observerSettingsVersion) { ObserverConnectionState = "unverified"; ObserverStatus = T("控制接口测试已取消或超时。", "Control connection test cancelled or timed out."); }
        }
        catch (Exception ex)
        {
            if (!_disposed && version == _observerSettingsVersion) { ObserverConnectionState = ex is ObserverStorageException ? "locked" : ex is ObserverAuthenticationException auth ? auth.State : connecting ? "failed" : "invalid"; ObserverStatus = ex.Message; }
        }
        finally { _observerCancel = null; IsTestingObserver = false; CanStart = !_disposed; CanRetry = !_disposed && Results.Any(r => r.State is "failed" or "cancelled"); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Cancel(); _exit.Dispose(); _presentation?.Dispose(); _observerSettings?.Dispose();
        _locationCancel?.Dispose(); _locationCancel = null;
        _countrySearchSubscription?.Dispose();
        foreach (var choice in CountryChoices) choice.PropertyChanged -= CountrySelectionChanged;
        Results.CollectionChanged -= ResultsChanged; ObserverPairingCode = ""; CanStart = CanRetry = CanExport = false;
    }
}
