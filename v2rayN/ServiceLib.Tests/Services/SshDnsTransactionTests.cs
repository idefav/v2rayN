namespace ServiceLib.Tests.Services;

[NotInParallel]
public class SshDnsTransactionTests
{
    private sealed class Remote : ISshDnsRemote
    {
        internal string Phase = "absent", Id = "";
        internal string? FailAction;
        internal bool CleanupFails;
        internal readonly List<string> Calls = [];
        internal CancellationTokenSource? Cancel;
        public Task<JsonObject> SendAsync(object request, CancellationToken token)
        {
            var data = JsonNode.Parse(JsonSerializer.Serialize(request))!;
            var action = data["action"]!.GetValue<string>();
            Calls.Add(action);
            if (action == "prepare") { Id = data["transactionId"]!.GetValue<string>(); Phase = "prepared"; }
            if (action == "activate") Phase = "awaiting_verification";
            if (action == "commit") Phase = "committed";
            if (action == "rollback")
            {
                if (token.IsCancellationRequested) throw new Exception("Cleanup inherited user cancellation");
                if (CleanupFails) throw new IOException("fixture cleanup failure");
                Phase = "rolled_back";
            }
            if (action == FailAction)
            {
                Cancel?.Cancel();
                if (Cancel != null) throw new OperationCanceledException(Cancel.Token);
                throw new IOException("fixture lost response");
            }
            return Task.FromResult(new JsonObject { ["id"] = Id, ["phase"] = Phase });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static SshDnsDeployment Deployment(Remote remote) => new(remote, new ProfileItem(),
        new SshDnsService { ProbeOverride = (_, _, _) => Task.FromResult(
            new SshDnsCheckResult(SshDnsVerdict.Match, "203.0.113.10", ["203.0.113.10"], 3, DateTimeOffset.UtcNow)) })
        { Plan = new JsonObject { ["phase"] = "absent", ["token"] = "fixture", ["exit"] = "203.0.113.10" } };

    [Test]
    [Arguments(SshDnsVerdict.Match)]
    [Arguments(SshDnsVerdict.Unknown)]
    public async Task RepeatedCompletedDeploymentOnlyChecksDns(SshDnsVerdict verdict)
    {
        var remote = new Remote { Phase = "committed", Id = "original-deployment" };
        var checks = 0;
        await using var deployment = new SshDnsDeployment(remote, new ProfileItem(), new SshDnsService
        {
            ProbeOverride = (_, _, _) =>
            {
                checks++;
                return Task.FromResult(new SshDnsCheckResult(verdict, "203.0.113.10", [], 0, DateTimeOffset.UtcNow));
            }
        }) { Plan = new JsonObject { ["phase"] = "committed" } };
        for (var i = 0; i < 2; i++)
            await (await deployment.InstallAsync(default)).Verdict.Should().BeEqualTo(verdict);
        await checks.Should().BeEqualTo(2);
        await remote.Calls.Count.Should().BeEqualTo(0);
        await remote.Phase.Should().BeEqualTo("committed");
        await remote.Id.Should().BeEqualTo("original-deployment");
    }

    [Test]
    public async Task CancellationAfterActivationRollsBackWithIndependentToken()
    {
        using var cancel = new CancellationTokenSource();
        var remote = new Remote { FailAction = "activate", Cancel = cancel };
        await using var deployment = Deployment(remote);
        try { await deployment.InstallAsync(cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        await remote.Phase.Should().BeEqualTo("rolled_back");
        await string.Join(",", remote.Calls).Should().BeEqualTo("prepare,activate,status,rollback");
    }

    [Test]
    public async Task LostCommitReplyReadsBackSuccessWithoutRollback()
    {
        var remote = new Remote { FailAction = "commit" };
        await using var deployment = Deployment(remote);
        var result = await deployment.InstallAsync(default);
        await result.Verdict.Should().BeEqualTo(SshDnsVerdict.Match);
        await remote.Phase.Should().BeEqualTo("committed");
        await remote.Calls.Contains("rollback").Should().BeFalse();
    }

    [Test]
    public async Task UnconfirmedCleanupIsReportedWithoutPretendingRollback()
    {
        var remote = new Remote { FailAction = "activate", CleanupFails = true };
        await using var deployment = Deployment(remote);
        try { await deployment.InstallAsync(default); throw new Exception("Expected unconfirmed rollback"); }
        catch (SshSetupException error) { await error.Code.Should().BeEqualTo("E_ROLLBACK_UNCONFIRMED"); }
        await remote.Phase.Should().BeEqualTo("awaiting_verification");
    }

    [Test]
    public async Task FailurePreparingStillReconcilesTheCreatedTransaction()
    {
        var remote = new Remote { FailAction = "prepare" };
        await using var deployment = Deployment(remote);
        try { await deployment.InstallAsync(default); throw new Exception("Expected failure"); }
        catch (IOException) { }
        await remote.Phase.Should().BeEqualTo("rolled_back");
        await remote.Calls.Contains("activate").Should().BeFalse();
    }
}
