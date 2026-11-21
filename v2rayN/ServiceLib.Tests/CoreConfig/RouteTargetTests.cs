using System.Net.Http;

namespace ServiceLib.Tests.CoreConfig;

[NotInParallel]
public class RouteTargetTests
{
    [Test]
    [Arguments(ECoreType.Xray, false, false)]
    [Arguments(ECoreType.sing_box, false, false)]
    [Arguments(ECoreType.Xray, true, false)]
    [Arguments(ECoreType.Xray, false, true)]
    public async Task LiveRuleUsesFastestAvailableExit(ECoreType core, bool fronted, bool mainUsesLeastLoad)
    {
        var binary = Environment.GetEnvironmentVariable(core == ECoreType.Xray ? "HANDOFF_TEST_XRAY" : "TUN_TEST_SINGBOX");
        if (string.IsNullOrEmpty(binary)) { Skip.Test("Set the local core executable to run the forwarding check."); return; }

        var singbox = Environment.GetEnvironmentVariable("TUN_TEST_SINGBOX");
        if (fronted && string.IsNullOrEmpty(singbox)) { Skip.Test("Set the sing-box executable for the dual-core check."); return; }
        using var defaultExit = new ProbeProxy("default", 0);
        using var first = new ProbeProxy("first", 5);
        using var second = new ProbeProxy("second", 180);
        var config = CoreConfigTestFactory.CreateConfig(core);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        config.Inbound[0].LocalPort = FreePort();
        config.SpeedTestItem.SpeedPingTestUrl = "http://127.0.0.1:18081/probe";
        var target = new RouteOutboundTarget { ProfileIds = ["first", "second"] };
        var key = target.Tag();
        var context = CoreConfigTestFactory.CreateContext(config, defaultExit.Node, core) with { RouteTargetBridgePort = fronted ? FreePort() : null };
        if (mainUsesLeastLoad)
        {
            var alternate = JsonUtils.DeepCopy(defaultExit.Node);
            alternate.IndexId = "other-default";
            context.AllProxiesMap[alternate.IndexId] = alternate;
            var group = CoreConfigTestFactory.CreatePolicyGroupNode(core, "main-group", "main", [defaultExit.Node.IndexId, alternate.IndexId]);
            group.SetProtocolExtra(group.GetProtocolExtra() with { MultipleLoad = EMultipleLoad.LeastLoad });
            context = context with { Node = group };
        }
        var clientPort = config.Inbound[0].LocalPort;
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new[] { new RulesItem
        {
            Port = "18082", InboundTag = fronted ? ["tun"] : null, OutboundTag = Global.BlockTag, OutboundTarget = target
        } });
        context.RouteTargets[key] = [first.Node, second.Node];
        var path = Path.Combine(Path.GetTempPath(), $"route-target-{Guid.NewGuid():N}.json");
        Process? front = null;
        var frontPath = path + ".front.json";
        try
        {
            var generated = await CoreConfigHandler.GenerateClientConfig(context, path);
            await generated.Success.Should().BeTrue();
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            if (core == ECoreType.Xray)
            {
                var balancer = json["routing"]!["balancers"]!.AsArray().Single(b => (string?)b!["tag"] == key + Global.BalancerTagSuffix)!;
                await balancer["strategy"]!["type"]!.GetValue<string>().Should().BeEqualTo("leastPing");
                await balancer["fallbackTag"]!.GetValue<string>().Should().BeEqualTo(Global.BlockTag);
                if (mainUsesLeastLoad)
                {
                    var burst = json["burstObservatory"]!;
                    await (json["observatory"] == null).Should().BeTrue();
                    await burst["subjectSelector"]!.AsArray().Select(v => (string?)v).ToHashSet().SetEquals([Global.ProxyTag, key]).Should().BeTrue();
                    await ((string?)burst["pingConfig"]!["interval"]).Should().BeEqualTo("3m");
                    burst["pingConfig"]!["interval"] = "1s";
                }
                else
                {
                    await json["observatory"]!["probeInterval"]!.GetValue<string>().Should().BeEqualTo("3m");
                    json["observatory"]!["probeInterval"] = "1s";
                }
            }
            else
            {
                var group = json["outbounds"]!.AsArray().Single(o => (string?)o!["type"] == "urltest")!;
                await group["tag"]!.GetValue<string>().Should().BeEqualTo(key);
                await group["interval"]!.GetValue<string>().Should().BeEqualTo("3m");
                await group["tolerance"]!.GetValue<int>().Should().BeEqualTo(1);
                group["interval"] = "1s";
            }
            // Accelerate the production probe cadence only for this isolated process.
            await File.WriteAllTextAsync(path, json.ToJsonString());
            if (fronted)
            {
                // Exercise the real front-to-main handoff using an unprivileged mixed test listener.
                var frontConfig = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
                clientPort = frontConfig.Inbound[0].LocalPort = FreePort();
                var frontContext = CoreConfigTestFactory.CreateContext(frontConfig, defaultExit.Node, ECoreType.sing_box)
                    with { RouteTargets = context.RouteTargets, RouteTargetBridgePort = context.RouteTargetBridgePort };
                frontContext.RoutingItem!.RuleSet = JsonUtils.Serialize(new[] { new RulesItem { Port = "18082",
                    OutboundTag = Global.BlockTag, OutboundTarget = target } });
                var frontResult = await CoreConfigHandler.GenerateClientConfig(frontContext, frontPath);
                await frontResult.Success.Should().BeTrue();
                var start = new ProcessStartInfo(singbox!) { UseShellExecute = false };
                foreach (var arg in new[] { "run", "-c", frontPath }) start.ArgumentList.Add(arg);
                front = Process.Start(start);
            }
            var info = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "run", "-c", path }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            try
            {
                using var http = new HttpClient(new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://127.0.0.1:{clientPort}"),
                    PooledConnectionLifetime = TimeSpan.Zero
                }) { Timeout = TimeSpan.FromSeconds(2) };
                await ExpectExit("first");
                using var held = await http.GetAsync("http://127.0.0.1:18082/hold", HttpCompletionOption.ResponseHeadersRead);
                held.EnsureSuccessStatusCode();
                using var heldStream = await held.Content.ReadAsStreamAsync();
                var firstBytes = new byte["first".Length];
                await heldStream.ReadExactlyAsync(firstBytes);
                await Encoding.ASCII.GetString(firstBytes).Should().BeEqualTo("first");
                second.Delay = 3;
                first.Delay = 190;
                await ExpectExit("second");
                first.FinishHeldRequest.TrySetResult();
                await heldStream.ReadExactlyAsync(firstBytes).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await Encoding.ASCII.GetString(firstBytes).Should().BeEqualTo("first");
                second.Available = false;
                await ExpectExit("first");
                second.Available = true;
                await ExpectExit("second");
                first.Available = second.Available = false;
                await Task.Delay(TimeSpan.FromSeconds(3));
                try
                {
                    var escaped = await http.GetStringAsync("http://127.0.0.1:18082/exit");
                    throw new InvalidOperationException("Unexpected exit with both candidates offline: " + escaped);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }

                async Task ExpectExit(string expected)
                {
                    string last = "";
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(30))
                    {
                        if (process.HasExited) throw new InvalidOperationException(await errors + await output);
                        try { last = await http.GetStringAsync("http://127.0.0.1:18082/exit"); }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { last = ex.Message; }
                        if (last == expected) return;
                        await Task.Delay(175);
                    }
                    throw new TimeoutException($"Expected {expected}, last response {last}. Probe counts {first.Probes}/{second.Probes}.");
                }
            }
            finally
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
                await Task.WhenAll(errors, output);
            }
        }
        finally
        {
            if (front != null)
            {
                if (!front.HasExited) front.Kill();
                await front.WaitForExitAsync();
                front.Dispose();
            }
            File.Delete(frontPath);
            File.Delete(path);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // Each loopback SOCKS5 exit serves a separate probe and an identifiable HTTP response.
    private sealed class ProbeProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public volatile int Delay;
        public volatile bool Available = true;
        public int Probes;
        public readonly TaskCompletionSource FinishHeldRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ProfileItem Node;

        public ProbeProxy(string name, int delay)
        {
            Delay = delay;
            _listener.Start();
            Node = new ProfileItem { IndexId = name, Remarks = name, Address = "127.0.0.1",
                ConfigType = EConfigType.SOCKS, Port = ((IPEndPoint)_listener.LocalEndpoint).Port };
            _ = AcceptAsync(_stop.Token);
        }

        private async Task AcceptAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                    _ = ServeAsync(await _listener.AcceptTcpClientAsync(token), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (SocketException) when (token.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            try
            {
                if (!Available) return;
                using var stream = client.GetStream();
                var header = new byte[4];
                await stream.ReadExactlyAsync(header.AsMemory(0, 2), token);
                await stream.ReadExactlyAsync(new byte[header[1]], token);
                await stream.WriteAsync(new byte[] { 5, 0 }, token);
                await stream.ReadExactlyAsync(header, token);
                var length = header[3] switch { 1 => 4, 4 => 16, 3 => stream.ReadByte(), _ => 0 };
                if (length <= 0) return;
                await stream.ReadExactlyAsync(new byte[length + 2], token);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
                using var reader = new StreamReader(stream, leaveOpen: true);
                var request = await reader.ReadLineAsync(token);
                if (request == null) return;
                while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
                var probe = request.Contains("/probe");
                if (probe) { Interlocked.Increment(ref Probes); await Task.Delay(Delay, token); }
                var hold = request.Contains("/hold");
                var body = probe ? "" : Node.IndexId;
                var response = $"HTTP/1.1 {(probe ? "204 No Content" : "200 OK")}\r\nContent-Length: {body.Length * (hold ? 2 : 1)}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
                if (hold)
                {
                    await FinishHeldRequest.Task.WaitAsync(token);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(body), token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }

        public void Dispose() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); }
    }
}
