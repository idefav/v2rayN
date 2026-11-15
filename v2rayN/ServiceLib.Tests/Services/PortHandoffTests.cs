namespace ServiceLib.Tests.Services;

public class PortHandoffTests
{
    private sealed class Clock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    [Test]
    public async Task TotalBudgetIsNinetySeconds()
    {
        var clock = new Clock();
        var endpoint = new CoreListener(IPAddress.Loopback, 34567, ProtocolType.Tcp);
        try
        {
            await PortHandoff.WaitAsync([endpoint], default, clock: clock,
                probe: _ => { clock.Seconds += 30; return SocketError.AddressAlreadyInUse; }, listeners: () => []);
            throw new Exception("Deadline ignored");
        }
        catch (TimeoutException) { if (clock.Seconds != 90) throw new Exception("Wrong handoff deadline"); }
    }
    [Test]
    public async Task WaitsForRealBoundSocketToRelease()
    {
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        // .NET's Unix Bind enables SO_REUSEADDR. Disable it after binding so
        // this fixture genuinely reserves the port without listening on Linux.
        reserved.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        var endpoint = new CoreListener(IPAddress.Loopback, ((IPEndPoint)reserved.LocalEndPoint!).Port, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var progress = 0;
        var task = PortHandoff.WaitAsync([endpoint], timeout.Token, _ => progress++);
        await Task.Delay(300);
        if (task.IsCompleted) throw new Exception("Bound-only socket incorrectly accepted");
        reserved.Close();
        await task;
        if (progress == 0) throw new Exception("No handoff progress reported");
    }

    [Test]
    public async Task CancellationDoesNotWaitForNinetySeconds()
    {
        using var token = new CancellationTokenSource();
        var endpoint = new CoreListener(IPAddress.Loopback, 34567, ProtocolType.Tcp);
        var task = PortHandoff.WaitAsync([endpoint], token.Token, probe: _ => SocketError.AddressAlreadyInUse, listeners: () => []);
        token.Cancel();
        try { await task.WaitAsync(TimeSpan.FromSeconds(1)); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
    }

    [Test]
    public async Task NewListenerIsNotKilledOrIgnored()
    {
        var endpoint = new CoreListener(IPAddress.Loopback, 34567, ProtocolType.Tcp);
        try
        {
            await PortHandoff.WaitAsync([endpoint], default, probe: _ => SocketError.AddressAlreadyInUse, listeners: () => [endpoint]);
            throw new Exception("Listener accepted");
        }
        catch (IOException) { }
    }
}
