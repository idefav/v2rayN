using System.Threading.Channels;

namespace ServiceLib.Services;

internal sealed record RecoveryContext(string NodeId, int Port, string TestUrl, long Revision);
internal sealed record ProxyCheckResult(bool Matches, string Detail);
internal enum RecoveryState { Idle, Checking, Recovering, Offline, Failed, Degraded }
internal enum LocalHealth { Healthy, Failed, Unknown }
internal sealed record LocalRecoveryResult(LocalHealth Main, LocalHealth Pre, LocalHealth Socks)
{
    public bool Healthy => Main == LocalHealth.Healthy && Pre == LocalHealth.Healthy && Socks == LocalHealth.Healthy;
    public string? Failure => Main == LocalHealth.Failed ? "主内核进程已退出"
        : Pre == LocalHealth.Failed ? "必要前置内核已退出"
        : Socks == LocalHealth.Failed ? "本地 SOCKS 连续两次握手失败" : null;
}
internal sealed record RecoveryRestartRequest(RecoveryContext Context, string Reason,
    Func<CancellationToken, Task<bool>> Confirm);

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
    Func<RecoveryContext, CancellationToken, Task<RecoveryProbeResult>> probe,
    Func<RecoveryRestartRequest, CancellationToken, Task<bool>> reload,
    Action<string> log,
    TimeProvider? timeProvider = null,
    RecoveryOptions? options = null,
    Func<RecoveryContext, CancellationToken, Task<LocalRecoveryResult>>? localHealth = null)
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
    private long _checkId;
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
                catch (OperationCanceledException) { Log("decision=cancelled check superseded or stopped"); }
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
        if (error != null) Log(error);
        if (changed) Changed?.Invoke();
    }

    // Also used by deterministic tests with injected network, proxy, core and clock.
    internal async Task CheckAsync(string reason, CancellationToken token)
    {
        var snapshot = context();
        if (snapshot == null) { SetState(RecoveryState.Idle); return; }
        SetState(RecoveryState.Checking);
        Interlocked.Increment(ref _checkId);
        Log("check trigger=" + reason);
        var before = await network(token).ConfigureAwait(false);
        if (before == null) { Log("decision=preserve network unavailable"); SetState(RecoveryState.Offline); return; }
        await Delay(_options.StableDelay, token).ConfigureAwait(false);
        EnsureCurrent(snapshot, token);
        if (before != await network(token).ConfigureAwait(false))
        {
            RequestCheck("network settling");
            SetState(RecoveryState.Offline);
            return;
        }
        if (!await EnsureProxyAsync(snapshot, token).ConfigureAwait(false)) return;
        var local = await ReadLocal(snapshot, token);
        if (!local.Healthy) { await ConfirmAndRecover(local); return; }
        local = await ProbeAndRecheckLocal();
        if (local != null) await ConfirmAndRecover(local);

        // All paths use the same final local check before reporting an external-only
        // outage. A null result means the check already published its final state.
        async Task<LocalRecoveryResult?> ProbeAndRecheckLocal()
        {
            if ((await Probe(snapshot, token)).Success) { Healthy(); return null; }
            await Delay(_options.RetryDelay, token);
            if (!await SameNetwork()) return null;
            if ((await Probe(snapshot, token)).Success) { Healthy(); return null; }
            if (!await SameNetwork()) return null;
            var latest = await ReadLocal(snapshot, token);
            if (!await SameNetwork()) return null;
            if (!latest.Healthy) return latest;
            Degraded();
            return null;
        }

        async Task ConfirmAndRecover(LocalRecoveryResult first, bool recheckTransient = true)
        {
            if (first.Failure == null) { Unknown(); return; }
            await Delay(_options.RetryDelay, token);
            if (!await SameNetwork()) return;
            var second = await ReadLocal(snapshot, token);
            if (second.Healthy)
            {
                if (!recheckTransient)
                {
                    // Bound work when local health keeps flapping; the next scheduled
                    // check may try again, without falsely claiming external-only failure.
                    Log("decision=preserve local health unstable");
                    SetState(RecoveryState.Degraded, "Local core health unstable; connection preserved.");
                    return;
                }
                var latest = await ProbeAndRecheckLocal();
                if (latest != null) await ConfirmAndRecover(latest, recheckTransient: false);
                return;
            }
            if (second.Failure == null) { Unknown(); return; }
            await Restart(second.Failure);
        }

        async Task<bool> SameNetwork()
        {
            EnsureCurrent(snapshot, token);
            var current = await network(token);
            EnsureCurrent(snapshot, token);
            if (current == before) return true;
            SetState(RecoveryState.Offline);
            Log("decision=preserve network changed");
            return false;
        }

        async Task Restart(string failure)
        {
            if (!await SameNetwork()) return;
            var now = _clock.GetUtcNow();
            while (_restarts.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(5)) _restarts.Dequeue();
            if (_restarts.Count >= 2 || _restarts.TryPeek(out _) && now - _restarts.Last() < TimeSpan.FromSeconds(60))
            {
                SetState(RecoveryState.Failed, "Core connection unavailable; automatic restart limit reached.");
                return;
            }
            SetState(RecoveryState.Recovering);
            var attempted = false;
            var request = new RecoveryRestartRequest(snapshot, failure, async ct =>
            {
                ct.ThrowIfCancellationRequested();
                if (!await SameNetwork()) return false;
                var latest = await ReadLocal(snapshot, ct);
                if (latest.Failure != failure) { Log("decision=preserve local fault cleared, changed or unknown before reload"); return false; }
                if (!await SameNetwork()) return false;
                // Count only a restart authorized while holding the reload coordinator lock.
                attempted = true;
                _restarts.Enqueue(_clock.GetUtcNow());
                Log("decision=restart reason=" + failure);
                return true;
            });
            var reloaded = await reload(request, token).ConfigureAwait(false);
            EnsureCurrent(snapshot, token);
            if (!reloaded)
            {
                if (!attempted)
                {
                    EnsureCurrent(snapshot, token);
                    if (State != RecoveryState.Offline) SetState(RecoveryState.Degraded, "Recovery deferred; local state changed.");
                    return;
                }
                SetState(RecoveryState.Failed, "Core restart failed; see core log.");
                return;
            }
            EnsureCurrent(snapshot, token);
            if (!await EnsureProxyAsync(snapshot, token).ConfigureAwait(false)) return;
            var after = await ReadLocal(snapshot, token);
            if (!after.Healthy) { SetState(RecoveryState.Failed, "Local core unavailable after restart."); return; }
            // A fault recurring after the restart is reported, never followed by a
            // second restart within this check.
            if (await ProbeAndRecheckLocal() != null)
                SetState(RecoveryState.Failed, "Local core unavailable after restart.");
        }
    }

    private async Task<RecoveryProbeResult> Probe(RecoveryContext snapshot, CancellationToken token)
    {
        var result = await probe(snapshot, token).ConfigureAwait(false);
        EnsureCurrent(snapshot, token);
        foreach (var target in result.Targets)
            Log($"probe host={target.Host} outcome={target.Outcome} HTTP={target.StatusCode} elapsedMs={target.ElapsedMs}");
        return result;
    }

    private void Log(string message) => log("check=" + _checkId + " " + message);

    private void Degraded()
    {
        Log("decision=preserve external probes unavailable; local core healthy");
        SetState(RecoveryState.Degraded, "External probes unavailable; local core and SOCKS are healthy. Connection preserved.");
    }

    private void Unknown()
    {
        Log("decision=preserve local health unknown");
        SetState(RecoveryState.Degraded, "Local core health unknown; connection preserved.");
    }

    private async Task<LocalRecoveryResult> ReadLocal(RecoveryContext snapshot, CancellationToken token)
    {
        EnsureCurrent(snapshot, token);
        LocalRecoveryResult result;
        try { result = localHealth == null ? new(LocalHealth.Unknown, LocalHealth.Unknown, LocalHealth.Unknown) : await localHealth(snapshot, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log("local check unknown=" + ex.GetType().Name); result = new(LocalHealth.Unknown, LocalHealth.Unknown, LocalHealth.Unknown); }
        EnsureCurrent(snapshot, token);
        Log($"local main={result.Main} pre={result.Pre} socks={result.Socks}");
        return result;
    }

    private void Healthy()
    {
        Log("decision=preserve connection healthy");
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
                EnsureCurrent(snapshot, token);
                Log("system proxy " + actual.Detail);
                if (actual.Matches) return true;
                SetState(RecoveryState.Recovering);
                await applyProxy(snapshot, token).ConfigureAwait(false);
                EnsureCurrent(snapshot, token);
                actual = await readProxy(snapshot.Port, token).ConfigureAwait(false);
                EnsureCurrent(snapshot, token);
                Log("system proxy read-back " + actual.Detail);
                if (actual.Matches) return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || context() != snapshot) { throw; }
            catch (Exception ex) { Log("system proxy verification failed=" + ex.GetType().Name); }
            if (attempt < 2) await Delay(_options.ProxyRetryDelay, token).ConfigureAwait(false);
        }
        SetState(RecoveryState.Failed, "System proxy settings could not be applied or verified; check network permissions.");
        return false;
    }
}
