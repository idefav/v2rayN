using ServiceLib.Tests.CoreConfig;
using ReactiveUI.Primitives;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class XrayHandoffTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task MixedHasSocksReadinessAndDynamicUdp(bool udp, bool auth)
    {
        var json = JsonSerializer.Serialize(new { inbounds = new[] { new {
            protocol = "mixed", listen = "::1", port = 10808,
            settings = new { udp, auth = auth ? "password" : "noauth" }
        } } });
        var listeners = CoreListenerCheck.Read(json, ECoreType.Xray);
        await listeners.Should().HaveCount(1);
        await listeners[0].Socks.Should().BeTrue();
        await listeners[0].SocksUdp.Should().BeEqualTo(udp);
        await listeners[0].Authentication.Should().BeEqualTo(auth);
        await listeners[0].Protocol.Should().BeEqualTo(ProtocolType.Tcp);
    }
    [Test]
    public async Task FrontTunDoesNotHideUnprivilegedXrayHandoffAndReverseDirectionWorks()
    {
        var endpoint = new CoreListener(IPAddress.Loopback, 10808, ProtocolType.Tcp);
        var api = new CoreListener(IPAddress.Loopback, 9090, ProtocolType.Tcp);
        var main = new CoreConfigContext { Node = new ProfileItem(), RunCoreType = ECoreType.Xray, IsTunEnabled = false };
        var pre = new CoreConfigContext { Node = new ProfileItem(), RunCoreType = ECoreType.sing_box, IsTunEnabled = true };
        var selected = CoreManager.SelectHandoffTargets(main, pre, [endpoint], [api], [endpoint, api]);
        await selected.SequenceEqual([endpoint]).Should().BeTrue();
        await CoreManager.SelectHandoffTargets(main with { RunCoreType = ECoreType.sing_box }, null, [endpoint], [], [endpoint]).Should().HaveCount(1);
        await CoreManager.SelectHandoffTargets(pre, null, [endpoint], [], [endpoint]).Should().HaveCount(0);
    }

    [Test]
    public async Task ParsesXrayProxyApiIpv6AndUdpListeners()
    {
        var endpoints = CoreListenerCheck.Read("""
        {"inbounds":[
          {"protocol":"socks","listen":"127.0.0.1","port":10808,"settings":{"udp":true,"auth":"password"}},
          {"protocol":"http","listen":"::1","port":"10809"},
          {"protocol":"dokodemo-door","listen":"127.0.0.1","port":10810,"settings":{"network":"tcp"}},
          {"protocol":"socks","port":10811,"settings":{"udp":false}},
          {"protocol":"dokodemo-door","listen":"::","port":"12000-12001","settings":{"network":"udp"}}
        ]}
        """, ECoreType.Xray);
        await endpoints.Should().HaveCount(6);
        await endpoints.Single(e => e.Port == 10808).SocksUdp.Should().BeTrue();
        await endpoints.Single(e => e.Port == 10811).SocksUdp.Should().BeFalse();
        await endpoints.Single(e => e.Port == 10808 && e.Protocol == ProtocolType.Tcp).Authentication.Should().BeTrue();
        await endpoints.Single(e => e.Port == 10809).Address.Should().BeEqualTo(IPAddress.IPv6Loopback);
        await endpoints.Single(e => e.Port == 10811).Address.Should().BeEqualTo(IPAddress.Any);
        await endpoints.Count(e => e.Protocol == ProtocolType.Udp).Should().BeEqualTo(2);
    }

    [Test]
    [Arguments("{\"protocol\":\"socks\",\"port\":0}")]
    [Arguments("{\"protocol\":\"socks\",\"port\":65536}")]
    [Arguments("{\"protocol\":\"socks\",\"port\":\"3-1\"}")]
    [Arguments("{\"protocol\":\"socks\",\"port\":10000,\"listen\":\"/tmp/socket\"}")]
    [Arguments("{\"protocol\":\"socks\"}")]
    public void InvalidXrayListenerCannotBeSilentlySkipped(string inbound)
    {
        try { CoreListenerCheck.Read("{\"inbounds\":[" + inbound + "]}", ECoreType.Xray); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Invalid inbound accepted");
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    [Arguments(false, false, true)]
    public async Task XrayWaitsBeforeLaunchAndPreservesFormalConfigOnCancellation(bool disconnectFirst, bool cancel, bool withPre)
    {
        if (!OperatingSystem.IsMacOS()) { Skip.Test("macOS handoff policy fixture"); return; }
        var binary = Environment.GetEnvironmentVariable("HANDOFF_TEST_XRAY");
        if (string.IsNullOrEmpty(binary)) { Skip.Test("Set HANDOFF_TEST_XRAY"); return; }
        var singbox = Environment.GetEnvironmentVariable("TUN_TEST_SINGBOX");
        if (withPre && string.IsNullOrEmpty(singbox)) { Skip.Test("Set TUN_TEST_SINGBOX"); return; }
        var config = CoreConfigTestFactory.CreateConfig(); CoreConfigTestFactory.BindAppManagerConfig(config);
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        reserved.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        var port = ((IPEndPoint)reserved.LocalEndPoint!).Port;
        var directory = Path.Combine(Path.GetTempPath(), "xray-handoff-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "candidate.json");
        var oldConfig = Path.Combine(directory, "old.json");
        var formal = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        var before = File.Exists(formal) ? await File.ReadAllBytesAsync(formal) : null;
        var preFormal = Utils.GetBinConfigPath(Global.CorePreConfigFileName);
        var preBefore = File.Exists(preFormal) ? await File.ReadAllBytesAsync(preFormal) : null;
        var launches = 0;
        var manager = new CoreManager(info => (info?.CoreType == ECoreType.sing_box ? singbox! : binary, ""))
        { ProcessStarted = _ => launches++ };
        using var old = new ProcessService("/bin/sleep", "30", directory, false, false, null, null);
        await old.StartAsync();
        using (var process = Process.GetProcessById(old.Id)) { process.Kill(); await process.WaitForExitAsync(); }
        old.IsElevated = true; old.ConfigPath = oldConfig;
        typeof(CoreManager).GetField("_processService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(manager, old);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await File.WriteAllTextAsync(oldConfig, $$"""{"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":{{port}}}]}""");
            await File.WriteAllTextAsync(source, $$$"""{"inbounds":[{"protocol":"mixed","listen":"127.0.0.1","port":{{{port}}},"settings":{"udp":true}}],"outbounds":[{"protocol":"freedom"}]}""");
            if (disconnectFirst) await manager.CoreStop();
            var context = new CoreConfigContext { Node = new ProfileItem { ConfigType = EConfigType.Custom, CoreType = ECoreType.Xray, Address = source }, RunCoreType = ECoreType.Xray, AppConfig = config };
            CoreConfigContext? preContext = null;
            if (withPre)
            {
                using var prePort = new TcpListener(IPAddress.Loopback, 0); prePort.Start();
                var number = ((IPEndPoint)prePort.LocalEndpoint).Port; prePort.Stop();
                var path = Path.Combine(directory, "pre.json");
                await File.WriteAllTextAsync(path, $$"""{"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":{{number}}}],"outbounds":[{"type":"direct"}]}""");
                preContext = new CoreConfigContext { Node = new ProfileItem { ConfigType = EConfigType.Custom, CoreType = ECoreType.sing_box, Address = path }, RunCoreType = ECoreType.sing_box, AppConfig = config };
            }
            var loading = manager.LoadCore(context, preContext, token: cancellation.Token,
                stageChanged: (stage, _) => { if (stage == ConnectionStage.Waiting) waiting.TrySetResult(); });
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await launches.Should().BeEqualTo(0);
            await loading.IsCompleted.Should().BeFalse();
            await (File.Exists(formal) ? Convert.ToHexString(await File.ReadAllBytesAsync(formal)) : "").Should()
                .BeEqualTo(before == null ? "" : Convert.ToHexString(before));
            if (cancel) cancellation.Cancel(); else reserved.Close();
            var outcome = await loading;
            await outcome.Should().BeEqualTo(cancel ? CoreLoadResult.Failed : CoreLoadResult.Started);
            await launches.Should().BeEqualTo(cancel ? 0 : withPre ? 2 : 1);
            if (cancel)
            {
                var retryWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var retry = manager.LoadCore(context, null, stageChanged: (stage, _) =>
                { if (stage == ConnectionStage.Waiting) retryWaiting.TrySetResult(); });
                await retryWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await launches.Should().BeEqualTo(0);
                reserved.Close();
                await (await retry).Should().BeEqualTo(CoreLoadResult.Started);
                await launches.Should().BeEqualTo(1);
            }
            if (!cancel)
            {
                var first = manager.RemainingProcesses.Select(p => p.Pid).ToArray();
                await (await manager.LoadCore(context, preContext, onlyIfChanged: true)).Should().BeEqualTo(CoreLoadResult.Unchanged);
                await (await manager.LoadCore(context, preContext, onlyIfChanged: false)).Should().BeEqualTo(CoreLoadResult.Started);
                await manager.RemainingProcesses.Any(p => first.Contains(p.Pid)).Should().BeFalse();
                await (await RecoveryProbe.CheckSocksAsync(port, CancellationToken.None)).Should().BeEqualTo(LocalHealth.Healthy);
                if (withPre) await (await manager.LoadCore(preContext, null)).Should().BeEqualTo(CoreLoadResult.Started);
            }
        }
        finally
        {
            reserved.Close(); cancellation.Cancel();
            await manager.CoreStop();
            if (before == null) File.Delete(formal); else await File.WriteAllBytesAsync(formal, before);
            if (preBefore == null) File.Delete(preFormal); else await File.WriteAllBytesAsync(preFormal, preBefore);
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task InvalidCandidateDoesNotStopTheOldCore()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix owned-process fixture"); return; }
        var config = CoreConfigTestFactory.CreateConfig(); CoreConfigTestFactory.BindAppManagerConfig(config);
        using var old = new ProcessService("/bin/sleep", "30", Path.GetTempPath(), false, false, null, null);
        await old.StartAsync(); old.IsReady = true;
        var manager = new CoreManager(_ => throw new Exception("Invalid candidate launched a core"));
        typeof(CoreManager).GetField("_processService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(manager, old);
        var path = Path.Combine(Path.GetTempPath(), "invalid-xray-" + Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"inbounds\":[{\"protocol\":\"socks\",\"port\":0}]}");
            var context = new CoreConfigContext { Node = new ProfileItem { ConfigType = EConfigType.Custom, CoreType = ECoreType.Xray, Address = path }, RunCoreType = ECoreType.Xray, AppConfig = config };
            await (await manager.LoadCore(context, null)).Should().BeEqualTo(CoreLoadResult.Failed);
            await (await manager.IsCoreRunningAsync()).Should().BeTrue();
        }
        finally { await manager.CoreStop(); File.Delete(path); }
    }

    [Test]
    public async Task RestartCommandRejectsDuplicatesAndUnavailableStates()
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        var vm = new ServiceLib.ViewModels.StatusBarViewModel { CoreRunning = true };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        vm.RestartCore = async () => { calls++; await release.Task; };
        var first = vm.RestartCoreCmd.Execute().ToTask();
        var second = vm.RestartCoreCmd.Execute().ToTask();
        release.SetResult(); await Task.WhenAll(first, second);
        await calls.Should().BeEqualTo(1);
        vm.CoreRunning = false;
        await vm.RestartCoreCmd.Execute().ToTask();
        vm.CoreRunning = true; vm.CoreReloading = true;
        await vm.RestartCoreCmd.Execute().ToTask();
        await calls.Should().BeEqualTo(1);
        vm.HasPendingChanges = true;
        await vm.RestartCoreLabel.Should().BeEqualTo(RoutingCoreCompatibility.T("应用并重启", "Apply & restart"));
    }
}
