using System.Threading.Channels;

namespace ServiceLib.Services;

internal sealed record RecoveryContext(string NodeId, int Port, string TestUrl, long Revision);
internal sealed record ProxyCheckResult(bool Matches, string Detail);
internal enum RecoveryState { Idle, Checking, Recovering, Offline, Failed }

internal sealed record RecoveryOptions
{
    public TimeSpan StableDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ProxyRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan Period { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>One process-lifetime worker. Never creates a test core or changes the selected node.</summary>
internal sealed class ConnectionRecoveryService(
    Func<RecoveryContext?> context,
    Func<CancellationToken, Task<string?>> network,
    Func<int, CancellationToken, Task<ProxyCheckResult>> readProxy,
    Func<RecoveryContext, CancellationToken, Task<bool>> applyProxy,
    Func<RecoveryContext, CancellationToken, Task<bool>> probe,
    Func<RecoveryContext, CancellationToken, Task<bool>> reload,
    Action<string> log,
    TimeProvider? timeProvider = null,
    RecoveryOptions? options = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly RecoveryOptions _options = options ?? new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(1);
    private readonly object _sync = new();
    private readonly Queue<DateTimeOffset> _restarts = new();
    private CancellationTokenSource? _checking;
    private Task _worker = Task.CompletedTask;
    private Task _timer = Task.CompletedTask;
    private bool _started;
    private bool _stopping;
    private string _reason = "startup";
    public RecoveryState State { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    public event Action? Stopping;

    public void Start()
    {
        lock (_sync)
        {
            if (_started || _stopping) return;
            _started = true;
            _worker = Task.Run(WorkAsync);
            _timer = Task.Run(TimerAsync);
        }
        RequestCheck("startup");
    }

    public void RequestCheck(string reason, bool interrupt = true)
    {
        lock (_sync)
        {
            if (_stopping || (!interrupt && _checking != null)) return;
            _reason = reason;
            if (interrupt) _checking?.Cancel();
            _requests.Writer.TryWrite(true);
        }
    }

    public async Task StopAsync()
    {
        lock (_sync)
        {
            if (!_stopping)
            {
                _stopping = true;
                _lifetime.Cancel();
                _checking?.Cancel();
                _requests.Writer.TryComplete();
            }
        }
        // Unregister native notifications before the caller clears the system proxy.
        Stopping?.Invoke();
        await Task.WhenAll(_worker, _timer).ConfigureAwait(false);
    }

    private async Task TimerAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_options.Period, _clock);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                RequestCheck("periodic", false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var signal in _requests.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                CancellationTokenSource check;
                string reason;
                lock (_sync)
                {
                    while (_requests.Reader.TryRead(out _)) { }
                    if (_stopping) return;
                    check = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    _checking = check;
                    reason = _reason;
                }
                try { await CheckAsync(reason, check.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // Exception messages from HTTP clients may contain credential-bearing URLs.
                    SetState(RecoveryState.Failed, "Recovery check failed: " + ex.GetType().Name);
                }
                finally
                {
                    lock (_sync) { _checking = null; check.Dispose(); }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, _clock, token);

    private void EnsureCurrent(RecoveryContext snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context() != snapshot) throw new OperationCanceledException("Connection selection changed", token);
    }

    private void SetState(RecoveryState state, string? error = null)
    {
        var changed = State != state || Error != error;
        State = state;
        Error = error;
        if (error != null) log(error);
        if (changed) Changed?.Invoke();
    }

    // Also used by deterministic tests with injected network, proxy, core and clock.
    internal async Task CheckAsync(string reason, CancellationToken token)
    {
        var snapshot = context();
        if (snapshot == null) { SetState(RecoveryState.Idle); return; }
        SetState(RecoveryState.Checking);
        log("check trigger=" + reason);
        var before = await network(token).ConfigureAwait(false);
        if (before == null) { SetState(RecoveryState.Offline); return; }
        await Delay(_options.StableDelay, token).ConfigureAwait(false);
        EnsureCurrent(snapshot, token);
        if (before != await network(token).ConfigureAwait(false))
        {
            RequestCheck("network settling");
            SetState(RecoveryState.Offline);
            return;
        }
        if (!await EnsureProxyAsync(snapshot, token).ConfigureAwait(false)) return;
        if (await probe(snapshot, token).ConfigureAwait(false)) { EnsureCurrent(snapshot, token); Healthy(); return; }
        await Delay(_options.RetryDelay, token).ConfigureAwait(false);
        EnsureCurrent(snapshot, token);
        // A link can disappear during a timed-out request. Do not restart while offline.
        if (before != await network(token).ConfigureAwait(false))
        {
            SetState(RecoveryState.Offline);
            return;
        }
        if (await probe(snapshot, token).ConfigureAwait(false)) { EnsureCurrent(snapshot, token); Healthy(); return; }
        EnsureCurrent(snapshot, token);
        var now = _clock.GetUtcNow();
        while (_restarts.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(5)) _restarts.Dequeue();
        if (_restarts.Count >= 2 || _restarts.TryPeek(out _) && now - _restarts.Last() < TimeSpan.FromSeconds(60))
        {
            SetState(RecoveryState.Failed, "Core connection unavailable; automatic restart limit reached.");
            return;
        }
        SetState(RecoveryState.Recovering);
        _restarts.Enqueue(now);
        log("core connection failed twice; restarting current core");
        if (!await reload(snapshot, token).ConfigureAwait(false))
        {
            SetState(RecoveryState.Failed, "Core restart failed; see core log.");
            return;
        }
        EnsureCurrent(snapshot, token);
        if (!await EnsureProxyAsync(snapshot, token).ConfigureAwait(false)) return;
        if (await probe(snapshot, token).ConfigureAwait(false)) { EnsureCurrent(snapshot, token); Healthy(); }
        else SetState(RecoveryState.Failed, "Core connection unavailable after restart.");
    }

    private void Healthy()
    {
        log("connection healthy");
        SetState(RecoveryState.Idle);
    }

    private async Task<bool> EnsureProxyAsync(RecoveryContext snapshot, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            EnsureCurrent(snapshot, token);
            try
            {
                var actual = await readProxy(snapshot.Port, token).ConfigureAwait(false);
                log("system proxy " + actual.Detail);
                if (actual.Matches) return true;
                SetState(RecoveryState.Recovering);
                await applyProxy(snapshot, token).ConfigureAwait(false);
                EnsureCurrent(snapshot, token);
                actual = await readProxy(snapshot.Port, token).ConfigureAwait(false);
                log("system proxy read-back " + actual.Detail);
                if (actual.Matches) return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { log("system proxy verification failed=" + ex.GetType().Name); }
            if (attempt < 2) await Delay(_options.ProxyRetryDelay, token).ConfigureAwait(false);
        }
        SetState(RecoveryState.Failed, "System proxy settings could not be applied or verified; check network permissions.");
        return false;
    }
}
