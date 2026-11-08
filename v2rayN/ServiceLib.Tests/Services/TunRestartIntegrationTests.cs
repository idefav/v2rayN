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
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
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
            if (preSocks)
            {
                plain = new ProcessService(binary, "", directory, true, false, null, null);
                plain.SetArguments(["run", "-c", plainPath, "--disable-color"]);
                await plain.StartAsync();
                await CoreListenerCheck.WaitForReadyAsync(plain, CoreListenerCheck.Read(plainJson), deadline.Token);
            }
            await CoreAdminManager.Instance.Init(new Config(), null);
            for (var round = 0; round < 3; round++)
            {
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
                if (!(await Route(destination, deadline.Token)).Contains("utun")) throw new Exception("TUN route did not become ready");
                var result = await elevated.StopAndWaitAsync();
                if (!result.Success) throw new Exception(result.Error);
                var remaining = await UnixProcessControl.ReadMacProcessAsync(identity.Pid, deadline.Token);
                if (remaining != null && !remaining.Zombie && remaining.Identity.StartTime == identity.StartTime)
                    throw new Exception("Root core still alive after stop");
                await CoreListenerCheck.EnsureAvailableAsync(CoreListenerCheck.Read(tun.ToJsonString()));
                elevated.Dispose(); elevated = null;
                if (await Route(destination, deadline.Token) != routeBefore) throw new Exception("isolated TUN route was not restored");
            }
        }
        finally
        {
            if (elevated != null) { await elevated.StopAsync(); elevated.Dispose(); }
            if (plain != null) { await plain.StopAsync(); plain.Dispose(); }
            if (ProcessService.HasUnstoppedConfig(tunPath)) throw new Exception("Fixture stop failed; retained config: " + directory);
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
}
