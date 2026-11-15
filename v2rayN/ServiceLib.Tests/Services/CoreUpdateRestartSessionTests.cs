namespace ServiceLib.Tests.Services;

public class CoreUpdateRestartSessionTests
{
    [Test]
    [Arguments("disconnected")]
    [Arguments("disconnect-during-download")]
    [Arguments("selection-changed")]
    [Arguments("exiting")]
    public async Task UpdateRemainsInstalledWithoutRestartWhenIntentNoLongerAllowsIt(string change)
    {
        using var fixture = new UpdateFixture();
        var wasRunning = change != "disconnected";
        var paused = false;
        var exiting = false;
        var revision = 1;
        var capturedRevision = revision;
        var starts = 0;
        var deferred = 0;
        var session = new CoreUpdateRestartSession(
            () => wasRunning && !paused && !exiting && revision == capturedRevision,
            (_, _) => { starts++; return Task.FromResult(true); }, () => deferred++);
        if (change == "disconnect-during-download") paused = true;
        if (change == "selection-changed") revision++;
        if (change == "exiting") exiting = true;

        await fixture.Store.ActivateAsync(fixture.Candidate, () => session.RestartAsync("update"));

        await starts.Should().BeEqualTo(0);
        await deferred.Should().BeEqualTo(1);
        await fixture.Store.Read()!.Version.Should().BeEqualTo("1.2.4");
    }

    [Test]
    public async Task RunningUpdateFailureStillRollsBackAndRestartsThePreviousVersion()
    {
        using var fixture = new UpdateFixture();
        var starts = new List<string>();
        var deferred = 0;
        var session = new CoreUpdateRestartSession(() => true, (_, _) =>
        {
            starts.Add(fixture.Store.Read()!.Version);
            // Failed candidate has already stopped; the original intent still permits rollback.
            return Task.FromResult(starts.Count > 1);
        }, () => deferred++);
        var failed = false;
        try { await fixture.Store.ActivateAsync(fixture.Candidate, () => session.RestartAsync("update")); }
        catch (InvalidOperationException) { failed = true; }
        await failed.Should().BeTrue();
        await string.Join(",", starts).Should().BeEqualTo("1.2.4,1.2.3");
        await deferred.Should().BeEqualTo(0);
        await fixture.Store.Read()!.Version.Should().BeEqualTo("1.2.3");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisconnectDuringActivationDoesNotRollBackIntoAnotherConnection(bool cancelled)
    {
        using var fixture = new UpdateFixture();
        var current = true;
        var starts = 0;
        var deferred = 0;
        var session = new CoreUpdateRestartSession(() => current, (_, _) =>
        {
            starts++;
            current = false;
            return cancelled ? Task.FromException<bool>(new OperationCanceledException()) : Task.FromResult(false);
        }, () => deferred++);

        await fixture.Store.ActivateAsync(fixture.Candidate, () => session.RestartAsync("update"));

        await starts.Should().BeEqualTo(1);
        await deferred.Should().BeEqualTo(1);
        await fixture.Store.Read()!.Version.Should().BeEqualTo("1.2.4");
    }

    [Test]
    public async Task CancellationWithoutChangedIntentRetainsFailureAndRollback()
    {
        using var fixture = new UpdateFixture();
        var starts = 0;
        var session = new CoreUpdateRestartSession(() => true, (_, _) =>
            ++starts == 1 ? Task.FromException<bool>(new OperationCanceledException()) : Task.FromResult(true),
            () => throw new InvalidOperationException("Unexpected defer"));
        var cancelled = false;
        try { await fixture.Store.ActivateAsync(fixture.Candidate, () => session.RestartAsync("update")); }
        catch (OperationCanceledException) { cancelled = true; }
        await cancelled.Should().BeTrue();
        await starts.Should().BeEqualTo(2);
        await fixture.Store.Read()!.Version.Should().BeEqualTo("1.2.3");
    }

    private sealed class UpdateFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "core-update-intent-" + Guid.NewGuid());
        public InstalledUpdateStore Store { get; }
        public InstalledUpdate Candidate { get; }
        public UpdateFixture()
        {
            Directory.CreateDirectory(_root);
            Store = new InstalledUpdateStore(_root);
            Store.Write(Create("1.2.3"));
            Candidate = Create("1.2.4");
        }
        private InstalledUpdate Create(string version)
        {
            var file = Path.Combine(_root, version);
            File.WriteAllText(file, "isolated update fixture " + version);
            return new InstalledUpdate { Repository = "fixture/core", Tag = "v" + version, Version = version,
                Executable = version, Sha256 = InstalledUpdateStore.Digest(file) };
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
