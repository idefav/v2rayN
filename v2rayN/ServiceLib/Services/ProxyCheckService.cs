using System.Net.Http;
using System.Globalization;
using DiagnosticResult = ServiceLib.Models.Dto.ProxyDiagnosticResult;

namespace ServiceLib.Services;

public sealed partial class ProxyCheckService
{
    internal sealed record Probe(string Id, string Group, string Kind, string Target, string Path, string Proxy = "", string Family = "", string Query = "", bool? TunEnabled = null);
    internal sealed class WorkerResult
    {
        public List<ObserverReceipt> Receipts { get; set; } = [];
        public string FailureStage { get; set; } = "";
        public int Version { get; set; }
        public string State { get; set; } = "failed";
        public double Elapsed { get; set; }
        public string Egress { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string Evidence { get; set; } = "";
        public string Error { get; set; } = "";
    }
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string T(string zh, string en) => ProxyCheckText.T(zh, en);

    public static void Validate(ProxyCheckOptions options)
    {
        if (options.Observer != null) ObserverSession.Validate(options.Observer);
        if (options.Samples is < 1 or > 10 || options.Timeout is < 1 or > 120 || options.Http3Timeout is < 1 or > 120)
            throw new ArgumentException(T("采样次数需为 1–10，超时需为 1–120 秒。", "Samples must be 1–10 and timeouts 1–120 seconds."));
        foreach (var value in options.TargetExpectations.Values.Append(options.ExpectedEgress))
            foreach (var network in Networks(value))
                if (!TryNetwork(network, out _, out _)) throw new ArgumentException(T("无效的出口 IP/CIDR: ", "Invalid egress IP/CIDR: ") + network);
        if (!Uri.TryCreate(options.HttpsUrl, UriKind.Absolute, out var https) || https.Scheme != "https" || !string.IsNullOrEmpty(https.UserInfo)
            || !Uri.TryCreate(options.HttpUrl, UriKind.Absolute, out var http) || http.Scheme != "http" || !string.IsNullOrEmpty(http.UserInfo))
            throw new ArgumentException(T("请填写有效的 HTTP / HTTPS 探测地址，不包含凭证。", "Provide valid HTTP / HTTPS probe URLs without credentials."));
        foreach (var target in new[] { options.StunTarget, options.DnsTarget, options.LocalDnsTarget }.Where(s => !string.IsNullOrWhiteSpace(s)))
            if (!Uri.TryCreate("udp://" + target, UriKind.Absolute, out var u) || u.Port is < 1 or > 65535 || u.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(u.UserInfo) || u.Query.Length > 0 || u.Fragment.Length > 0)
                throw new ArgumentException(T("无效的 host:port: ", "Invalid host:port: ") + target);
    }

    private static IEnumerable<string> Networks(string value) => value.Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool TryNetwork(string text, out IPAddress address, out int prefix)
    {
        var parts = text.Split('/'); prefix = 0; address = IPAddress.None;
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip)) return false;
        address = ip; prefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return parts.Length == 1 || int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) && prefix >= 0 && prefix <= ip.GetAddressBytes().Length * 8;
    }
    public static bool Matches(string ipText, string expected)
    {
        if (!IPAddress.TryParse(ipText, out var ip)) return false;
        foreach (var text in Networks(expected))
        {
            if (!TryNetwork(text, out var network, out var prefix) || network.AddressFamily != ip.AddressFamily) continue;
            var a = ip.GetAddressBytes(); var b = network.GetAddressBytes(); var full = prefix / 8; var rest = prefix % 8;
            if (!a.AsSpan(0, full).SequenceEqual(b.AsSpan(0, full))) continue;
            if (rest == 0 || (a[full] >> (8 - rest)) == (b[full] >> (8 - rest))) return true;
        }
        return false;
    }

    public static string ExpectedFor(string target, ProxyCheckOptions options, string kind = "", string path = "")
    {
        var uri = Uri.TryCreate(target, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : new Uri("udp://" + target);
        var host = uri.Host;
        var endpoint = host + ":" + uri.Port;
        foreach (var candidate in new[] { target, endpoint, host })
        {
            var scoped = options.TargetExpectations.FirstOrDefault(p => p.Key.Equals($"{candidate}|{kind}|{path}", StringComparison.OrdinalIgnoreCase)).Value;
            if (scoped != null) return scoped;
        }
        return options.TargetExpectations.FirstOrDefault(p => p.Key.Equals(target, StringComparison.OrdinalIgnoreCase)).Value
            ?? options.TargetExpectations.FirstOrDefault(p => p.Key.Equals(endpoint, StringComparison.OrdinalIgnoreCase)).Value
            ?? options.TargetExpectations.FirstOrDefault(p => p.Key.Equals(host, StringComparison.OrdinalIgnoreCase)).Value ?? options.ExpectedEgress;
    }

    public static string Fingerprint()
    {
        var config = AppManager.Instance.Config;
        var value = JsonSerializer.Serialize(new { config.IndexId, config.Inbound, config.TunModeItem, config.RoutingBasicItem, config.SystemProxyItem, config.CoreBasicItem, config.CoreTypeItem }, Json) + AppManager.Instance.RunningCoreType;
        var path = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        if (File.Exists(path)) value += File.ReadAllText(path);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static string OptionsFingerprint(ProxyCheckOptions options)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(options, Json))));

    public static async Task<string> DescribeEnvironmentAsync()
    {
        var config = AppManager.Instance.Config;
        var node = await AppManager.Instance.GetProfileItem(config.IndexId);
        var routing = await AppManager.Instance.GetRoutingItem(config.RoutingBasicItem.RoutingIndexId);
        var runtime = await RuntimeStateAsync(CancellationToken.None);
        return $"{T("节点", "Node")}: {node?.Remarks ?? "—"} · {T("内核", "Core")}: {DescribeRunningCore()}\n"
            + $"{T("路由", "Routing")}: {routing?.Remarks ?? "—"} · {T("运行模式", "Runtime mode")}: {(runtime.Length > 0 ? runtime.Split('|')[0] : T("未读取", "Unavailable"))} · TUN: {config.TunModeItem.EnableTun} · {T("系统代理配置", "Configured system proxy")}: {config.SystemProxyItem.SysProxyType}\n"
            + $"{T("本地入口", "Local ingress")}: 127.0.0.1:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}\n"
            + T("系统实际代理将按每个 URL 读取；普通套接字仍可能被 TUN 接管。", "Actual system proxy is resolved per URL; ordinary sockets may still traverse a TUN.");
    }

    internal static string DescribeRunningCore()
    {
        if (!CoreManager.Instance.IsCoreRunning) return T("未运行", "Not running");
        var core = AppManager.Instance.RunningCoreType;
        return !Enum.IsDefined(core) ? T("未知", "Unknown") : core == ECoreType.sing_box ? "sing-box" : core.ToString();
    }

    internal static async Task<string> RuntimeStateAsync(CancellationToken ct)
    {
        if (AppManager.Instance.RunningCoreType is not (ECoreType.sing_box or ECoreType.mihomo)) return "";
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            var root = $"http://127.0.0.1:{AppManager.Instance.StatePort2}";
            using var config = JsonDocument.Parse(await client.GetStringAsync(root + "/configs", ct));
            var mode = config.RootElement.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "";
            using var proxies = JsonDocument.Parse(await client.GetStringAsync(root + "/proxies", ct));
            var selected = new SortedDictionary<string, string>();
            if (proxies.RootElement.TryGetProperty("proxies", out var entries))
                foreach (var p in entries.EnumerateObject())
                    if (p.Value.TryGetProperty("now", out var now) && now.ValueKind == JsonValueKind.String) selected[p.Name] = now.GetString() ?? "";
            return mode + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(selected))));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { return ""; }
    }

    public static string DiscoverLocalDns()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Utils.GetBinConfigPath(Global.CoreConfigFileName)));
            if (doc.RootElement.TryGetProperty("inbounds", out var inbounds))
                foreach (var item in inbounds.EnumerateArray())
                {
                    var isDns = item.TryGetProperty("type", out var type) && type.GetString() == "direct"
                        && item.TryGetProperty("override_port", out var destPort) && destPort.GetInt32() == 53;
                    isDns |= item.TryGetProperty("tag", out var tag) && (tag.GetString()?.Contains("dns", StringComparison.OrdinalIgnoreCase) ?? false);
                    if (!isDns) continue;
                    if (!item.TryGetProperty("listen_port", out var port) && !item.TryGetProperty("port", out port)) continue;
                    var host = item.TryGetProperty("listen", out var listen) ? listen.GetString() : "127.0.0.1";
                    if (host is null or "0.0.0.0" or "::") host = "127.0.0.1";
                    return $"{(host.Contains(':') ? "[" + host + "]" : host)}:{port.GetInt32()}";
                }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
        return "";
    }

    internal static List<Probe> CreateProbes(ProxyCheckOptions options, int port)
    {
        if (options.Observer != null) return CreateObserverProbes(options, port);
        var result = new List<Probe>();
        var httpsUri = new Uri(options.HttpsUrl);
        var tcpTarget = (httpsUri.Host.Contains(':') ? "[" + httpsUri.Host.Trim('[', ']') + "]" : httpsUri.Host) + ":" + httpsUri.Port;
        foreach (var path in new[] { "socks", "native" })
        {
            var proxy = path == "socks" ? $"socks5://127.0.0.1:{port}" : "";
            foreach (var family in new[] { "4", "6" })
            {
                // Literal destinations enforce the remote address family even through SOCKS.
                var https = family == "4" ? "https://1.1.1.1/cdn-cgi/trace" : "https://[2606:4700:4700::1111]/cdn-cgi/trace";
                result.Add(new($"{path}-ipv{family}", "IPv" + family, "https", https, path, proxy, family));
            }
            foreach (var (kind, target) in new[] { ("tcp", tcpTarget), ("http", options.HttpUrl), ("https", options.HttpsUrl), ("stun", options.StunTarget), ("h3", options.HttpsUrl) })
                result.Add(new($"{path}-{kind}", kind is "stun" ? "UDP" : kind == "h3" ? "QUIC/HTTP3" : "TCP/TLS", kind, target, path, proxy));
            foreach (var kind in new[] { "dns-udp", "dns-tcp" })
                foreach (var query in new[] { "A", "AAAA", "NXDOMAIN", "WHOAMI" })
                    result.Add(new($"{path}-{kind}-{query}", "DNS", kind, options.DnsTarget, path, proxy, "", query));
        }
        result.Add(new("http-connect", "TCP/TLS", "https", options.HttpsUrl, "http-connect", $"http://127.0.0.1:{port}"));
        result.Add(new("system-http", "TCP/TLS", "system-http", options.HttpUrl, "system"));
        result.Add(new("system-https", "TCP/TLS", "system-http", options.HttpsUrl, "system"));
        result.Add(new("system-proxy", T("网络接管", "Network capture"), "system-proxy", options.HttpsUrl, "system"));
        foreach (var family in new[] { "4", "6" }) result.Add(new("system-dns-" + family, "DNS", "system-dns", "example.com", "system", "", family));
        result.Add(new("system-nxdomain", "DNS", "system-dns", Guid.NewGuid().ToString("N") + ".invalid", "system", "", "", "NXDOMAIN"));
        var local = string.IsNullOrWhiteSpace(options.LocalDnsTarget) ? DiscoverLocalDns() : options.LocalDnsTarget;
        if (local.Length > 0)
            foreach (var kind in new[] { "dns-udp", "dns-tcp" })
                foreach (var query in new[] { "A", "AAAA", "NXDOMAIN" }) result.Add(new($"local-{kind}-{query}", "DNS", kind, local, "local-dns", "", "", query));
        else result.Add(new("local-dns-missing", "DNS", "missing", "local DNS", "local-dns"));
        if (ReadSshOutbound() != null) result.Add(new("ssh-udp-capability", "UDP", "ssh-capability", "SSH upstream", "ssh"));
        return result;
    }

    public async Task<ProxyCheckReport> RunAsync(ProxyCheckOptions options, ProxyCheckReport? baseline,
        IProgress<DiagnosticResult> progress, CancellationToken ct, ISet<string>? retry = null)
    {
        Validate(options);
        var tunEnabled = AppManager.Instance.Config.TunModeItem.EnableTun;
        var report = new ProxyCheckReport { Observer = options.Observer, Environment = await DescribeEnvironmentAsync(), Fingerprint = Fingerprint(), OptionsFingerprint = OptionsFingerprint(options), RuntimeState = await RuntimeStateAsync(ct) };
        await using var observer = options.Observer == null ? null : await ObserverSession.OpenAsync(options, ct);
        var probes = CreateProbes(options, AppManager.Instance.GetLocalPort(EInboundProtocol.socks))
            .Select(p => p with { TunEnabled = tunEnabled })
            .Where(p => retry == null || retry.Contains(p.Id))
            .OrderBy(p => p.Kind == "system-proxy" ? 0 : p.Group switch { "TCP/TLS" => 1, "DNS" => 2, "UDP" => 3, "QUIC/HTTP3" => 4, "IPv4" => 5, _ => 6 }).ToList();
        foreach (var p in probes) progress.Report(NewResult(p));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var changed = false;
        var watch = Task.Run(async () =>
        {
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    await Task.Delay(1000, linked.Token);
                    if (Fingerprint() != report.Fingerprint) { changed = true; linked.Cancel(); }
                    var runtime = await RuntimeStateAsync(linked.Token);
                    if (runtime.Length > 0 && report.RuntimeState.Length > 0 && runtime != report.RuntimeState) { changed = true; linked.Cancel(); }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { changed = true; linked.Cancel(); }
        }, CancellationToken.None);
        try
        {
            await Parallel.ForEachAsync(probes, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = linked.Token }, async (p, token) =>
            {
                var row = NewResult(p); row.State = "running"; progress.Report(row);
                row = observer == null ? await CheckAsync(p, options, token) : await CheckObserverAsync(p, options, observer, token);
                var old = baseline?.Results?.FirstOrDefault(x => x.Id == row.Id);
                if (old != null && old.Target != row.Target)
                    row.BaselineComparison = T("探测目标已变化，不比较该项基线。", "Probe target changed; baseline comparison is not applicable.");
                else if (old != null && old.Egress.Length > 0 && row.Egress.Length > 0)
                    row.BaselineComparison = T("与基线出口", "Baseline egress") + (old.Egress == row.Egress ? T("相同；不据此认定泄漏。", " unchanged; this alone does not establish leakage.") : T("不同；变化本身不证明代理正确。", " changed; this alone does not establish correct proxying."));
                lock (report.Results) report.Results.Add(row);
                progress.Report(row);
            });
            report.Completion = "completed";
        }
        catch (OperationCanceledException)
        {
            report.Completion = changed ? "configuration-changed" : "cancelled";
        }
        finally { linked.Cancel(); await watch; }
        foreach (var p in probes.Where(p => report.Results.All(r => r.Id != p.Id)))
        {
            var row = NewResult(p); row.State = row.Connectivity = "cancelled";
            row.Advice = changed ? T("运行配置已变化，请重新检查。", "Running configuration changed. Run the checks again.") : "";
            report.Results.Add(row); progress.Report(row);
        }
        return report;
    }

    private static DiagnosticResult NewResult(Probe p) => new()
    {
        Id = p.Id, Group = p.Group,
        Name = (p.Kind switch { "system-proxy" => T("系统代理设置", "System proxy settings"), "system-dns" => T("系统解析", "System resolver"), "system-http" => T("系统 HTTP 请求", "System HTTP request"), "missing" => T("本地 DNS 发现", "Local DNS discovery"), _ => p.Kind.ToUpperInvariant() }) + (p.Query.Length > 0 ? " / " + p.Query : ""), Target = p.Target,
        Family = p.Family.Length > 0 ? "IPv" + p.Family : T("自动", "Auto"),
        Path = p.Path switch { "ssh" => T("SSH 扩展协商", "SSH capability negotiation"), "socks" => "SOCKS5", "native" => p.TunEnabled is bool tun ? ProxyCheckPathCapabilities.NativePath(tun) : T("普通套接字 / TUN", "Native sockets / TUN"), "system" => T("系统实际路径", "Actual system path"), "local-dns" => T("本地 DNS 入口", "Local DNS ingress"), _ => "HTTP CONNECT" }
    };

    private static async Task<DiagnosticResult> CheckAsync(Probe p, ProxyCheckOptions options, CancellationToken ct)
    {
        var row = NewResult(p);
        row.Expected = p.Kind is "system-dns" or "missing" or "ssh-capability" ? "" : ExpectedFor(p.Target, options);
        if (p.Kind == "missing") { row.State = row.Connectivity = "skipped"; row.Evidence = T("未发现独立 DNS 监听；可在高级选项指定。", "No separate DNS listener discovered; specify one in advanced options."); return row; }
        if (p.Path == "native" && p.Family == "6" && !NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal && !IPAddress.IsLoopback(a.Address)))
        {
            row.State = row.Connectivity = "unsupported";
            row.Evidence = T("本机没有可用的非链路本地 IPv6 地址，此路径未验证；SOCKS 远端 IPv6 仍单独检查。", "No usable non-link-local local IPv6 address; this path is unverified. Remote IPv6 via SOCKS is checked separately.");
            return row;
        }
        if (ProxyCheckPathCapabilities.IsFixedUnsupported(p.Path, p.Kind)) { row.State = row.Connectivity = "unsupported"; row.EgressState = "not-applicable"; row.Evidence = T("HTTP CONNECT 不支持普通 UDP，未改走本机网络。", "HTTP CONNECT does not support ordinary UDP; no direct fallback was attempted."); return row; }
        var exits = new List<string>(); var errors = new List<string>();
        for (var i = 0; i < (p.Kind == "ssh-capability" ? 1 : options.Samples); i++)
        {
            if (ct.IsCancellationRequested) { row.State = row.Connectivity = "cancelled"; return row; }
            row.Attempts++;
            try
            {
                var r = p.Kind.StartsWith("system-") ? await SystemProbeAsync(p, options.Timeout, ct)
                    : p.Kind == "stun" && p.Path == "socks" ? await SocksStunAsync(p, options.Timeout, ct)
                    : await WorkerAsync(p, p.Kind == "h3" ? options.Http3Timeout : options.Timeout, ct);
                row.Protocol = r.Protocol; row.Evidence = r.Evidence; row.Elapsed += r.Elapsed;
                if (r.State == "passed") { row.Successes++; if (!string.IsNullOrEmpty(r.Egress)) exits.Add(r.Egress); }
                else { errors.Add(r.Error); if (r.State == "unsupported") { row.State = row.Connectivity = "unsupported"; row.Advice = ExplainUdpError(r.Error); return row; } }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { row.State = row.Connectivity = "cancelled"; return row; }
            catch (Exception ex) { errors.Add(ex is OperationCanceledException ? T("请求超时", "Request timed out") : ex.Message); }
        }
        row.Elapsed /= Math.Max(1, row.Attempts);
        Evaluate(row, exits);
        if (p.Kind is "ssh-capability" or "system-proxy") { row.State = row.Connectivity; row.EgressState = "not-applicable"; row.ObservationType = "not-applicable"; }
        if (errors.Count > 0) row.Evidence += "\n" + string.Join("\n", errors.Distinct());
        row.Advice = row.Connectivity == "failed" ? T("检查入口、路由和目标服务；超时本身不证明泄漏。", "Check ingress, routing and endpoint health; a timeout alone does not establish leakage.")
            : row.EgressState == "failed" ? T("观测出口不符合配置的预期，请检查分流规则。", "Observed egress does not match the configured expectation. Check routing rules.")
            : row.EgressState == "unknown" ? T("通信正常；缺少预期出口或出口证据，无法判断是否正确代理。", "Connectivity works; an expected egress or egress evidence is missing, so proxy correctness is inconclusive.") : "";
        if (p.Kind == "h3" && row.Connectivity == "failed" && p.Path == "socks")
            row.Advice += "\n" + T("若规则集中启用了“阻断udp443”，请在 SSH 节点的高级选项启用 UDP over SSH 和“允许 HTTP3”；其他自定义规则仍需单独检查。", "If the routing set blocks UDP 443, enable UDP over SSH and Allow HTTP3 in the SSH node's advanced options; inspect other custom rules separately.");
        return row;
    }

    internal static void Evaluate(DiagnosticResult row, List<string> exits)
    {
        row.Connectivity = row.Attempts > 0 && row.Successes == row.Attempts ? "passed" : "failed";
        row.Egress = string.Join(", ", exits.Distinct().Order(StringComparer.Ordinal));
        row.EgressState = row.Expected.Length > 0 && exits.Any(ip => !Matches(ip, row.Expected)) ? "failed" : exits.Count != row.Attempts ? "unknown" : row.Expected.Length == 0 ? "not-configured" : "passed";
        row.State = row.Connectivity == "failed" || row.EgressState == "failed" ? "failed" : row.EgressState == "passed" ? "passed" : "unknown";
    }

    private static string ExplainUdpError(string error)
        => error.Contains("E_UDP_FORWARDING_DISABLED") ? T("远端 SSH 的 UDP 转发被配置或密钥权限禁用。", "Remote SSH UDP forwarding is disabled by configuration or key permissions.")
        : error.Contains("E_UDP_UPSTREAM_UNSUPPORTED") ? T("远端 SSH 的上游代理不支持 UDP。", "The remote SSH server's upstream proxy does not support UDP.")
        : error.Contains("E_UDP_PROXY_UNSUPPORTED") ? T("远端 SSH 服务未提供 UDP 扩展。请使用部署了扩展的独立 SSH 服务。", "Remote SSH does not provide the UDP extension. Use the independent UDP-enabled SSH service.") : error;

    private static async Task<WorkerResult> SocksStunAsync(Probe p, int timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        var proxy = new Uri(p.Proxy); var target = new Uri("udp://" + p.Target);
        using var channel = new ServiceLib.UdpTest.Socks5UdpChannel(proxy.Host, proxy.Port);
        using var stop = deadline.Token.Register(channel.Dispose);
        var start = Stopwatch.StartNew();
        if (!await channel.EstablishUdpAssociationAsync(deadline.Token)) return new() { State = channel.ReplyCode == 7 ? "unsupported" : "failed", Error = "SOCKS5 UDP ASSOCIATE failed; reply=" + channel.ReplyCode };
        var stun = new ServiceLib.UdpTest.Tester.StunService(); var packet = stun.BuildUdpRequestPacket();
        await channel.SendAsync(target.Host, (ushort)target.Port, packet, deadline.Token);
        while (true)
        {
            var (remote, data) = await channel.ReceiveAsync(deadline.Token);
            if (remote.Port != target.Port) continue;
            if (!ServiceLib.UdpTest.Tester.StunService.TryExtractMappedAddress(data, packet.AsSpan(8), out var ip)) throw new IOException("Invalid STUN response or transaction");
            return new() { Version = 1, State = "passed", Protocol = "STUN/UDP", Egress = ip!.ToString(), Elapsed = start.Elapsed.TotalMilliseconds, Evidence = "SOCKS5 UDP ASSOCIATE; matched transaction; server-observed mapped address" };
        }
    }

    internal static async Task<WorkerResult> WorkerAsync(Probe probe, int timeout, CancellationToken ct, object? observer = null)
    {
        var ssh = probe.Kind == "ssh-capability" ? ReadSshOutbound() : null;
        if (probe.Kind == "ssh-capability" && ssh?["udp_forwarding"]?.GetValue<bool>() != true)
            return new() { State = "unsupported", Error = T("当前 SSH 出站未启用 UDP over SSH；需要专用核心并在节点高级选项开启。", "UDP over SSH is not enabled for the SSH outbound; use the bundled core and enable the node's advanced option.") };
        if (ssh?["detour"] != null) return new() { State = "unsupported", Error = T("复杂 SSH 出站链需以实际 UDP 数据测试为准。", "Use actual UDP data probes for a chained SSH outbound.") };
        var path = Utils.GetBaseDirectory("proxy-check" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(path)) return new() { State = "unsupported", Error = T("安装包缺少 proxy-check 探测组件。", "The package is missing the proxy-check worker.") };
        using var process = new Process { StartInfo = new(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(timeout + 2));
        if (!process.Start()) throw new IOException("Cannot start proxy-check");
        using var stop = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        // Do not log requests: proxy credentials, if added later, belong only in stdin.
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { version = observer == null ? 1 : 2, kind = probe.Kind, target = probe.Target, proxy = probe.Proxy, family = probe.Family, query = probe.Query, timeout, ssh, observer }, Json));
        process.StandardInput.Close();
        var stdout = ReadLimitedAsync(process.StandardOutput, deadline.Token);
        var stderr = ReadLimitedAsync(process.StandardError, deadline.Token);
        await process.WaitForExitAsync(deadline.Token);
        var output = await stdout; await stderr;
        if (process.ExitCode != 0) throw new IOException("proxy-check exited: " + process.ExitCode);
        var result = JsonSerializer.Deserialize<WorkerResult>(output, Json);
        if (result?.Version != (observer == null ? 1 : 2) || result.State is not ("passed" or "failed" or "unsupported")) throw new IOException("Invalid proxy-check response");
        return result;
    }

    private static JsonObject? ReadSshOutbound()
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Utils.GetBinConfigPath(Global.CoreConfigFileName)));
            var ssh = root?["outbounds"]?.AsArray().OfType<JsonObject>().Where(x => x["type"]?.GetValue<string>() == "ssh").ToArray();
            return ssh?.Length == 1 ? ssh[0] : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return null; }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken ct)
    {
        var b = new char[65537]; var total = 0;
        while (total < b.Length)
        {
            var n = await reader.ReadAsync(b.AsMemory(total), ct); if (n == 0) return new string(b, 0, total); total += n;
        }
        throw new IOException("Worker output exceeds limit");
    }

    private static async Task<WorkerResult> SystemProbeAsync(Probe p, int timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        var start = Stopwatch.StartNew();
        var r = new WorkerResult { Version = 1, State = "passed" };
        if (p.Kind == "system-proxy")
        {
            var proxy = HttpClient.DefaultProxy; var target = new Uri(p.Target);
            var actual = proxy.GetProxy(target); var direct = proxy.IsBypassed(target) || actual == target;
            r.Protocol = "System proxy";
            r.Evidence = "actual=" + (direct ? "DIRECT (may traverse TUN)" : actual?.GetLeftPart(UriPartial.Authority));
            var config = AppManager.Instance.Config;
            if (config.SystemProxyItem.SysProxyType == ESysProxyType.ForcedChange && (direct || actual?.Port != AppManager.Instance.GetLocalPort(EInboundProtocol.socks) || actual.Host is not ("127.0.0.1" or "localhost" or "[::1]")))
            { r.State = "failed"; r.Error = T("系统实际代理与 v2rayN 配置不一致。", "Actual system proxy differs from the v2rayN configuration."); }
        }
        else if (p.Kind == "system-dns")
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(p.Target, p.Family == "4" ? AddressFamily.InterNetwork : p.Family == "6" ? AddressFamily.InterNetworkV6 : AddressFamily.Unspecified, deadline.Token);
                if (p.Query == "NXDOMAIN" || addresses.Length == 0) throw new IOException("Unexpected DNS result");
                r.Evidence = string.Join(", ", addresses.Select(a => a.ToString())); r.Protocol = "System resolver";
            }
            catch (SocketException ex) when (p.Query == "NXDOMAIN" && ex.SocketErrorCode == SocketError.HostNotFound) { r.Evidence = "NXDOMAIN / system HostNotFound"; r.Protocol = "System resolver"; }
        }
        else
        {
            // A new handler avoids stale proxy settings from a shared HttpClient.
            var proxy = HttpClient.DefaultProxy; var uri = new Uri(p.Target);
            var resolved = proxy.GetProxy(uri);
            var route = proxy.IsBypassed(uri) || resolved == uri ? "DIRECT (may traverse TUN)" : resolved?.GetLeftPart(UriPartial.Authority) ?? "unknown";
            using var handler = new HttpClientHandler { UseProxy = true, Proxy = proxy, AllowAutoRedirect = false };
            using var client = new HttpClient(handler);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var body = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
            var content = await ReadLimitedAsync(body, deadline.Token);
            r.Egress = content.Split('\n').FirstOrDefault(s => s.StartsWith("ip="))?[3..].Trim() ?? "";
            if (r.Egress.Length > 0 && !IPAddress.TryParse(r.Egress, out _)) throw new IOException("Invalid egress address");
            r.Protocol = "HTTP/" + response.Version;
            r.Evidence = $"actual proxy={route}; status={(int)response.StatusCode}; " + T("HTTPS 使用系统证书校验。", "HTTPS uses system certificate validation.");
        }
        r.Elapsed = start.Elapsed.TotalMilliseconds; return r;
    }
}
