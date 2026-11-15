namespace ServiceLib.Services;

internal sealed record ShutdownResult(bool Completed, IReadOnlyList<string> Errors, IReadOnlyList<ProcessIdentity> RemainingProcesses);

// A single cleanup transaction, shared by all exit entry points.
internal sealed class ShutdownCoordinator
{
    private readonly object _gate = new();
    private Task<ShutdownResult>? _task;
    public Task<ShutdownResult> RunAsync(Func<Task<ShutdownResult>> cleanup)
    {
        lock (_gate) return _task ??= cleanup();
    }
}

internal sealed class ShutdownStages(CancellationToken token, Action<string, Exception?> log)
{
    public List<string> Errors { get; } = [];

    public async Task RunAsync(string name, Func<CancellationToken, Task> action, TimeSpan? limit = null)
    {
        var clock = Stopwatch.StartNew();
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (limit.HasValue) stage.CancelAfter(limit.Value);
        try
        {
            stage.Token.ThrowIfCancellationRequested();
            await action(stage.Token).WaitAsync(stage.Token);
        }
        catch (Exception ex) { Errors.Add(name + ": " + ex.Message); log(name, ex); }
        finally { log($"{name} elapsedMs={clock.ElapsedMilliseconds}", null); }
    }
}
