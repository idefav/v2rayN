namespace ServiceLib.Tests.Services;

public class RecoveryProbeTests
{
    [Test]
    [Arguments(204, true)]
    [Arguments(302, false)]
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
        if (success != expected) throw new Exception("probe bypassed proxy or accepted an unsuccessful response");
    }

    [Test]
    public async Task ProbeHonorsCancellationWhileSocksServerStalls()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var context = new RecoveryContext("fixture", ((IPEndPoint)listener.LocalEndpoint).Port, "https://remote-dns-fixture.invalid/check", 1);
        try
        {
            await RecoveryProbe.ProbeAsync(context, timeout.Token, fallback: context.TestUrl);
            throw new Exception("cancelled probe completed normally");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
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
