namespace ServiceLib.Tests.Services;

public class ConnectionRecoveryTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Harness
    {
        public readonly Clock Clock = new();
        public RecoveryContext? Context = new("node", 12345, "https://probe.invalid/", 1);
        public string? Network = "en0:192.0.2.1";
        public bool Proxy = true;
        public bool RepairWorks = true;
        public bool Online = true;
        public bool ReloadWorks = true;
        public bool LocalHealthy = true;
        public Func<Task<LocalRecoveryResult>>? Local;
        public readonly List<string> Logs = [];
        public int Probes, Repairs, Restarts;
        public Func<CancellationToken, Task<string?>>? ReadNetwork;
        public Func<CancellationToken, Task<ProxyCheckResult>>? ReadProxy;
        public Func<CancellationToken, Task<bool>>? ApplyProxy;
        public Func<CancellationToken, Task<bool>>? Probe;
        public Func<CancellationToken, Task<bool>>? Reload;
        public Func<Task>? BeforeConfirm;
        public ProbeOutcome ExternalFailure = ProbeOutcome.Timeout;
        public ConnectionRecoveryService Service;

        public Harness()
        {
            Service = new(() => Context, ct => ReadNetwork?.Invoke(ct) ?? Task.FromResult(Network),
                (_, ct) => ReadProxy?.Invoke(ct) ?? Task.FromResult(new ProxyCheckResult(Proxy, "fixture")),
                async (_, ct) =>
                {
                    Repairs++;
                    if (ApplyProxy != null) return await ApplyProxy(ct);
                    Proxy = RepairWorks; return RepairWorks;
                },
                async (_, ct) => { Probes++; return Result(await (Probe?.Invoke(ct) ?? Task.FromResult(Online)), ExternalFailure); },
                async (request, ct) =>
                {
                    if (BeforeConfirm != null) await BeforeConfirm();
                    if (!await request.Confirm(ct)) return false;
                    Restarts++;
                    if (Reload != null) return await Reload(ct);
                    LocalHealthy = ReloadWorks;
                    return ReloadWorks;
                },
                Logs.Add, Clock, new() { StableDelay = TimeSpan.Zero, RetryDelay = TimeSpan.Zero, ProxyRetryDelay = TimeSpan.Zero, Period = TimeSpan.FromHours(1) },
                (_, _) => Local?.Invoke() ?? Task.FromResult(new LocalRecoveryResult(LocalHealth.Healthy, LocalHealth.Healthy,
                    LocalHealthy ? LocalHealth.Healthy : LocalHealth.Failed)));
        }
        public Task Run() => Service.CheckAsync("test", CancellationToken.None);
    }

    private static RecoveryProbeResult Result(bool success, ProbeOutcome failure = ProbeOutcome.Timeout) => new([new("fixture.invalid",
        success ? ProbeOutcome.Success : failure, success ? 204 : null, 0)]);

    [Test]
    public async Task HealthyConnectionIsPreserved()
    {
        var h = new Harness(); await h.Run();
        Check(h.Restarts == 0 && h.Repairs == 0 && h.Probes == 1 && h.Service.State == RecoveryState.Idle, "healthy connection changed");
    }

    [Test]
    public async Task OnlyProxySettingsAreRepaired()
    {
        var h = new Harness { Proxy = false }; await h.Run();
        Check(h.Repairs == 1 && h.Restarts == 0 && h.Service.Error == null, "proxy-only failure restarted core");
    }

    [Test]
    public async Task ProxyFailureHasBoundedRetriesAndNeverRestartsCore()
    {
        var h = new Harness { Proxy = false, RepairWorks = false }; await h.Run();
        Check(h.Repairs == 3 && h.Restarts == 0 && h.Probes == 0 && h.Service.Error!.StartsWith("System proxy"), "proxy failure misclassified");
    }

    [Test]
    [Arguments("read")]
    [Arguments("apply")]
    [Arguments("readback")]
    public async Task InternalProxyTimeoutRetriesAndReportsFailure(string phase)
    {
        var h = new Harness { Proxy = false };
        var reads = 0;
        h.ReadProxy = _ =>
        {
            reads++;
            if (phase == "read" || phase == "readback" && reads % 2 == 0)
                throw new OperationCanceledException("internal command timeout with sensitive details");
            return Task.FromResult(new ProxyCheckResult(false, "fixture"));
        };
        if (phase == "apply") h.ApplyProxy = _ => throw new OperationCanceledException("internal command timeout");
        await h.Run();
        Check(reads == (phase == "readback" ? 6 : 3) && h.Repairs == (phase == "read" ? 0 : 3), "internal timeout did not exhaust bounded retries");
        Check(h.Service.State == RecoveryState.Failed && h.Restarts == 0 && h.Probes == 0, "internal timeout was treated as caller cancellation");
        Check(h.Logs.All(l => !l.Contains("sensitive")), "timeout details leaked");
    }

    [Test]
    public async Task TransientProxyCommandTimeoutCanRecoverWithinSameCheck()
    {
        var h = new Harness(); var reads = 0;
        h.ReadProxy = _ => ++reads < 3 ? throw new OperationCanceledException("internal timeout")
            : Task.FromResult(new ProxyCheckResult(true, "fixture"));
        await h.Run();
        Check(reads == 3 && h.Service.State == RecoveryState.Idle && h.Restarts == 0, "transient timeout aborted recovery");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CallerCancellationOrSelectionChangeDoesNotRetryProxyCommand(bool cancelCaller)
    {
        var h = new Harness(); var reads = 0;
        using var cancel = new CancellationTokenSource();
        h.ReadProxy = _ =>
        {
            reads++;
            if (cancelCaller) cancel.Cancel();
            else h.Context = h.Context! with { Revision = 2 };
            throw new OperationCanceledException();
        };
        try { await h.Service.CheckAsync("cancel fixture", cancel.Token); throw new Exception("obsolete check was accepted"); }
        catch (OperationCanceledException) { }
        Check(reads == 1 && h.Restarts == 0 && h.Repairs == 0, "obsolete proxy command was retried");
    }

    [Test]
    public async Task WorkerReportsInternalProxyTimeoutInsteadOfStayingChecking()
    {
        var h = new Harness();
        h.ReadProxy = _ => throw new OperationCanceledException("internal timeout");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.Changed += () => { if (h.Service.State == RecoveryState.Failed) failed.TrySetResult(); };
        h.Service.Start();
        try { await failed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { await h.Service.StopAsync(); }
        Check(h.Service.Error?.StartsWith("System proxy") == true, "worker swallowed internal timeout");
    }

    [Test]
    public async Task WorkerReportsNetworkCommandTimeoutAndCanProcessAnotherCheck()
    {
        var h = new Harness();
        h.ReadNetwork = _ => throw new TimeoutException("sensitive platform arguments");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.Changed += () =>
        {
            if (h.Service.State == RecoveryState.Failed) failed.TrySetResult();
            if (h.Service.State == RecoveryState.Idle) healthy.TrySetResult();
        };
        h.Service.Start();
        try
        {
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(h.Service.Error?.Contains("TimeoutException") == true && h.Restarts == 0,
                "network command timeout was treated as cancellation or restarted core");
            h.ReadNetwork = null;
            h.Service.RequestCheck("retry after network command timeout");
            await healthy.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(h.Logs.All(l => !l.Contains("sensitive")), "network command details leaked");
        }
        finally { await h.Service.StopAsync(); }
    }

    [Test]
    public async Task SingleFailureDoesNotRestart()
    {
        var h = new Harness(); h.Probe = _ => Task.FromResult(h.Probes > 1); await h.Run();
        Check(h.Probes == 2 && h.Restarts == 0, "single failure restarted core");
    }

    [Test]
    public async Task TwoFailuresRestartOnceAndVerifyRecovery()
    {
        var h = new Harness { LocalHealthy = false }; await h.Run();
        Check(h.Probes == 1 && h.Restarts == 1 && h.Service.State == RecoveryState.Idle, "restart did not verify active connection");
    }

    [Test]
    public async Task CoreStartFailureIsReportedWithoutAdditionalRestart()
    {
        var h = new Harness { LocalHealthy = false, ReloadWorks = false }; await h.Run();
        Check(h.Restarts == 1 && h.Service.Error!.StartsWith("Core restart failed"), "core startup failure missing");
    }

    [Test]
    public async Task OfflineAndDisappearingNetworksDoNotRestart()
    {
        var h = new Harness { Network = null }; await h.Run();
        Check(h.Probes == 0 && h.Restarts == 0 && h.Service.State == RecoveryState.Offline, "offline core restarted");
        h.Network = "en0"; h.Online = false;
        h.Probe = _ => { h.Network = null; return Task.FromResult(false); };
        await h.Run();
        Check(h.Restarts == 0, "link lost during request restarted core");
    }

    [Test]
    public async Task RestartCooldownAndRollingLimitUseInjectedClock()
    {
        var h = new Harness { Online = false, LocalHealthy = false };
        h.Reload = _ => Task.FromResult(true);
        await h.Run(); await h.Run();
        Check(h.Restarts == 1, "60 second cooldown missing");
        h.Clock.Now += TimeSpan.FromSeconds(61); await h.Run();
        Check(h.Restarts == 2, "second restart should be allowed");
        h.Clock.Now += TimeSpan.FromSeconds(61); await h.Run();
        Check(h.Restarts == 2, "five minute restart limit missing");
        h.Clock.Now += TimeSpan.FromMinutes(5); await h.Run();
        Check(h.Restarts == 3, "expired restart history was not removed");
        h.Online = true; h.LocalHealthy = true; await h.Run();
        Check(h.Service.Error == null, "successful probe did not clear failure");
    }

    [Test]
    public async Task ChangedNodeInvalidatesStaleProbeEvenIfItIgnoresCancellation()
    {
        var h = new Harness();
        h.Probe = _ => { h.Context = h.Context! with { Revision = 2 }; return Task.FromResult(true); };
        try { await h.Run(); throw new Exception("stale result accepted"); }
        catch (OperationCanceledException) { }
        Check(h.Restarts == 0, "stale node restarted");
    }

    [Test]
    public async Task EventBurstCancelsOldCheckAndCoalescesPendingChecks()
    {
        var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        h.ReadNetwork = async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult(); await release.Task; ct.ThrowIfCancellationRequested();
            }
            return h.Network;
        };
        h.Probe = _ => { healthy.TrySetResult(); return Task.FromResult(true); };
        h.Service.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 100; i++) h.Service.RequestCheck("wake");
        release.SetResult();
        await healthy.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await h.Service.StopAsync();
        Check(h.Restarts == 0 && h.Probes <= 2, "event burst duplicated recovery jobs");
    }

    [Test]
    public async Task ShutdownWaitsForStartedReloadAndRejectsLateEvents()
    {
        var h = new Harness { Online = false, LocalHealthy = false };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Reload = _ => { entered.SetResult(); return finish.Task; };
        h.Service.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = h.Service.StopAsync();
        Check(!stop.IsCompleted, "shutdown raced active core transaction");
        finish.SetResult(true); await stop.WaitAsync(TimeSpan.FromSeconds(3));
        h.Service.RequestCheck("late wake");
        Check(h.Restarts == 1 && h.Probes == 0, "shutdown allowed another proxy operation");
    }

    [Test]
    [Arguments("Timeout")]
    [Arguments("Dns")]
    [Arguments("Tls")]
    [Arguments("Connection")]
    [Arguments("HttpRejected")]
    public async Task ExternalFailuresAcrossManyPeriodsNeverRestartHealthyLocalCore(string outcome)
    {
        var h = new Harness { Online = false, ExternalFailure = Enum.Parse<ProbeOutcome>(outcome) };
        for (var i = 0; i < 8; i++) { await h.Run(); h.Clock.Now += TimeSpan.FromMinutes(1); }
        Check(h.Restarts == 0 && h.Probes == 16 && h.Service.State == RecoveryState.Degraded, "external failure restarted local core");
        Check(h.Logs.Any(l => l.Contains("decision=preserve external")), "preservation reason missing");
        h.Online = true; await h.Run();
        Check(h.Service.State == RecoveryState.Idle && h.Service.Error == null, "probe recovery did not clear degradation");
    }

    [Test]
    public async Task TransientLocalFailureIsPreserved()
    {
        var h = new Harness(); var calls = 0;
        h.Local = () => Task.FromResult(new LocalRecoveryResult(LocalHealth.Healthy, LocalHealth.Healthy,
            ++calls == 1 ? LocalHealth.Failed : LocalHealth.Healthy));
        await h.Run();
        Check(h.Restarts == 0 && h.Service.State == RecoveryState.Idle, "transient local fault restarted core");
    }

    [Test]
    public async Task RecurringLocalFaultAfterTransientRecoveryIsConfirmedInSameCheck()
    {
        var h = new Harness { Online = false }; var locals = 0;
        h.Local = () => Task.FromResult(new LocalRecoveryResult(LocalHealth.Healthy, LocalHealth.Healthy,
            h.Restarts > 0 || ++locals == 2 ? LocalHealth.Healthy : LocalHealth.Failed));
        await h.Run();
        Check(h.Restarts == 1 && locals >= 5 && h.Service.State == RecoveryState.Degraded,
            "recurring local fault was mislabeled as external-only failure");
        Check(h.Logs.Any(l => l.Contains("decision=restart")), "recurring fault evidence missing");
    }

    [Test]
    public async Task LocalHealthFlappingHasBoundedWorkAndDoesNotClaimHealthy()
    {
        var h = new Harness { Online = false }; var locals = 0;
        h.Local = () => Task.FromResult(new LocalRecoveryResult(LocalHealth.Healthy, LocalHealth.Healthy,
            ++locals % 2 == 0 ? LocalHealth.Healthy : LocalHealth.Failed));
        await h.Run();
        Check(h.Restarts == 0 && locals == 4 && h.Probes == 2 && h.Service.Error!.Contains("unstable"),
            "flapping check was unbounded or claimed external-only failure");
    }

    [Test]
    public async Task LocalFaultRecurringDuringPostRestartProbeIsReportedWithoutSecondRestart()
    {
        var h = new Harness { LocalHealthy = false, Online = false };
        h.Probe = _ => { h.LocalHealthy = false; return Task.FromResult(false); };
        await h.Run();
        Check(h.Restarts == 1 && h.Service.State == RecoveryState.Failed
            && h.Service.Error!.StartsWith("Local core unavailable"), "post-restart local fault was reported healthy");
    }

    [Test]
    public async Task UnknownAndThrowingLocalChecksNeverAuthorizeRestart()
    {
        var h = new Harness { Online = false };
        h.Local = () => Task.FromResult(new LocalRecoveryResult(LocalHealth.Unknown, LocalHealth.Unknown, LocalHealth.Unknown));
        await h.Run();
        h.Local = () => throw new InvalidOperationException("sensitive fixture message");
        await h.Run();
        Check(h.Restarts == 0 && h.Service.State == RecoveryState.Degraded, "unknown local state restarted core");
        Check(h.Logs.All(l => !l.Contains("sensitive")), "local exception message leaked");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ExitedMainOrRequiredPreCoreRestarts(bool main)
    {
        var h = new Harness();
        h.Local = () => Task.FromResult(h.Restarts > 0
            ? new LocalRecoveryResult(LocalHealth.Healthy, LocalHealth.Healthy, LocalHealth.Healthy)
            : new LocalRecoveryResult(main ? LocalHealth.Failed : LocalHealth.Healthy,
                main ? LocalHealth.Healthy : LocalHealth.Failed, LocalHealth.Healthy));
        await h.Run();
        Check(h.Restarts == 1 && h.Service.State == RecoveryState.Idle, "exited process not recovered");
        Check(h.Logs.Any(l => l.Contains(main ? "主内核进程已退出" : "必要前置内核已退出")), "restart evidence missing");
    }

    [Test]
    public async Task LocalRecoveryWithExternalFailureDoesNotRestartAgain()
    {
        var h = new Harness { LocalHealthy = false, Online = false };
        await h.Run(); h.Clock.Now += TimeSpan.FromMinutes(2); await h.Run();
        Check(h.Restarts == 1 && h.Service.State == RecoveryState.Degraded, "external failure caused a second restart");
    }

    [Test]
    public async Task LocalFaultClearedBeforeReloadConfirmationDoesNotRestart()
    {
        var h = new Harness(); var calls = 0;
        h.Local = () => Task.FromResult(new LocalRecoveryResult(LocalHealth.Healthy, LocalHealth.Healthy,
            ++calls <= 2 ? LocalHealth.Failed : LocalHealth.Healthy));
        await h.Run();
        Check(h.Restarts == 0 && h.Logs.Any(l => l.Contains("fault cleared")), "cleared fault restarted core");
    }

    [Test]
    [Arguments("network")]
    [Arguments("selection")]
    [Arguments("recovered")]
    public async Task QueuedRestartRevalidatesNetworkSelectionAndLocalFault(string change)
    {
        var h = new Harness { LocalHealthy = false };
        var coordinator = new ConnectionReloadCoordinator();
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = coordinator.RunAsync(() => true, () => hold.Task, CancellationToken.None);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforeConfirm = async () =>
        {
            queued.SetResult();
            await coordinator.RunAsync(() => true, () => Task.FromResult(true), CancellationToken.None);
        };
        var check = h.Run();
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (change == "network") h.Network = null;
        if (change == "selection") h.Context = h.Context! with { Revision = 2 };
        if (change == "recovered") h.LocalHealthy = true;
        hold.SetResult(true); await blocker;
        try { await check; Check(change != "selection", "stale selection was accepted"); }
        catch (OperationCanceledException) when (change == "selection") { }
        Check(h.Restarts == 0, "queued obsolete restart executed");
        if (change == "network") Check(h.Service.State == RecoveryState.Offline, "network loss overwritten with another state");
    }

    [Test]
    public async Task FaultDetectedAfterExternalFailuresStillRequiresTwoLocalConfirmations()
    {
        var h = new Harness { Online = false };
        h.Probe = _ => { h.LocalHealthy = false; return Task.FromResult(false); };
        await h.Run();
        Check(h.Restarts == 1 && h.Logs.Count(l => l.Contains("socks=Failed")) >= 3, "post-probe local fault not confirmed");
    }

    [Test]
    public async Task ReloadCallersAwaitTheirOwnTransactionAndRecheckSelectionAfterWaiting()
    {
        var coordinator = new ConnectionReloadCoordinator();
        var firstDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.RunAsync(() => true, () => firstDone.Task, CancellationToken.None);
        var current = true; var secondRan = false;
        var second = coordinator.RunAsync(() => current, () => { secondRan = true; return Task.FromResult(true); }, CancellationToken.None);
        Check(!second.IsCompleted, "queued reload returned early");
        current = false; firstDone.SetResult(true);
        await first;
        Check(!await second && !secondRan, "stale queued reload executed");
        await coordinator.WaitForIdleAsync();
    }
}
