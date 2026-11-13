using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class TunRestartIntegrationTests
{
    // Explicitly opt-in: creates only an isolated TUN route, never the default route
    // or a system resolver. Requires sudo authorization already available to this process.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ElevatedTunRestartsPreserveDefaultRouteAndDns(bool preSocks)
    {
        var binary = Environment.GetEnvironmentVariable("SSH_TEST_SINGBOX");
        if (Environment.GetEnvironmentVariable("V2RAYN_TEST_TUN_RESTART") != "1")
        { Skip.Test("Opt-in macOS TUN fixture: V2RAYN_TEST_TUN_RESTART=1 and SSH_TEST_SINGBOX; existing sudo authorization required"); return; }
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(binary) || !File.Exists(binary))
            throw new Exception("Explicit TUN verification requires macOS and a real sing-box binary");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var sudo = await Cli.Wrap("/usr/bin/sudo").WithArguments(["-n", "true"]).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync(deadline.Token);
        if (sudo.ExitCode != 0) throw new Exception("Required TUN verification has no sudo authorization");
        const string destination = "198.18.77.2";
        var routeBefore = await Route(destination, deadline.Token);
        if (routeBefore.Contains("utun", StringComparison.Ordinal) || !Regex.IsMatch(routeBefore, @"destination:\s+default"))
            throw new Exception("Isolated test prefix already uses a specific/VPN route; refusing to overlay it");
        var defaultBefore = await Route("1.1.1.1", deadline.Token);
        var dnsBefore = (await Cli.Wrap("/usr/sbin/scutil").WithArguments(["--dns"]).ExecuteBufferedAsync(deadline.Token)).StandardOutput;
        var proxyBefore = (await Cli.Wrap("/usr/sbin/scutil").WithArguments(["--proxy"]).ExecuteBufferedAsync(deadline.Token)).StandardOutput;
        var directory = Path.Combine(Path.GetTempPath(), "tun-restart-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        using var first = new TcpListener(IPAddress.Loopback, 0); first.Start();
        using var second = new TcpListener(IPAddress.Loopback, 0); second.Start();
        var proxyPort = ((IPEndPoint)first.LocalEndpoint).Port;
        var apiPort = ((IPEndPoint)second.LocalEndpoint).Port;
        first.Stop(); second.Stop();
        var plainJson = $$"""{"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":{{proxyPort}}}],"outbounds":[{"type":"direct","tag":"direct"}]}""";
        var tun = JsonNode.Parse(plainJson)!.AsObject();
        if (preSocks) tun["inbounds"] = new JsonArray();
        tun["inbounds"]!.AsArray().Add(JsonNode.Parse("""
            {"type":"tun","address":["172.31.252.1/30"],"auto_route":true,"strict_route":true,
             "dns_mode":"disabled","route_address":["198.18.77.0/30"],"stack":"gvisor"}
            """));
        tun["experimental"] = new JsonObject { ["clash_api"] = new JsonObject { ["external_controller"] = $"127.0.0.1:{apiPort}" } };
        tun["route"] = JsonNode.Parse("""{"auto_detect_interface":true,"final":"direct"}""");
        if (preSocks)
        {
            tun["outbounds"]!.AsArray().Add(JsonNode.Parse($$"""{"type":"socks","tag":"proxy","server":"127.0.0.1","server_port":{{proxyPort}},"version":"5"}"""));
            tun["route"]!["final"] = "proxy";
        }
        var plainPath = Path.Combine(directory, "plain.json");
        var tunPath = Path.Combine(directory, "tun.json");
        await File.WriteAllTextAsync(plainPath, plainJson);
        await File.WriteAllTextAsync(tunPath, tun.ToJsonString());
        ProcessService? plain = null;
        ProcessService? elevated = null;
        try
        {
            await CoreAdminManager.Instance.Init(new Config(), null);
            for (var round = 0; round < 3; round++)
            {
                // Begin every round in ordinary mode, including direct TUN topology.
                {
                    plain = new ProcessService(binary, "", directory, true, false, null, null);
                    plain.SetArguments(["run", "-c", plainPath, "--disable-color"]);
                    await plain.StartAsync();
                    await CoreListenerCheck.WaitForReadyAsync(plain, CoreListenerCheck.Read(plainJson), deadline.Token);
                    if (await RecoveryProbe.CheckSocksAsync(proxyPort, deadline.Token) != LocalHealth.Healthy) throw new Exception("ordinary SOCKS unavailable");
                    if (!preSocks)
                    {
                        if (!(await plain.StopAndWaitAsync()).Success) throw new Exception("ordinary stop failed");
                        plain.Dispose(); plain = null;
                    }
                }
                await CoreListenerCheck.EnsureAvailableAsync(CoreListenerCheck.Read(tun.ToJsonString()));
                elevated = await CoreAdminManager.Instance.RunProcessAsLinuxSudo(binary,
                    CoreInfoManager.Instance.GetCoreInfo(ECoreType.sing_box)!, tunPath, p => elevated = p);
                using var start = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await CoreListenerCheck.WaitForReadyAsync(elevated, CoreListenerCheck.Read(tun.ToJsonString()), start.Token);
                if (elevated.CoreIdentity == null || elevated.CoreIdentity.Pid == elevated.Id)
                    throw new Exception("elevated child identity was not reported independently of sudo");
                var uid = await Cli.Wrap("/bin/ps").WithArguments(["-p", elevated.CoreIdentity.Pid.ToString(), "-o", "uid="]).ExecuteBufferedAsync(deadline.Token);
                if (uid.StandardOutput.Trim() != "0" || await elevated.HasExitedAsync(deadline.Token))
                    throw new Exception("Root core identity was not verified as alive");
                var identity = elevated.CoreIdentity;
                await ProxyTraffic(proxyPort, deadline.Token);
                // Exercise a real root-owned accepted TCP connection before shutdown.
                using (var http = new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler { UseProxy = false }))
                {
                    http.DefaultRequestHeaders.ConnectionClose = true;
                    using var response = await http.GetAsync($"http://127.0.0.1:{apiPort}/version", deadline.Token);
                    response.EnsureSuccessStatusCode();
                }
                if (!(await Route(destination, deadline.Token)).Contains("utun")) throw new Exception("TUN route did not become ready");
                var result = await elevated.StopAndWaitAsync();
                if (!result.Success) throw new Exception(result.Error);
                var remaining = await UnixProcessControl.ReadMacProcessAsync(identity.Pid, deadline.Token);
                if (remaining != null && !remaining.Zombie && remaining.Identity.StartTime == identity.StartTime)
                    throw new Exception("Root core still alive after stop");
                await PortHandoff.WaitAsync(CoreListenerCheck.Read(tun.ToJsonString()), deadline.Token);
                elevated.Dispose(); elevated = null;
                if (plain != null)
                {
                    var plainResult = await plain.StopAndWaitAsync();
                    if (!plainResult.Success) throw new Exception(plainResult.Error);
                    plain.Dispose(); plain = null;
                    await CoreListenerCheck.EnsureAvailableAsync(CoreListenerCheck.Read(plainJson));
                }
                if (await Route(destination, deadline.Token) != routeBefore) throw new Exception("isolated TUN route was not restored");
                await PortHandoff.WaitAsync(CoreListenerCheck.Read(plainJson), deadline.Token);
                plain = new ProcessService(binary, "", directory, true, false, null, null);
                plain.SetArguments(["run", "-c", plainPath, "--disable-color"]);
                await plain.StartAsync();
                await CoreListenerCheck.WaitForReadyAsync(plain, CoreListenerCheck.Read(plainJson), deadline.Token);
                var plainUid = await Cli.Wrap("/bin/ps").WithArguments(["-p", plain.Id.ToString(), "-o", "uid="]).ExecuteBufferedAsync(deadline.Token);
                if (plainUid.StandardOutput.Trim() == "0" || await RecoveryProbe.CheckSocksAsync(proxyPort, deadline.Token) != LocalHealth.Healthy)
                    throw new Exception("ordinary handoff did not restore non-root SOCKS");
                await ProxyTraffic(proxyPort, deadline.Token);
                if (!(await plain.StopAndWaitAsync()).Success) throw new Exception("ordinary cleanup failed");
                plain.Dispose(); plain = null;
            }
        }
        finally
        {
            if (elevated != null) { await elevated.StopAsync(); elevated.Dispose(); }
            if (plain != null) { await plain.StopAsync(); plain.Dispose(); }
            if (ProcessService.HasUnstoppedConfig(tunPath) || ProcessService.HasUnstoppedConfig(plainPath))
                throw new Exception("Fixture stop failed; retained config: " + directory);
            Directory.Delete(directory, true);
            if (await Route("1.1.1.1", deadline.Token) != defaultBefore) throw new Exception("default route changed");
            if ((await Cli.Wrap("/usr/sbin/scutil").WithArguments(["--dns"]).ExecuteBufferedAsync(deadline.Token)).StandardOutput != dnsBefore)
                throw new Exception("system DNS changed");
            if ((await Cli.Wrap("/usr/sbin/scutil").WithArguments(["--proxy"]).ExecuteBufferedAsync(deadline.Token)).StandardOutput != proxyBefore)
                throw new Exception("system proxy changed");
        }
    }

    private static async Task<string> Route(string destination, CancellationToken token)
    {
        var text = (await Cli.Wrap("/sbin/route").WithArguments(["-n", "get", destination]).ExecuteBufferedAsync(token)).StandardOutput;
        return string.Join("\n", text.Split('\n').Where(l => Regex.IsMatch(l, @"^\s*(destination|mask|gateway|interface):")));
    }

    private static async Task ProxyTraffic(int proxyPort, CancellationToken token)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var destination = ((IPEndPoint)server.LocalEndpoint).Port;
        var reply = Task.Run(async () =>
        {
            using var peer = await server.AcceptTcpClientAsync(token);
            using var stream = peer.GetStream();
            var buffer = new byte[4096];
            var header = new StringBuilder();
            while (!header.ToString().Contains("\r\n\r\n"))
            {
                var count = await stream.ReadAsync(buffer, token);
                if (count == 0) throw new IOException("proxy request ended early");
                header.Append(Encoding.ASCII.GetString(buffer, 0, count));
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"), token);
        }, token);
        using var http = new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler
        {
            UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}", false)
        });
        http.DefaultRequestHeaders.ConnectionClose = true;
        if (await http.GetStringAsync($"http://127.0.0.1:{destination}/handoff", token) != "OK")
            throw new Exception("real proxy traffic failed");
        await reply;
    }
}
