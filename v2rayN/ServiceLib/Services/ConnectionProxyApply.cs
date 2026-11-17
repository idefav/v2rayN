namespace ServiceLib.Services;

internal sealed record ProxyApplyRequest(Config Config, int Port)
{
    internal static ProxyApplyRequest Capture(Config config, int port) => new(JsonUtils.DeepCopy(config), port);
}

internal sealed record ProxyApplyOutcome(bool? Matches, string? Detail);

internal static class ConnectionProxyApply
{
    internal static async Task<ProxyApplyOutcome> EnsureAsync(ProxyApplyRequest request, CancellationToken token,
        Func<ProxyApplyRequest, CancellationToken, Task<bool>> apply,
        Func<ProxyApplyRequest, CancellationToken, Task<bool?>> inspect,
        Action<ProxyApplyOutcome> publish)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (await inspect(request, token) == true)
            {
                var matched = new ProxyApplyOutcome(true, null); publish(matched); return matched;
            }
            token.ThrowIfCancellationRequested();
            var applied = await apply(request, token);
            token.ThrowIfCancellationRequested();
            var actual = await inspect(request, token);
            var matches = actual ?? applied;
            var result = new ProxyApplyOutcome(matches,
                matches ? null : "系统代理尚未应用 / System proxy is not applied.");
            publish(result); return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            using var readback = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            bool? matches = null;
            try { matches = await inspect(request, readback.Token); }
            catch (Exception ex) { Logging.SaveLog("Cancelled proxy read-back", ex); }
            publish(new(matches, matches == true ? null : "操作已取消，系统代理未匹配或状态未确认 / Cancelled; system proxy unmatched or unconfirmed."));
            throw;
        }
    }
}
