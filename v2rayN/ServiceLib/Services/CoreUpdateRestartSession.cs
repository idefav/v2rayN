namespace ServiceLib.Services;

/// <summary>Installing a core must not reconnect a user who disconnected during the update.</summary>
internal sealed class CoreUpdateRestartSession(
    Func<bool> isCurrent,
    Func<string, Func<bool>, Task<bool>> restart,
    Action defer)
{
    public async Task<bool> RestartAsync(string reason)
    {
        if (!isCurrent()) return Defer();
        try
        {
            // The same predicate is checked again inside the reload coordinator's lock.
            var succeeded = await restart(reason, isCurrent);
            return !isCurrent() ? Defer() : succeeded;
        }
        catch (OperationCanceledException) when (!isCurrent())
        {
            return Defer();
        }
    }

    private bool Defer()
    {
        defer();
        // Installation succeeded; apply the new executable on the next explicit connection.
        return true;
    }
}
