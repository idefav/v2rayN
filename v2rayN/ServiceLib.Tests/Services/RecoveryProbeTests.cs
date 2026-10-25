using System.Net.Http;

namespace ServiceLib.Tests.Services;

public class RecoveryProbeTests
{
    [Test]
    [Arguments(204, true)]
    [Arguments(302, false)]
    [Arguments(403, false)]
    [Arguments(503, false)]
    public async Task UsesRunningSocksPortAndRemoteDnsAndChecksHttpStatus(int status, bool expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = ServeAsync(listener, status, timeout.Token);
        var context = new RecoveryContext("fixture", ((IPEndPoint)listener.LocalEndpoint).Port, "http://remote-dns-fixture.invalid/check", 1);
        var success = await RecoveryProbe.ProbeAsync(context, timeout.Token, fallback: context.TestUrl);
        await serve;
        if (success.Success != expected || success.Targets.Single().StatusCode != status) throw new Exception("probe bypassed proxy or accepted an unsuccessful response");
    }

    [Test]
    [Arguments("http://\u200d/private?token=fixture-secret")]
    [Arguments("http://\ufffd/private?token=fixture-secret")]
    [Arguments("http://\uffff/private?token=fixture-secret")]
    [Arguments("file:///private/fixture-secret")]
    public async Task InvalidTargetCannotPoisonSuccessfulFallback(string invalidTarget)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = ServeAsync(listener, 204, timeout.Token);
        var logs = new List<string>();
        var context = new RecoveryContext("fixture", ((IPEndPoint)listener.LocalEndpoint).Port, invalidTarget, 1);
        var result = await RecoveryProbe.ProbeAsync(context, timeout.Token, logs.Add,
            fallback: "http://remote-dns-fixture.invalid/check");
        await server;
        if (!result.Success || !result.Targets.Any(t => t.Outcome == ProbeOutcome.InvalidTarget && t.Host == "<invalid>"))
            throw new Exception("invalid target interrupted fallback probe");
        if (logs.Any(l => l.Contains("private") || l.Contains("fixture-secret")))
            throw new Exception("invalid target exposed URL content");
    }

    [Test]
    public async Task ProbeHonorsCancellationWhileSocksServerStalls()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var context = new RecoveryContext("fixture", ((IPEndPoint)listener.LocalEndpoint).Port, "https://remote-dns-fixture.invalid/check", 1);
        var logs = new List<string>();
        try
        {
            await RecoveryProbe.ProbeAsync(context, timeout.Token, logs.Add, fallback: context.TestUrl);
            throw new Exception("cancelled probe completed normally");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        if (logs.Count != 0) throw new Exception("caller cancellation was logged as a probe failure");
    }

    [Test]
    public async Task SuccessfulFallbackCancelsSiblingAndLogsOnlyHostnames()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Serve()
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = client.GetStream();
            var greeting = new byte[3]; await stream.ReadExactlyAsync(greeting, timeout.Token);
            await stream.WriteAsync(new byte[] { 5, 0 }, timeout.Token);
            var header = new byte[5]; await stream.ReadExactlyAsync(header, timeout.Token);
            var destination = new byte[header[4] + 2]; await stream.ReadExactlyAsync(destination, timeout.Token);
            var host = Encoding.ASCII.GetString(destination, 0, destination.Length - 2);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, timeout.Token);
            if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult();
            await both.Task.WaitAsync(timeout.Token);
            if (host == "stalled.invalid")
            {
                // Keep the HTTP response pending until the caller cancels this fixture.
                try { await Task.Delay(Timeout.Infinite, timeout.Token); }
                catch (OperationCanceledException) { }
            }
            else
            {
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 Fixture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
            }
        }
        var servers = Task.WhenAll(Serve(), Serve());
        var logs = new List<string>();
        var context = new RecoveryContext("fixture", ((IPEndPoint)listener.LocalEndpoint).Port,
            "http://user:private@stalled.invalid/private?token=secret", 1);
        RecoveryProbeResult result;
        try { result = await RecoveryProbe.ProbeAsync(context, timeout.Token, logs.Add, "http://good.invalid/check"); }
        finally { timeout.Cancel(); await servers; }
        if (!result.Success || !result.Targets.Any(t => t.Outcome == ProbeOutcome.Cancelled)
            || logs.Any(l => l.Contains("private") || l.Contains("secret") || l.Contains("user") || l.Contains("failed=")))
            throw new Exception("successful fallback leaked a URL or logged sibling cancellation as failure");
    }

    [Test]
    public async Task AllTargetsTimeoutWithStructuredResult()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new RecoveryContext("fixture", ((IPEndPoint)listener.LocalEndpoint).Port, "https://stalled.invalid/", 1);
        var result = await RecoveryProbe.ProbeAsync(context, timeout.Token, fallback: context.TestUrl);
        if (result.Success || result.Targets.Single().Outcome != ProbeOutcome.Timeout)
            throw new Exception("timed out request not classified");
    }

    [Test]
    public void TransportFailuresAreClassifiedWithoutParsingSensitiveMessages()
    {
        foreach (var pair in new[]
        {
            (HttpRequestError.NameResolutionError, ProbeOutcome.Dns),
            (HttpRequestError.ConnectionError, ProbeOutcome.Connection),
            (HttpRequestError.SecureConnectionError, ProbeOutcome.Tls),
            (HttpRequestError.Unknown, ProbeOutcome.Transport)
        })
            if (RecoveryProbe.Classify(new HttpRequestException(pair.Item1, "sensitive URL")) != pair.Item2)
                throw new Exception("wrong transport classification");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task LocalCheckRequiresValidSocksHandshake(bool valid)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var greeting = new byte[3]; await client.GetStream().ReadExactlyAsync(greeting, timeout.Token);
            await client.GetStream().WriteAsync(new byte[] { 5, valid ? (byte)0 : (byte)255 }, timeout.Token);
        });
        var result = await RecoveryProbe.CheckSocksAsync(((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        await server;
        if (result != (valid ? LocalHealth.Healthy : LocalHealth.Failed)) throw new Exception("SOCKS protocol not validated");
    }

    [Test]
    [Arguments(SocketError.ConnectionRefused, true)]
    [Arguments(SocketError.ConnectionReset, true)]
    [Arguments(SocketError.TimedOut, true)]
    [Arguments(SocketError.TooManyOpenSockets, false)]
    [Arguments(SocketError.NoBufferSpaceAvailable, false)]
    [Arguments(SocketError.AccessDenied, false)]
    public void LocalResourceOrPermissionErrorsDoNotAuthorizeRestart(SocketError error, bool failed)
    {
        var result = RecoveryProbe.ClassifySocksFailure(error);
        if (result != (failed ? LocalHealth.Failed : LocalHealth.Unknown))
            throw new Exception("local OS failure was confused with an unavailable SOCKS service");
        var local = new LocalRecoveryResult(LocalHealth.Healthy, LocalHealth.Healthy, result);
        if ((local.Failure != null) != failed) throw new Exception("unknown local state authorized restart");
    }

    [Test]
    public async Task ProcessExitingDuringSocksHandshakeIsNotReportedHealthy()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = true;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var greeting = new byte[3]; await client.GetStream().ReadExactlyAsync(greeting, timeout.Token);
            running = false;
            await client.GetStream().WriteAsync(new byte[] { 5, 0 }, timeout.Token);
        });
        var context = new RecoveryContext("fixture", ((IPEndPoint)listener.LocalEndpoint).Port, "https://fixture.invalid", 1);
        var result = await RecoveryProbe.LocalAsync(context, timeout.Token,
            () => (running ? LocalHealth.Healthy : LocalHealth.Failed, LocalHealth.Healthy));
        await server;
        if (result.Healthy || result.Main != LocalHealth.Failed || result.Socks != LocalHealth.Healthy)
            throw new Exception("process state sampled before the pending handshake");
    }

    [Test]
    public async Task LocalHandshakeTimeoutIsFailureAndCallerCancellationPropagates()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        if (await RecoveryProbe.CheckSocksAsync(port, CancellationToken.None) != LocalHealth.Failed)
            throw new Exception("stalled SOCKS handshake accepted");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try { await RecoveryProbe.CheckSocksAsync(port, cancel.Token); throw new Exception("cancellation swallowed"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    [Test]
    public async Task PersistentSocksProtocolFailureRestartsOnceAndVerifiesHandshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var healthy = false;
        var server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    var greeting = new byte[3]; await client.GetStream().ReadExactlyAsync(greeting, lifetime.Token);
                    await client.GetStream().WriteAsync(new byte[] { 5, healthy ? (byte)0 : (byte)255 }, lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        var context = new RecoveryContext("fixture", ((IPEndPoint)listener.LocalEndpoint).Port, "https://fixture.invalid", 1);
        var restarts = 0;
        var service = new ConnectionRecoveryService(() => context, _ => Task.FromResult<string?>("fixture network"),
            (_, _) => Task.FromResult(new ProxyCheckResult(true, "fixture")), (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(new RecoveryProbeResult([new("fixture.invalid", ProbeOutcome.Success, 204, 0)])),
            async (request, ct) =>
            {
                if (!await request.Confirm(ct)) return false;
                if (!request.Reason.Contains("SOCKS")) throw new Exception("wrong local fault evidence");
                restarts++; healthy = true; return true;
            }, _ => { }, options: new() { StableDelay = TimeSpan.Zero, RetryDelay = TimeSpan.Zero },
            localHealth: async (_, ct) => new(LocalHealth.Healthy, LocalHealth.Healthy, await RecoveryProbe.CheckSocksAsync(context.Port, ct)));
        try
        {
            await service.CheckAsync("protocol fixture", lifetime.Token);
            if (restarts != 1 || service.State != RecoveryState.Idle) throw new Exception("persistent SOCKS fault did not recover");
        }
        finally { lifetime.Cancel(); await server; }
    }

    private static async Task ServeAsync(TcpListener listener, int status, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        var stream = client.GetStream();
        var greeting = new byte[3]; await stream.ReadExactlyAsync(greeting, token);
        await stream.WriteAsync(new byte[] { 5, 0 }, token);
        var header = new byte[5]; await stream.ReadExactlyAsync(header, token);
        if (header[0] != 5 || header[1] != 1 || header[3] != 3) throw new Exception("SOCKS remote DNS was not used");
        var destination = new byte[header[4] + 2]; await stream.ReadExactlyAsync(destination, token);
        if (Encoding.ASCII.GetString(destination, 0, destination.Length - 2) != "remote-dns-fixture.invalid")
            throw new Exception("wrong SOCKS destination");
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, token);
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
    }
}
