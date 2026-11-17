using ServiceLib.Tests.CoreConfig;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class ConnectionIntegrityTests
{
    [Test]
    public async Task XrayTunAndManagementOnlyConfigurationsAreParsed()
    {
        var plan = CoreListenerPlan.Read("""{"inbounds":[{"protocol":"tun","settings":{"name":"utun99","gateway":["172.18.0.1/30"]}}],"metrics":{"listen":"127.0.0.1:12345"},"api":{"listen":"[::1]:12346"}}""", ECoreType.Xray);
        await plan.Tuns.Should().HaveCount(1);
        await plan.Ports.Should().HaveCount(2);
        await plan.Ports[0].Source.Should().BeEqualTo("metrics.listen");
        await CoreListenerPlan.Read("""{"metrics":{"listen":":12345"}}""", ECoreType.Xray).Ports.Should().HaveCount(1);
    }

    [Test]
    public async Task DuplicateCandidatesAreNotHiddenAndTcpUdpMayCoexist()
    {
        var p = new CoreListener(IPAddress.Loopback, 12345, ProtocolType.Tcp);
        var rejected = false;
        try { CoreListenerPlan.ValidateTogether(new([p, p], []), null); }
        catch (IOException ex) { rejected = ex.Message.Contains("main/"); }
        await rejected.Should().BeTrue();
        rejected = false;
        try { CoreListenerPlan.ValidateTogether(new([p], []), new([p], [])); }
        catch (IOException ex) { rejected = ex.Message.Contains("pre/"); }
        await rejected.Should().BeTrue();
        CoreListenerPlan.ValidateTogether(new([p], []), new([p with { Protocol = ProtocolType.Udp }], []));
        if (Socket.OSSupportsIPv6)
            CoreListenerPlan.ValidateTogether(new([p], []), new([p with { Address = IPAddress.IPv6Loopback }], []));
    }

    [Test]
    public async Task SecondPublishFailureRestoresBothOriginals()
    {
        var dir = Path.Combine(Path.GetTempPath(), "config-transaction-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        var main = Path.Combine(dir, "main"); var pre = Path.Combine(dir, "pre");
        try
        {
            await File.WriteAllTextAsync(main, "old-main"); await File.WriteAllTextAsync(pre, "old-pre");
            await File.WriteAllTextAsync(main + ".new", "new-main"); await File.WriteAllTextAsync(pre + ".new", "new-pre");
            var transaction = new CoreConfigTransaction([(main + ".new", main), (pre + ".new", pre)],
                (source, target) => { if (target == pre) throw new IOException("fixture failure"); File.Move(source, target, true); });
            try { transaction.Publish(); throw new Exception("Fault not injected"); }
            catch (IOException) { transaction.Rollback(); }
            await (await File.ReadAllTextAsync(main)).Should().BeEqualTo("old-main");
            await (await File.ReadAllTextAsync(pre)).Should().BeEqualTo("old-pre");
            await Directory.GetFiles(dir, "*.rollback-*").Length.Should().BeEqualTo(0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task ProxyFailureCanRetryWithoutRestartAndMatchedProxyDoesNotWrite()
    {
        var config = CoreConfigTestFactory.CreateConfig(); config.Inbound[0].LocalPort = 23456;
        var request = ProxyApplyRequest.Capture(config, 23456); config.Inbound[0].LocalPort = 34567;
        var calls = 0; var matched = false;
        Task<bool> Apply(ProxyApplyRequest r, CancellationToken _) { calls++; matched = calls > 1; return Task.FromResult(matched); }
        Task<bool?> Inspect(ProxyApplyRequest r, CancellationToken _) => Task.FromResult<bool?>(matched);
        var first = await ConnectionProxyApply.EnsureAsync(request, default, Apply, Inspect, _ => { });
        await first.Matches.Should().BeFalse();
        await (await ConnectionProxyApply.EnsureAsync(request, default, Apply, Inspect, _ => { })).Matches.Should().BeTrue();
        await (await ConnectionProxyApply.EnsureAsync(request, default, Apply, Inspect, _ => { })).Matches.Should().BeTrue();
        await calls.Should().BeEqualTo(2);
        await request.Config.Inbound[0].LocalPort.Should().BeEqualTo(23456);
    }

    [Test]
    public async Task CancelledProxyApplicationReadsBackWithIndependentToken()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0; ProxyApplyOutcome? published = null;
        try
        {
            await ConnectionProxyApply.EnsureAsync(ProxyApplyRequest.Capture(CoreConfigTestFactory.CreateConfig(), 10808), cancellation.Token,
                (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(false); },
                (_, token) => { if (token.IsCancellationRequested) throw new Exception("Readback used cancelled token"); reads++; return Task.FromResult<bool?>(reads > 1); },
                outcome => published = outcome);
            throw new Exception("Cancellation ignored");
        }
        catch (OperationCanceledException) { }
        await reads.Should().BeEqualTo(2);
        await published!.Matches.Should().BeTrue();
    }

    [Test]
    public async Task SameNodeDifferentGenerationCannotReceiveOldAvailability()
    {
        var first = new CoreRuntimeSummary("node", "node", "route", false) { ProxyPort = 10808 };
        var next = new CoreRuntimeSummary("node", "node", "route", false) { ProxyPort = 10808 };
        await StatusBarViewModel.AvailabilityBelongsTo(first, next).Should().BeFalse();
        await StatusBarViewModel.AvailabilityBelongsTo(first, first).Should().BeTrue();
        await new ConnectionRuntimeState(true, true, false, false, false, first, true, null).Ready.Should().BeFalse();
        await new ConnectionRuntimeState(true, true, false, false, false, first, true, null).Incomplete.Should().BeTrue();
        await new ConnectionRuntimeState(false, true, true, false, false, first, true, null).Incomplete.Should().BeTrue();
    }

    [Test]
    public async Task ListenerOwnerParsingRejectsOtherProcessesAndConnectedSockets()
    {
        var listeners = CoreListenerOwnership.ParseLsof("p42\ntIPv4\nn127.0.0.1:12345\nn127.0.0.1:12345->127.0.0.1:34567\np99\ntIPv6\nn*:12346\n", ProtocolType.Tcp, new HashSet<int> { 42 });
        await listeners.Should().HaveCount(1);
        await CoreListenerOwnership.ParseNetstat("TCP 127.0.0.1:12345 0.0.0.0:0 LISTENING 99", new HashSet<int> { 42 }).Should().HaveCount(0);
        await CoreListenerOwnership.ParseNetstat("TCP [::1]:12345 [::]:0 LISTENING 42\nUDP [::]:12346 *:* 42", new HashSet<int> { 42 }).Should().HaveCount(2);
        await CoreListenerOwnership.Covers(new(IPAddress.Loopback, 12345, ProtocolType.Tcp), new(IPAddress.Any, 12345, ProtocolType.Tcp)).Should().BeFalse();
    }

    [Test]
    public async Task StdoutFailureSurvivesAndUnknownListenerCannotMakeSleepReady()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix isolated fixture"); return; }
        using var process = new ProcessService("/bin/sh", "", Path.GetTempPath(), true, false, null, null);
        process.SetArguments(["-c", "echo 'Failed to start: fixture binding failure'; exec /bin/sleep 20"]);
        await process.StartAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var endpoint = new CoreListener(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ProtocolType.Tcp);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            try { await CoreListenerCheck.WaitForReadyAsync(process, [endpoint], timeout.Token); throw new Exception("Unknown listener accepted"); }
            catch (OperationCanceledException) { }
            await process.IsReady.Should().BeFalse();
            await process.RecentError.Should().Contain("fixture binding failure");
            await listener.Server.IsBound.Should().BeTrue();
        }
        finally { await process.StopAndWaitAsync(); }
    }

    [Test]
    public async Task CancellingNativePreflightConfirmsItsOwnCheckerExit()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix isolated checker fixture"); return; }
        var dir = Path.Combine(Path.GetTempPath(), "checker-cancel-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "checker.sh"); var pidFile = Path.Combine(dir, "pid"); var config = Path.Combine(dir, "candidate.json");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await File.WriteAllTextAsync(script, "#!/bin/sh\necho $$ > " + UnixProcessControl.Quote(pidFile) + "\nexec /bin/sleep 30\n");
            await File.WriteAllTextAsync(config, "{}");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var check = RoutingCoreCompatibility.CheckExecutableAsync(script, ECoreType.Xray, config, new Dictionary<string, string?>(), cancellation.Token);
            while (!File.Exists(pidFile))
            {
                if (check.IsCompleted) throw new Exception("Checker exited before fixture initialization: " + await check);
                await Task.Delay(20, cancellation.Token);
            }
            cancellation.Cancel();
            try { await check; throw new Exception("Preflight ignored cancellation"); }
            catch (OperationCanceledException) { }
            var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
            try { using var process = Process.GetProcessById(pid); await process.HasExited.Should().BeTrue(); }
            catch (ArgumentException) { }
            await ProcessService.HasUnstoppedConfig(config).Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }
}
