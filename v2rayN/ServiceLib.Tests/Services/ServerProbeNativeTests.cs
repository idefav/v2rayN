using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class ServerProbeNativeTests
{
    [Test]
    [Arguments(EConfigType.VLESS)]
    [Arguments(EConfigType.Shadowsocks)]
    public async Task RealIsolatedCoreMeasuresHttpWithoutChangingConnection(EConfigType protocol)
    {
        var binary = Environment.GetEnvironmentVariable("HANDOFF_TEST_XRAY");
        if (string.IsNullOrEmpty(binary)) { Skip.Test("Requires bundled Xray fixture"); return; }
        var config = CoreConfigTestFactory.CreateConfig();
        config.IndexId = "untouched-running-profile";
        config.CoreTypeItem = [new() { ConfigType = protocol, CoreType = ECoreType.Xray }];
        CoreConfigTestFactory.BindAppManagerConfig(config);
        using var http = new TcpListener(IPAddress.Loopback, 0); http.Start();
        var httpPort = ((IPEndPoint)http.LocalEndpoint).Port;
        config.SpeedTestItem.SpeedPingTestUrl = $"http://127.0.0.1:{httpPort}/probe";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reply = Task.Run(async () =>
        {
            using var client = await http.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
        });
        using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
        var password = protocol == EConfigType.VLESS ? Guid.NewGuid().ToString() : "probe-fixture";
        var settings = protocol == EConfigType.VLESS
            ? new JsonObject { ["clients"] = new JsonArray(new JsonObject { ["id"] = password }), ["decryption"] = "none" }
            : new JsonObject { ["method"] = "aes-128-gcm", ["password"] = password, ["network"] = "tcp" };
        var server = new JsonObject
        {
            ["inbounds"] = new JsonArray(new JsonObject { ["listen"] = "127.0.0.1", ["port"] = port,
                ["protocol"] = protocol == EConfigType.VLESS ? "vless" : "shadowsocks", ["settings"] = settings }),
            // Bundled Xray blocks server-side loopback destinations by default.
            // Allow only this fixture's random HTTP endpoint, never change a real server.
            ["outbounds"] = new JsonArray(new JsonObject { ["protocol"] = "freedom",
                ["settings"] = new JsonObject { ["finalRules"] = new JsonArray(new JsonObject {
                    ["action"] = "allow", ["network"] = "tcp", ["ip"] = new JsonArray("127.0.0.1"), ["port"] = httpPort }) } })
        };
        var directory = Path.Combine(Path.GetTempPath(), "node-probe-server-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "server.json");
        await File.WriteAllTextAsync(path, server.ToJsonString());
        var info = CoreInfoManager.Instance.GetCoreInfo(ECoreType.Xray)!;
        var original = info.CoreExes;
        info.CoreExes = [binary];
        await CoreInfoManager.Instance.GetCoreExecFile(info, out _).Should().BeEqualTo(binary);
        Process? process = null;
        try
        {
            reserve.Stop();
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            while (true)
            {
                try { using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token); break; }
                catch (SocketException) { await Task.Delay(50, timeout.Token); }
            }
            var node = new ProfileItem { IndexId = "native-probe", Address = "127.0.0.1", Port = port,
                Password = password, ConfigType = protocol, CoreType = ECoreType.Xray };
            node.SetProtocolExtra(new() { SsMethod = "aes-128-gcm", VlessEncryption = "none" });
            var result = await new ServerProbeService().TestAsync(node, config, Guid.NewGuid(), timeout.Token);
            if (result.Proxy.State != ServerProbeState.Responded)
            {
                process.Kill(true);
                await process.WaitForExitAsync(timeout.Token);
                Console.WriteLine("Fixture server: " + await output + await errors);
                Console.WriteLine("HTTP fixture task: " + reply.Status);
            }
            await result.Tcp.State.Should().BeEqualTo(ServerProbeState.Passed);
            await result.Proxy.State.Should().BeEqualTo(ServerProbeState.Responded);
            await result.Proxy.HttpStatus.Should().BeEqualTo(403);
            await config.IndexId.Should().BeEqualTo("untouched-running-profile");
            await reply;
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, errors);
        }
        finally
        {
            timeout.Cancel();
            if (process is { HasExited: false }) { process.Kill(true); await process.WaitForExitAsync(); }
            process?.Dispose();
            info.CoreExes = original;
            Directory.Delete(directory, true);
        }
    }
}
