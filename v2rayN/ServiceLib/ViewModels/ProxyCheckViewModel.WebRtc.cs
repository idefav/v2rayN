namespace ServiceLib.ViewModels;

public partial class ProxyCheckViewModel
{
    // A separate explicit action opens the real default browser, never a native STUN substitute.
    public async Task StartWebRtcAsync(Func<Uri, Task<bool>> launchBrowser)
    {
        if (_disposed || _starting || IsRunning || IsTestingObserver) return;
        _starting = true; IsRunning = true; CanStart = CanRetry = CanExport = false;
        _cancel = new CancellationTokenSource(TimeSpan.FromSeconds(165));
        var ct = _cancel.Token;
        var rows = new List<ProxyDiagnosticResult>();
        try
        {
            var profile = ReadObserver() ?? throw new ArgumentException(T("WebRTC 需要先配置独立探测服务。", "Configure the independent observer before WebRTC checks."));
            var options = new ProxyCheckOptions { Observer = profile, ExpectedEgress = ExpectedEgress };
            foreach (var line in TargetRules.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0) throw new ArgumentException("Invalid target expectation");
                options.TargetExpectations.Add(pair[0], pair[1]);
            }
            ProxyCheckService.Validate(options);
            if (!int.TryParse(Samples, out var count) || count is < 1 or > 3) throw new ArgumentException(T("浏览器检查支持每轮 1–3 次采样。", "Browser checks support 1–3 samples per run."));
            var fingerprint = ProxyCheckService.Fingerprint();
            var runtime = await ProxyCheckService.RuntimeStateAsync(ct);
            var report = new ProxyCheckReport { Observer = profile, Fingerprint = fingerprint, RuntimeState = runtime, OptionsFingerprint = ProxyCheckService.OptionsFingerprint(options), Environment = await ProxyCheckService.DescribeEnvironmentAsync() };
            await using var session = await ObserverSession.OpenAsync(options, ct);
            if (!session.SupportsWebRtc) throw new NotSupportedException(T("探测服务尚不支持 WebRTC，请升级服务端。", "Upgrade the observer to support WebRTC."));
            var tickets = new List<ObserverTicket>();
            for (var i = 0; i < count; i++) tickets.Add(await session.IssueAsync("webrtc", "", ct));
            // Start a separate report: do not mix old core/configuration evidence with a new browser run.
            Results.Clear(); _report = report;
            foreach (var ticket in tickets)
            {
                var row = new ProxyDiagnosticResult { Id = $"webrtc-browser-{rows.Count}", Group = "WebRTC", Name = $"DataChannel #{rows.Count + 1}", Path = "browser", Family = "IPv4", Target = new Uri(profile.HttpsUrl).Host + ":3480", State = "running" };
                rows.Add(row); Results.Add(row);
            }
            report.Results = rows;
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { tickets, profile.PublicKey, profile.ServerId }, ProxyCheckService.Json);
            var fragment = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var url = new Uri(new Uri(profile.HttpsUrl), "/webrtc#" + fragment);
            if (!await launchBrowser(url)) throw new IOException(T("无法打开默认浏览器。", "Could not open the default browser."));
            LastChecked = DateTimeOffset.Now.ToString("g");
            Summary = T("请保持浏览器检查页打开；本轮仅检查浏览器实际路径，最多等待 165 秒。", "Keep the browser check page open. This run tests the browser's actual path; up to 165 seconds.");
            while (rows.Any(r => r.State == "running"))
            {
                ct.ThrowIfCancellationRequested();
                var currentRuntime = await ProxyCheckService.RuntimeStateAsync(ct);
                if (fingerprint != ProxyCheckService.Fingerprint() || runtime.Length > 0 && currentRuntime.Length > 0 && runtime != currentRuntime)
                {
                    report.Completion = "configuration-changed"; _cancel.Cancel(); ct.ThrowIfCancellationRequested();
                }
                for (var i = 0; i < tickets.Count; i++)
                {
                    if (rows[i].State != "running") continue;
                    var status = await session.WebRtcStatusAsync(tickets[i], ct);
                    if (status.State == "connecting") rows[i].Attempts = 1;
                    if (status.State is not ("complete" or "failed" or "unsupported")) continue;
                    var row = status.ToResult(profile, tickets[i], options, i);
                    rows[i] = row; Results[i] = row;
                }
                if (rows.Any(r => r.State == "running")) await Task.Delay(1500, ct);
            }
            report.Completion = "completed";
            Summary = T("浏览器检查完成；候选地址与已签名的数据通道出口分别列出。", "Browser checks completed; candidate addresses and signed data-channel egress are listed separately.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Summary = _report?.Completion == "configuration-changed" ? T("核心或路由变化，浏览器检查已取消。", "Core or routing changed; browser checks cancelled.") : T("浏览器检查已取消或超时。", "Browser checks cancelled or timed out.");
            foreach (var row in rows.Where(r => r.State == "running")) { row.State = row.Connectivity = "cancelled"; }
            if (_report != null && rows.Count > 0 && _report.Completion != "configuration-changed") _report.Completion = "cancelled";
        }
        catch (Exception ex)
        {
            Summary = ex.Message;
            foreach (var row in rows.Where(r => r.State == "running")) { row.State = row.Connectivity = "failed"; row.Evidence = ex.Message; }
            if (_report != null && rows.Count > 0) _report.Completion = "failed";
        }
        finally
        {
            for (var i = 0; i < rows.Count; i++) Results[i] = rows[i];
            _cancel.Dispose(); _cancel = null; _starting = IsRunning = false; CanStart = !_disposed; CanExport = _report != null && !_disposed;
            // Browser checks need fresh short-lived tickets; repeat using the browser action.
            CanRetry = false;
        }
    }
}
