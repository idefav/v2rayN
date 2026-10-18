namespace ServiceLib.Services;

/// <summary>Queued callers await their own transaction, including callers behind an in-flight reload.</summary>
internal sealed class ConnectionReloadCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> RunAsync(Func<bool> isCurrent, Func<Task<bool>> reload, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            return isCurrent() && await reload();
        }
        finally { _gate.Release(); }
    }

    public async Task WaitForIdleAsync()
    {
        await _gate.WaitAsync();
        _gate.Release();
    }
}
