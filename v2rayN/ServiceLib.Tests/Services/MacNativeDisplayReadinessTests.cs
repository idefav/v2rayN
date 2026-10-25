namespace ServiceLib.Tests.Services;

public class MacNativeDisplayReadinessTests
{
    private static readonly MacNativeDisplayState Ready = new(true, false, 1, 2, 0);

    [Test]
    public async Task LockedSessionIsNotReadyEvenWhenCoreVideoSucceeds()
    {
        await (Ready with { ScreenLocked = true }).IsReady.Should().BeFalse();
        await (Ready with { SessionAvailable = false }).IsReady.Should().BeFalse();
        await (Ready with { MainDisplay = 0 }).IsReady.Should().BeFalse();
        await (Ready with { ActiveDisplays = 0 }).IsReady.Should().BeFalse();
        await (Ready with { DisplayLinkStatus = -6661 }).IsReady.Should().BeFalse();
        await Ready.IsReady.Should().BeTrue();
    }

    [Test]
    public async Task StartupWaitsForUnlockAndAWorkingDisplayLink()
    {
        var states = new Queue<MacNativeDisplayState>([
            Ready with { ScreenLocked = true }, Ready with { ScreenLocked = true },
            Ready with { MainDisplay = 0 }, Ready with { DisplayLinkStatus = -6661 }, Ready
        ]);
        var warnings = new List<MacNativeDisplayState>();
        var waits = 0;
        await MacNativeDisplayReadiness.WaitCoreAsync(() => states.Dequeue(), Timeout.InfiniteTimeSpan,
            warnings.Add, CancellationToken.None, (_, _) => { waits++; return Task.CompletedTask; });
        await states.Count.Should().BeEqualTo(0);
        await waits.Should().BeEqualTo(4);
        await warnings.Count.Should().BeEqualTo(3);
    }

    [Test]
    public async Task UnavailableNativeReviewTimesOutInsteadOfReportingSuccess()
    {
        Exception? failure = null;
        try
        {
            await MacNativeDisplayReadiness.WaitCoreAsync(() => Ready with { ScreenLocked = true },
                TimeSpan.Zero, null, CancellationToken.None);
        }
        catch (Exception ex) { failure = ex; }
        await (failure is TimeoutException).Should().BeTrue();
        await failure!.Message.Contains("Unlock the Mac").Should().BeTrue();
    }

    [Test]
    public async Task WaitingStartupCanBeCancelledWithoutInitializingNativeUi()
    {
        using var cancel = new CancellationTokenSource();
        var probes = 0;
        Exception? failure = null;
        try
        {
            await MacNativeDisplayReadiness.WaitCoreAsync(() => { probes++; return Ready with { ActiveDisplays = 0 }; },
                Timeout.InfiniteTimeSpan, null, cancel.Token,
                (_, _) => { cancel.Cancel(); return Task.CompletedTask; });
        }
        catch (Exception ex) { failure = ex; }
        await (failure is OperationCanceledException).Should().BeTrue();
        await probes.Should().BeEqualTo(1);
    }

    [Test]
    public async Task ReadyDisplayDoesNotSleepOrPublishAWaitWarning()
    {
        var waits = 0;
        await MacNativeDisplayReadiness.WaitCoreAsync(() => Ready, TimeSpan.Zero, _ => waits++, CancellationToken.None,
            (_, _) => throw new InvalidOperationException("A ready display must not wait."));
        await waits.Should().BeEqualTo(0);
    }
}
