namespace ServiceLib.Tests.Manager;

public class CoreManagerTests
{
    [Test]
    public async Task PartialLaunchKeepsRequiredPreCoreRecoverableWithoutEffectiveConfig()
    {
        // Seed the state of a partial launch: the desired topology is known, but
        // no effective-config snapshot exists because the required pre-core failed.
        var manager = new CoreManager();
        var requirements = typeof(CoreManager).GetField("_processRequirements", BindingFlags.Instance | BindingFlags.NonPublic)!;
        requirements.SetValue(manager, new CoreProcessRequirements(PreRequired: true));
        var restarts = 0;
        var clock = new RecoveryClock();
        var context = new RecoveryContext("partial-launch", 12345, "https://fixture.invalid", 1);
        var recovery = new ConnectionRecoveryService(() => context, _ => Task.FromResult<string?>("fixture"),
            (_, _) => Task.FromResult(new ProxyCheckResult(true, "fixture")), (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(new RecoveryProbeResult([])),
            async (request, token) =>
            {
                if (!await request.Confirm(token)) return false;
                if (request.Reason != "必要前置内核已退出") throw new Exception("missing pre-core requirement");
                restarts++;
                // A retry also fails to launch the pre-core; the effective snapshot
                // remains absent, just as it does after LoadCore returns Failed.
                return false;
            }, _ => { }, clock, new() { StableDelay = TimeSpan.Zero, RetryDelay = TimeSpan.Zero },
            localHealth: (_, _) => Task.FromResult(new LocalRecoveryResult(LocalHealth.Healthy,
                manager.GetRecoveryProcessHealth().Pre, LocalHealth.Healthy)));
        await recovery.CheckAsync("partial startup", CancellationToken.None);
        if (restarts != 1 || recovery.State != RecoveryState.Failed) throw new Exception("partial launch was not recoverable");
        await recovery.CheckAsync("cooldown", CancellationToken.None);
        if (restarts != 1) throw new Exception("partial startup ignored restart cooldown");
        clock.Now += TimeSpan.FromSeconds(61);
        await recovery.CheckAsync("retry", CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(61);
        await recovery.CheckAsync("rolling limit", CancellationToken.None);
        if (restarts != 2) throw new Exception("pre-core recovery lost its requirement or ignored restart limit");
        await manager.CoreStop();
        if (manager.GetRecoveryProcessHealth().Pre != LocalHealth.Unknown) throw new Exception("stop retained stale topology");
        requirements.SetValue(manager, new CoreProcessRequirements(PreRequired: false));
        if (manager.GetRecoveryProcessHealth().Pre != LocalHealth.Healthy) throw new Exception("new topology retained an old pre-core requirement");
    }

    private sealed class RecoveryClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Test]
    [Arguments(ECoreType.sing_box)]
    [Arguments(ECoreType.mihomo)]
    [Arguments(ECoreType.Xray)]
    public async Task ShouldRunAsSudo_TunLaunchOnNonWindows_RequiresElevation(ECoreType coreType)
    {
        await CoreManager.ShouldRunAsSudo(isTunLaunch: true, coreType, isNonWindows: true).Should().BeTrue();
    }

    [Test]
    public async Task ShouldRunAsSudo_NonTunLaunch_ShouldNotElevate()
    {
        // Regression guard for the macOS TUN failure: the elevation decision must follow
        // the context snapshot that generated the config. A launch whose snapshot has TUN
        // disabled must never elevate, and a launch whose snapshot has TUN enabled must
        // elevate regardless of later changes to the live config.
        await CoreManager.ShouldRunAsSudo(isTunLaunch: false, ECoreType.sing_box, isNonWindows: true).Should().BeFalse();
        await CoreManager.ShouldRunAsSudo(isTunLaunch: false, ECoreType.Xray, isNonWindows: true).Should().BeFalse();
    }

    [Test]
    public async Task ShouldRunAsSudo_OnWindows_ShouldNotElevate()
    {
        await CoreManager.ShouldRunAsSudo(isTunLaunch: true, ECoreType.sing_box, isNonWindows: false).Should().BeFalse();
    }

    [Test]
    [Arguments(ECoreType.v2fly)]
    [Arguments(ECoreType.hysteria)]
    [Arguments(null)]
    public async Task ShouldRunAsSudo_UnsupportedCoreType_ShouldNotElevate(ECoreType? coreType)
    {
        await CoreManager.ShouldRunAsSudo(isTunLaunch: true, coreType, isNonWindows: true).Should().BeFalse();
    }
}
