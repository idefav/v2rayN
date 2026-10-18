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
        public int Probes, Repairs, Restarts;
        public Func<CancellationToken, Task<string?>>? ReadNetwork;
        public Func<CancellationToken, Task<bool>>? Probe;
        public Func<CancellationToken, Task<bool>>? Reload;
        public ConnectionRecoveryService Service;

        public Harness()
        {
            Service = new(() => Context, ct => ReadNetwork?.Invoke(ct) ?? Task.FromResult(Network),
                (_, _) => Task.FromResult(new ProxyCheckResult(Proxy, "fixture")),
                (_, _) => { Repairs++; Proxy = RepairWorks; return Task.FromResult(RepairWorks); },
                (_, ct) => { Probes++; return Probe?.Invoke(ct) ?? Task.FromResult(Online); },
                (_, ct) => { Restarts++; return Reload?.Invoke(ct) ?? Task.FromResult(ReloadWorks); },
                _ => { }, Clock, new() { StableDelay = TimeSpan.Zero, RetryDelay = TimeSpan.Zero, ProxyRetryDelay = TimeSpan.Zero, Period = TimeSpan.FromHours(1) });
        }
        public Task Run() => Service.CheckAsync("test", CancellationToken.None);
    }

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
    public async Task SingleFailureDoesNotRestart()
    {
        var h = new Harness(); h.Probe = _ => Task.FromResult(h.Probes > 1); await h.Run();
        Check(h.Probes == 2 && h.Restarts == 0, "single failure restarted core");
    }

    [Test]
    public async Task TwoFailuresRestartOnceAndVerifyRecovery()
    {
        var h = new Harness(); h.Probe = _ => Task.FromResult(h.Restarts > 0); await h.Run();
        Check(h.Probes == 3 && h.Restarts == 1 && h.Service.State == RecoveryState.Idle, "restart did not verify active connection");
    }

    [Test]
    public async Task CoreStartFailureIsReportedWithoutAdditionalRestart()
    {
        var h = new Harness { Online = false, ReloadWorks = false }; await h.Run();
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
        var h = new Harness { Online = false };
        await h.Run(); await h.Run();
        Check(h.Restarts == 1, "60 second cooldown missing");
        h.Clock.Now += TimeSpan.FromSeconds(61); await h.Run();
        Check(h.Restarts == 2, "second restart should be allowed");
        h.Clock.Now += TimeSpan.FromSeconds(61); await h.Run();
        Check(h.Restarts == 2, "five minute restart limit missing");
        h.Clock.Now += TimeSpan.FromMinutes(5); await h.Run();
        Check(h.Restarts == 3, "expired restart history was not removed");
        h.Online = true; await h.Run();
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
        var h = new Harness { Online = false };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Reload = _ => { entered.SetResult(); return finish.Task; };
        h.Service.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = h.Service.StopAsync();
        Check(!stop.IsCompleted, "shutdown raced active core transaction");
        finish.SetResult(true); await stop.WaitAsync(TimeSpan.FromSeconds(3));
        h.Service.RequestCheck("late wake");
        Check(h.Restarts == 1 && h.Probes == 2, "shutdown allowed another proxy operation");
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
