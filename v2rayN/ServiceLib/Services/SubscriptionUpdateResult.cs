namespace ServiceLib.Services;

internal enum SubscriptionAttemptStatus { Succeeded, Failed, Cancelled, Skipped }

internal sealed record SubscriptionAttempt(string Id, SubscriptionAttemptStatus Status);

/// <summary>Import outcome is independent of presentation and connection application.</summary>
internal sealed record SubscriptionUpdateResult(IReadOnlyList<SubscriptionAttempt> Attempts, string? NotificationError = null)
{
    internal bool Imported => Attempts.Any(x => x.Status == SubscriptionAttemptStatus.Succeeded);
}

/// <summary>Per-subscription scheduling, with a session fallback if timestamp persistence fails.</summary>
internal sealed class SubscriptionUpdateSchedule(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, long> _completed = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task RunAsync(IEnumerable<SubItem> subscriptions,
        Func<SubItem, CancellationToken, Task<SubscriptionUpdateResult>> update,
        Func<string, long, Task> saveTime,
        Func<SubscriptionUpdateResult, CancellationToken, Task> notify,
        Action<string, Exception> log, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            foreach (var item in subscriptions)
            {
                token.ThrowIfCancellationRequested();
                var now = _clock.GetUtcNow().ToUnixTimeSeconds();
                var last = Math.Max(item.UpdateTime, _completed.GetValueOrDefault(item.Id));
                if (!item.Enabled || item.AutoUpdateInterval <= 0 || now - last < (long)item.AutoUpdateInterval * 60) continue;
                SubscriptionUpdateResult result;
                try { result = await update(item, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    log("Subscription attempt", ex);
                    result = new([new(item.Id, SubscriptionAttemptStatus.Failed)]);
                }
                // Commit scheduling before optional UI / connection callbacks. Do not rewrite the subscription.
                if (result.Attempts.Any(x => x.Id == item.Id && x.Status is SubscriptionAttemptStatus.Succeeded or SubscriptionAttemptStatus.Failed))
                {
                    _completed[item.Id] = now;
                    try { await saveTime(item.Id, now); }
                    catch (Exception ex) { log("Subscription timestamp", ex); }
                }
                try { await notify(result, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { log("Subscription post-processing", ex); }
            }
        }
        finally { _gate.Release(); }
    }
}
