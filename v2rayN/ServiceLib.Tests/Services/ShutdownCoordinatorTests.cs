namespace ServiceLib.Tests.Services;

public class ShutdownCoordinatorTests
{
    [Test]
    public async Task ConcurrentExitsShareCleanupAndResult()
    {
        var coordinator = new ShutdownCoordinator();
        var completion = new TaskCompletionSource<ShutdownResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<ShutdownResult> Clean() { Interlocked.Increment(ref calls); return completion.Task; }
        var requests = Enumerable.Range(0, 20).Select(_ => coordinator.RunAsync(Clean)).ToArray();
        if (calls != 1 || requests.Any(t => t.IsCompleted)) throw new Exception("Cleanup was duplicated or not awaited");
        completion.SetResult(new(true, [], []));
        await Task.WhenAll(requests);
        if (requests.Any(t => !t.Result.Completed)) throw new Exception("Shared result lost");
    }

    [Test]
    public async Task FailureAndStageTimeoutDoNotSkipOtherCleanup()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var stages = new ShutdownStages(budget.Token, (_, _) => { });
        await stages.RunAsync("proxy", _ => throw new IOException("injected"));
        await stages.RunAsync("save", token => Task.Delay(Timeout.Infinite, token), TimeSpan.FromMilliseconds(30));
        var main = false; var pre = false;
        await stages.RunAsync("main", _ => { main = true; throw new IOException("stop failed"); });
        await stages.RunAsync("pre", _ => { pre = true; return Task.CompletedTask; });
        if (!main || !pre || stages.Errors.Count != 3) throw new Exception("Cleanup stages were skipped");
    }

    [Test]
    public async Task TotalBudgetBoundsUncooperativeOperation()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        var stages = new ShutdownStages(budget.Token, (_, _) => { });
        var pending = new TaskCompletionSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await stages.RunAsync("blocked", _ => pending.Task);
        var started = false;
        await stages.RunAsync("expired", _ => { started = true; return Task.CompletedTask; });
        pending.SetResult();
        if (started || stages.Errors.Count != 2 || clock.Elapsed > TimeSpan.FromSeconds(2)) throw new Exception("Exit exceeded budget");
    }
}
