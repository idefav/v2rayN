using System.Net.Http;

namespace ServiceLib.Tests.CoreConfig;

[NotInParallel]
public class RoutingCoreIntegrationTests
{
    [Test]
    [Arguments(ECoreType.sing_box, EConfigType.VLESS, "socks5", false, true)]
    [Arguments(ECoreType.Xray, EConfigType.VLESS, "socks5", false, true)]
    [Arguments(ECoreType.Xray, EConfigType.Shadowsocks, "socks5", false, true)]
    [Arguments(ECoreType.Xray, EConfigType.VLESS, "http", false, true)]
    [Arguments(ECoreType.Xray, EConfigType.Shadowsocks, "http", false, true)]
    [Arguments(ECoreType.Xray, EConfigType.VLESS, "socks5", true, false)]
    [Arguments(ECoreType.Xray, EConfigType.Shadowsocks, "http", true, false)]
    [Arguments(ECoreType.Xray, EConfigType.VLESS, "http", true, true)]
    [Arguments(ECoreType.Xray, EConfigType.Shadowsocks, "socks5", true, true)]
    public async Task GeneratedCoreConfigProxiesARequest(ECoreType core, EConfigType protocol, string proxyScheme, bool extraInbounds, bool statistics)
    {
        var binary = Environment.GetEnvironmentVariable("TUN_TEST_SINGBOX");
        if (string.IsNullOrEmpty(binary)) { Skip.Test("Set TUN_TEST_SINGBOX for isolated VLESS test."); return; }
        var clientBinary = core == ECoreType.Xray ? Environment.GetEnvironmentVariable("HANDOFF_TEST_XRAY") : binary;
        if (string.IsNullOrEmpty(clientBinary)) { Skip.Test("Set HANDOFF_TEST_XRAY"); return; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = new TcpListener(IPAddress.Loopback, 0);
        http.Start();
        var httpPort = ((IPEndPoint)http.LocalEndpoint).Port;
        var serverPort = FreePort();
        var clientPort = FreePort();
        var directory = Path.Combine(Path.GetTempPath(), "routing-core-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? server = null;
        var manager = new CoreManager(_ => (clientBinary, ""));
        var formal = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        var before = File.Exists(formal) ? await File.ReadAllBytesAsync(formal) : null;
        try
        {
            var config = CoreConfigTestFactory.CreateConfig(core);
            CoreConfigTestFactory.BindAppManagerConfig(config);
            config.Inbound[0].LocalPort = clientPort;
            config.GuiItem.EnableStatistics = statistics;
            config.Inbound[0].SecondLocalPortEnabled = extraInbounds;
            config.Inbound[0].AllowLANConn = extraInbounds;
            config.Inbound[0].NewPort4LAN = extraInbounds;
            config.Inbound[0].User = "fixture";
            config.Inbound[0].Pass = "fixture-password";
            var node = CoreConfigTestFactory.CreateVmessNode(core);
            node.ConfigType = protocol;
            if (protocol == EConfigType.VLESS) node.SetProtocolExtra(new() { VlessEncryption = "none" });
            if (protocol == EConfigType.Shadowsocks) node.SetProtocolExtra(new() { SsMethod = "aes-128-gcm" });
            node.StreamSecurity = "";
            node.Address = "127.0.0.1";
            node.Port = serverPort;
            var routing = new RoutingItem { RuleSet = JsonUtils.Serialize(new[]
            {
                new RulesItem { Remarks = "protocol fixture", Protocol = [core == ECoreType.sing_box ? "stun" : "tls"], OutboundTag = Global.ProxyTag }
            }) };
            var statisticsPort = FreePort();
            var inboundPorts = new[] { EInboundProtocol.socks, EInboundProtocol.socks2, EInboundProtocol.socks3 }
                .Select(p => AppManager.GetLocalPort(config, p)).ToArray();
            while (inboundPorts.Contains(statisticsPort)) statisticsPort = FreePort();
            var context = CoreConfigTestFactory.CreateContext(config, node, core) with { RoutingItem = routing, StatisticsPort = statisticsPort };
            var clientPath = Path.Combine(directory, "client.json");
            var result = await CoreConfigHandler.GenerateClientConfig(context, clientPath);
            await result.Success.Should().BeTrue();
            var generated = await File.ReadAllTextAsync(clientPath);
            if (core == ECoreType.Xray)
                await JsonNode.Parse(generated)!["inbounds"]!.AsArray()
                    .Any(i => i?["protocol"]?.GetValue<string>() == "mixed").Should().BeTrue();
            var listeners = CoreListenerCheck.Read(generated, core);
            await CoreListenerCheck.EnsureAvailableAsync(listeners, token: deadline.Token);
            await (await RoutingCoreCompatibility.CheckExecutableAsync(clientBinary, core, clientPath,
                CoreInfoManager.Instance.GetCoreInfo(core)!.Environment, deadline.Token)).Should().BeNull();
            var serverInbound = new JsonObject { ["type"] = protocol == EConfigType.Shadowsocks ? "shadowsocks" : "vless",
                ["tag"] = "fixture", ["listen"] = "127.0.0.1", ["listen_port"] = serverPort };
            if (protocol == EConfigType.Shadowsocks) { serverInbound["method"] = "aes-128-gcm"; serverInbound["password"] = node.Password; }
            else serverInbound["users"] = new JsonArray(new JsonObject { ["uuid"] = node.Password });

            var serverPath = Path.Combine(directory, "server.json");
            // Only the VLESS server can route this synthetic destination to the local HTTP fixture.
            await File.WriteAllTextAsync(serverPath, JsonSerializer.Serialize(new
            {
                log = new { disabled = true },
                inbounds = new[] { serverInbound },
                outbounds = new[] { new { type = "direct", tag = "direct" } },
                route = new { rules = new[] { new { inbound = new[] { "fixture" }, action = "route", outbound = "direct",
                    override_address = "127.0.0.1", override_port = httpPort } } }
            }), deadline.Token);
            await (await RoutingCoreCompatibility.CheckExecutableAsync(binary, ECoreType.sing_box, serverPath,
                CoreInfoManager.Instance.GetCoreInfo(ECoreType.sing_box)!.Environment, deadline.Token)).Should().BeNull();
            server = Start(binary, serverPath);
            while (true)
            {
                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, serverPort, deadline.Token);
                    break;
                }
                catch (SocketException) { await Task.Delay(50, deadline.Token); }
            }
            var outcome = await manager.LoadCore(context, null, token: deadline.Token);
            if (outcome != CoreLoadResult.Started) throw new Exception(manager.LastError);
            await (await manager.GetRuntimeStateAsync()).Ready.Should().BeTrue();
            if (core == ECoreType.Xray)
            {
                using var udpAssociation = new TcpClient();
                await udpAssociation.ConnectAsync(IPAddress.Loopback, clientPort, deadline.Token);
                var stream = udpAssociation.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, deadline.Token);
                var selected = new byte[2]; await stream.ReadExactlyAsync(selected, deadline.Token);
                await selected.SequenceEqual(new byte[] { 5, 0 }).Should().BeTrue();
                await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, deadline.Token);
                var header = new byte[4]; await stream.ReadExactlyAsync(header, deadline.Token);
                await header[1].Should().BeEqualTo((byte)0);
                var size = header[3] switch { 1 => 4, 4 => 16, _ => throw new Exception("Invalid UDP relay address type") };
                var address = new byte[size + 2]; await stream.ReadExactlyAsync(address, deadline.Token);
                await (address[^2] * 256 + address[^1] > 0).Should().BeTrue();
            }
            // Production coordinator, native validation, listener ownership and metrics readiness
            // must all succeed before either SOCKS or HTTP traffic is tested.
            var response = Task.Run(async () =>
            {
                using var accepted = await http.AcceptTcpClientAsync(deadline.Token);
                var stream = accepted.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 16\r\nConnection: close\r\n\r\nrouting-core-ok!"), deadline.Token);
            }, deadline.Token);
            using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"{proxyScheme}://127.0.0.1:{clientPort}"), UseProxy = true };
            using var request = new HttpClient(handler);
            var body = await request.GetStringAsync("http://198.18.0.1:18080/check", deadline.Token);
            await body.Should().BeEqualTo("routing-core-ok!");
            await response;
        }
        finally
        {
            deadline.Cancel();
            await manager.CoreStop();
            if (before == null) File.Delete(formal); else await File.WriteAllBytesAsync(formal, before);
            foreach (var process in new[] { server })
            {
                if (process == null) continue;
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
                process.Dispose();
            }
            Directory.Delete(directory, true);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Process Start(string binary, string path, ECoreType core = ECoreType.sing_box)
    {
        var info = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", "-c", path }) info.ArgumentList.Add(arg);
        foreach (var item in CoreInfoManager.Instance.GetCoreInfo(core)!.Environment) info.Environment[item.Key] = item.Value;
        var process = Process.Start(info)!;
        process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
        process.OutputDataReceived += (_, _) => { }; process.BeginOutputReadLine();
        return process;
    }
}
