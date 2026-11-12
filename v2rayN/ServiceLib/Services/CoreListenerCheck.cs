using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Services;

internal sealed record CoreListener(IPAddress Address, int Port, ProtocolType Protocol, bool Socks = false, bool Authentication = false)
{
    public override string ToString() => $"{Protocol} {new IPEndPoint(Address, Port)}";
    internal IPAddress ProbeAddress => Address.Equals(IPAddress.Any) ? IPAddress.Loopback
        : Address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : Address;
}

internal static class CoreListenerCheck
{
    internal static IReadOnlyList<CoreListener> Read(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var listeners = new List<CoreListener>();
        if (root?["inbounds"] is JsonArray inbounds)
            foreach (var item in inbounds.OfType<JsonObject>())
            {
                var type = item["type"]?.GetValue<string>();
                if (item["listen_port"]?.GetValue<int>() is not { } port || port == 0) continue;
                var address = ParseAddress(item["listen"]?.GetValue<string>() ?? "::");
                var socks = type is "mixed" or "socks";
                var networks = item["network"] is JsonArray list ? list.Select(v => v!.GetValue<string>()).ToArray()
                    : item["network"]?.GetValue<string>() is { Length: > 0 } value ? [value] : Array.Empty<string>();
                var udpOnly = type is "hysteria" or "hysteria2" or "tuic" || item["transport"]?["type"]?.GetValue<string>() == "quic";
                var bothByDefault = type is "direct" or "shadowsocks" or "tproxy" or "naive";
                if (!udpOnly && (networks.Length == 0 || networks.Contains("tcp")))
                    listeners.Add(new(address, port, ProtocolType.Tcp, socks && item["tls"]?["enabled"]?.GetValue<bool>() != true,
                        item["users"] is JsonArray { Count: > 0 }));
                if (udpOnly || bothByDefault && (networks.Length == 0 || networks.Contains("udp")))
                    listeners.Add(new(address, port, ProtocolType.Udp));
            }
        foreach (var api in new[] { root?["experimental"]?["clash_api"]?["external_controller"], root?["experimental"]?["v2ray_api"]?["listen"] })
        {
            var value = api?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (value.StartsWith(':')) value = "0.0.0.0" + value;
            if (!IPEndPoint.TryParse(value, out var endpoint)) throw new InvalidOperationException("无法识别管理 API 监听地址 / Invalid API listener: " + value);
            if (endpoint.Port != 0) listeners.Add(new(endpoint.Address, endpoint.Port, ProtocolType.Tcp));
        }
        return listeners.Distinct().ToArray();
    }

    private static IPAddress ParseAddress(string address) => IPAddress.TryParse(address, out var ip) ? ip
        : throw new InvalidOperationException("无法识别监听地址 / Invalid listen address: " + address);

    internal static bool Overlaps(CoreListener expected, CoreListener actual)
    {
        if (expected.Port != actual.Port || expected.Protocol != actual.Protocol) return false;
        var a = expected.Address.IsIPv4MappedToIPv6 ? expected.Address.MapToIPv4() : expected.Address;
        var b = actual.Address.IsIPv4MappedToIPv6 ? actual.Address.MapToIPv4() : actual.Address;
        // sing-box's unspecified IPv6 listener uses the platform's dual-stack socket.
        if (a.Equals(IPAddress.IPv6Any) || b.Equals(IPAddress.IPv6Any)) return true;
        if (a.AddressFamily != b.AddressFamily) return false;
        return a.Equals(b) || a.Equals(IPAddress.Any) || b.Equals(IPAddress.Any);
    }

    internal static IReadOnlyList<CoreListener> ActiveListeners()
    {
        // Do not use Utils.PortInUse: it includes established connections and TIME_WAIT.
        // Failures here must propagate instead of being mistaken for an empty table.
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        return properties.GetActiveTcpListeners().Select(p => new CoreListener(p.Address, p.Port, ProtocolType.Tcp))
            .Concat(properties.GetActiveUdpListeners().Select(p => new CoreListener(p.Address, p.Port, ProtocolType.Udp))).ToArray();
    }

    internal static async Task EnsureAvailableAsync(IReadOnlyList<CoreListener> expected,
        Func<CoreListener, CancellationToken, Task<SocketError>>? elevatedProbe = null, CancellationToken token = default)
    {
        if (expected.Count == 0) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        timeout.Token.ThrowIfCancellationRequested();
        foreach (var endpoint in expected)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var overlapping = ActiveListeners().Where(active => Overlaps(endpoint, active)).ToArray();
            // An empty LISTEN table does not imply bind is possible: bound-only
            // sockets and cross-UID TCP remnants can still reserve the address.
            // Probing also resolves ambiguous IPv6-only versus dual-stack listeners.
            async Task<SocketError> ProbeAsync() => elevatedProbe != null
                ? await elevatedProbe(endpoint, timeout.Token)
                : ListenerBindProbe.Check(endpoint.Address, endpoint.Port, endpoint.Protocol);
            var error = await ProbeAsync();
            // Closing a BSD socket with recently completed async IO can defer the
            // native close briefly. Poll the actual bind, never just sleep and
            // assume release, and keep the whole check within its shared budget.
            var retryClock = Stopwatch.StartNew();
            for (; error == SocketError.AddressAlreadyInUse && overlapping.Length == 0
                && retryClock.Elapsed < TimeSpan.FromSeconds(2);)
            {
                await Task.Delay(100, timeout.Token);
                error = await ProbeAsync();
                overlapping = ActiveListeners().Where(active => Overlaps(endpoint, active)).ToArray();
            }
            if (error != SocketError.Success)
                overlapping = ActiveListeners().Where(active => Overlaps(endpoint, active)).ToArray();
            // The table can lag a just-closed listener on macOS. A successful
            // non-sharing bind is authoritative; stale rows must not block restart.
            if (error != SocketError.Success && overlapping.Any(active => endpoint.Address.AddressFamily == active.Address.AddressFamily))
                throw new IOException($"端口被占用，已中止启动 / Listener occupied: {endpoint}. " + await DescribeOwnerAsync(endpoint));
            timeout.Token.ThrowIfCancellationRequested();
            if (error == SocketError.Success) continue;
            if (error == SocketError.AddressAlreadyInUse && overlapping.Length > 0)
                throw new IOException($"端口被占用，已中止启动 / Listener occupied: {endpoint}. " + await DescribeOwnerAsync(endpoint));
            if (error != SocketError.AddressAlreadyInUse)
                throw new IOException($"端口绑定检查失败，已中止启动 / Listener bind check failed: {endpoint} ({error}).");
            throw new IOException($"端口无法绑定，已中止启动 / Cannot bind listener: {endpoint} ({error}). " +
                "未确认匹配的监听进程；端口可能仍由非监听套接字占用或尚未释放，请稍后重试。" +
                "未终止任何未知进程。 / No confirmed listener owner; the socket may still be reserved. No unknown process was terminated.");
        }
    }

    private static async Task<string> DescribeOwnerAsync(CoreListener endpoint)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (Utils.IsNonWindows())
            {
                var args = new List<string> { "-nP", "-a", $"-i{(endpoint.Protocol == ProtocolType.Tcp ? "TCP" : "UDP")}:{endpoint.Port}", "-Fpc" };
                if (endpoint.Protocol == ProtocolType.Tcp) args.Add("-sTCP:LISTEN");
                var result = await Cli.Wrap(Utils.IsMacOS() ? "/usr/sbin/lsof" : "lsof").WithArguments(args)
                    .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync(timeout.Token);
                var pids = result.StandardOutput.Split('\n').Where(l => l.StartsWith('p') && int.TryParse(l.AsSpan(1), out _))
                    .Select(l => int.Parse(l.AsSpan(1))).Distinct().ToArray();
                var snapshot = await UnixProcessControl.SnapshotAsync(timeout.Token);
                var owners = snapshot.Where(p => pids.Contains(p.Identity.Pid)).Select(p => $"PID {p.Identity.Pid}: {p.Identity.Executable}").ToArray();
                if (owners.Length > 0) return string.Join("; ", owners);
            }
            else
            {
                var result = await Cli.Wrap("netstat").WithArguments(["-ano"])
                    .ExecuteBufferedAsync(timeout.Token);
                var owners = new List<string>();
                foreach (var line in result.StandardOutput.Split('\n'))
                {
                    var fields = Regex.Split(line.Trim(), @"\s+");
                    if (fields.Length < 4 || !fields[0].Equals(endpoint.Protocol.ToString(), StringComparison.OrdinalIgnoreCase)
                        || !IPEndPoint.TryParse(fields[1], out var address) || address.Port != endpoint.Port
                        || endpoint.Protocol == ProtocolType.Tcp && (fields.Length < 5 || fields[3] != "LISTENING")
                        || !int.TryParse(fields[^1], out var pid)) continue;
                    using var process = Process.GetProcessById(pid);
                    owners.Add($"PID {pid}: {process.MainModule?.FileName ?? process.ProcessName}");
                }
                if (owners.Count > 0) return string.Join("; ", owners);
            }
        }
        catch (Exception ex) { Logging.SaveLog("Listener owner lookup: " + ex.GetType().Name); }
        return "占用进程信息不可读取（可能权限不足）；未终止任何未知进程。 / Owner unavailable; no unknown process was terminated.";
    }

    internal static async Task WaitForReadyAsync(ProcessService process, IReadOnlyList<CoreListener> endpoints, CancellationToken token)
    {
        await process.WaitForIdentityAsync(token);
        var consecutive = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (await process.HasExitedAsync(token)) throw new IOException("内核启动失败 / Core exited during startup. " + process.RecentError);
            var ready = endpoints.Count > 0;
            foreach (var endpoint in endpoints)
            {
                if (endpoint.Protocol == ProtocolType.Udp)
                {
                    ready &= ActiveListeners().Any(p => Overlaps(endpoint, p));
                    continue;
                }
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(250));
                try
                {
                    using var socket = new TcpClient(endpoint.Address.AddressFamily);
                    await socket.ConnectAsync(endpoint.ProbeAddress, endpoint.Port, attempt.Token);
                    if (endpoint.Socks)
                    {
                        var stream = socket.GetStream();
                        await stream.WriteAsync(new byte[] { 5, 1, endpoint.Authentication ? (byte)2 : (byte)0 }, attempt.Token);
                        var reply = new byte[2];
                        await stream.ReadExactlyAsync(reply, attempt.Token);
                        ready &= reply[0] == 5 && reply[1] == (endpoint.Authentication ? 2 : 0);
                    }
                }
                catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) { ready = false; }
            }
            if ((ready || endpoints.Count == 0 && process.StartupReported) && !await process.HasExitedAsync(token))
            {
                if (++consecutive >= 2)
                {
                    if (Utils.IsNonWindows())
                    {
                        var snapshot = await UnixProcessControl.SnapshotAsync(token);
                        var core = process.CoreIdentity;
                        if (core == null || !snapshot.Any(p => p.Identity.Pid == core.Pid && p.Identity.StartTime == core.StartTime && !p.Zombie))
                            throw new IOException("启动进程身份无法核验 / Cannot verify the started core's identity.");
                        process.Remember(UnixProcessControl.Descendants(snapshot, process.KnownProcesses));
                    }
                    process.IsReady = true;
                    return;
                }
            }
            else consecutive = 0;
            await Task.Delay(50, token);
        }
    }
}
