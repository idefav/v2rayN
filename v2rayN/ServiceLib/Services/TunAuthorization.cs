namespace ServiceLib.Services;

internal enum TunAuthorizationState { Required, Authorizing, Authorized, Cancelled, Failed, Superseded }
internal sealed record TunAuthorizationResult(TunAuthorizationState State, string? Password = null);

internal sealed class TunAuthorization
{
    private Task<TunAuthorizationResult>? _pending;
    private int _generation;
    public TunAuthorizationState State { get; private set; } = TunAuthorizationState.Required;
    public void Invalidate() { _generation++; State = TunAuthorizationState.Required; }

    public Task<TunAuthorizationResult> RequestAsync(Func<Task<string?>> prompt,
        Func<string, CancellationToken, Task<bool>> validate, CancellationToken token)
        => _pending ??= RunAsync(prompt, validate, token);

    private async Task<TunAuthorizationResult> RunAsync(Func<Task<string?>> prompt,
        Func<string, CancellationToken, Task<bool>> validate, CancellationToken token)
    {
        var generation = _generation;
        State = TunAuthorizationState.Authorizing;
        await Task.Yield();
        try
        {
            var password = await prompt().WaitAsync(token);
            if (generation != _generation) return new(TunAuthorizationState.Superseded);
            if (string.IsNullOrEmpty(password)) return new(State = TunAuthorizationState.Cancelled);
            var valid = await validate(password, token);
            token.ThrowIfCancellationRequested();
            if (generation != _generation) return new(TunAuthorizationState.Superseded);
            return new(State = valid ? TunAuthorizationState.Authorized : TunAuthorizationState.Failed, valid ? password : null);
        }
        catch (OperationCanceledException) { return new(State = TunAuthorizationState.Superseded); }
        catch { State = TunAuthorizationState.Failed; throw; }
        finally { _pending = null; }
    }
}
