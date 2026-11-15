namespace ServiceLib.Services;

internal enum PortHandoffState { Idle, Stopping, Waiting, Starting, Completed, Failed, Cancelled }

internal static class PortHandoff
{
    internal static async Task WaitAsync(IReadOnlyList<CoreListener> endpoints, CancellationToken token,
        Action<TimeSpan>? progress = null, TimeProvider? clock = null,
        Func<CoreListener, SocketError>? probe = null, Func<IReadOnlyList<CoreListener>>? listeners = null)
    {
        clock ??= TimeProvider.System;
        probe ??= endpoint => ListenerBindProbe.Check(endpoint.Address, endpoint.Port, endpoint.Protocol);
        listeners ??= CoreListenerCheck.ActiveListeners;
        var start = clock.GetTimestamp();
        var lastLog = -1;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var active = listeners();
            var blocked = false;
            foreach (var endpoint in endpoints)
            {
                var error = probe(endpoint);
                if (error == SocketError.Success) continue;
                if (active.Any(other => CoreListenerCheck.Overlaps(endpoint, other)))
                    throw new IOException($"端口交接遇到监听占用 / Listener appeared during handoff: {endpoint}");
                if (error != SocketError.AddressAlreadyInUse)
                    throw new IOException($"普通用户端口交接失败 / Unprivileged bind failed: {endpoint}: {error}");
                blocked = true;
            }
            if (!blocked) return;
            var elapsed = clock.GetElapsedTime(start);
            if ((int)elapsed.TotalSeconds != lastLog)
            {
                lastLog = (int)elapsed.TotalSeconds;
                Logging.SaveLog($"PortHandoff target=unprivileged elapsedSeconds={lastLog} endpoints={string.Join(",", endpoints)}");
            }
            progress?.Invoke(elapsed);
            if (elapsed >= TimeSpan.FromSeconds(90))
                throw new TimeoutException($"等待普通模式端口释放超时（90 秒），请手动重试 / Unprivileged port handoff timed out: {string.Join(", ", endpoints)}");
            await Task.Delay(TimeSpan.FromMilliseconds(250), clock, token);
        }
    }
}
