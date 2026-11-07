namespace ServiceLib.Tests.Services;

public class CoreListenerCheckTests
{
    [Test]
    public void ReadsProxyAdditionalListenersAndBothApis()
    {
        var listeners = CoreListenerCheck.Read("""
            {"inbounds":[
              {"type":"mixed","listen":"127.0.0.1","listen_port":10808},
              {"type":"socks","listen":"0.0.0.0","listen_port":10809,"users":[{"username":"test","password":"secret"}]},
              {"type":"direct","listen":"::1","listen_port":1053,"network":"udp"},
              {"type":"tun","address":["172.19.0.1/30"]}],
             "experimental":{"clash_api":{"external_controller":"127.0.0.1:9090"},"v2ray_api":{"listen":"[::1]:9091"}}}
            """);
        Check(listeners.Count == 5, "listener set is incomplete");
        Check(listeners.Single(p => p.Port == 10809).Authentication, "SOCKS authentication method lost");
        Check(listeners.Single(p => p.Port == 1053).Protocol == ProtocolType.Udp, "UDP listener misclassified");
        Check(!string.Join(" ", listeners).Contains("secret"), "diagnostics contain credentials");
    }

    [Test]
    public void TransportAndNetworkArraysSelectOnlyActualListeners()
    {
        var listeners = CoreListenerCheck.Read("""
            {"inbounds":[
              {"type":"direct","listen":"127.0.0.1","listen_port":10001,"network":["udp"]},
              {"type":"vmess","listen":"127.0.0.1","listen_port":10002},
              {"type":"vless","listen":"127.0.0.1","listen_port":10003,"transport":{"type":"quic"}}]}
            """);
        Check(listeners.Count == 3, "invented listeners for a transport");
        Check(listeners.Single(p => p.Port == 10001).Protocol == ProtocolType.Udp, "network array ignored");
        Check(listeners.Single(p => p.Port == 10002).Protocol == ProtocolType.Tcp, "UDP incorrectly required for TCP tunnel");
        Check(listeners.Single(p => p.Port == 10003).Protocol == ProtocolType.Udp, "QUIC transport misclassified");
    }

    [Test]
    public void AddressAndProtocolMatching()
    {
        var ipv4 = new CoreListener(IPAddress.Loopback, 10808, ProtocolType.Tcp);
        Check(CoreListenerCheck.Overlaps(ipv4, ipv4 with { Address = IPAddress.Any }), "IPv4 wildcard missed");
        Check(CoreListenerCheck.Overlaps(ipv4, ipv4 with { Address = IPAddress.IPv6Any }), "dual stack wildcard missed");
        Check(CoreListenerCheck.Overlaps(ipv4, ipv4 with { Address = IPAddress.Parse("::ffff:127.0.0.1") }), "IPv4-mapped address missed");
        Check(!CoreListenerCheck.Overlaps(ipv4, ipv4 with { Address = IPAddress.IPv6Loopback }), "different families conflated");
        Check(!CoreListenerCheck.Overlaps(ipv4, ipv4 with { Protocol = ProtocolType.Udp }), "TCP and UDP conflated");
        Check(!CoreListenerCheck.Overlaps(ipv4, ipv4 with { Address = IPAddress.Parse("127.0.0.2") }), "specific addresses conflated");
    }

    [Test]
    public async Task UnknownListenerBlocksLaunchWithoutBeingTerminated()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var denied = false;
        try { await CoreListenerCheck.EnsureAvailableAsync([new(IPAddress.Loopback, port, ProtocolType.Tcp)]); }
        catch (IOException ex) { denied = ex.Message.Contains(port.ToString()); }
        Check(denied, "occupied port did not block launch");
        using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port);
        using var accepted = await listener.AcceptTcpClientAsync();
        Check(accepted.Connected, "unknown listener was changed");
    }

    [Test]
    public async Task UdpListenerIsDetected()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var denied = false;
        try { await CoreListenerCheck.EnsureAvailableAsync([new(IPAddress.Loopback, port, ProtocolType.Udp)]); }
        catch (IOException) { denied = true; }
        Check(denied, "occupied UDP port was missed");
        await CoreListenerCheck.EnsureAvailableAsync([new(IPAddress.Loopback, port, ProtocolType.Tcp)]);
    }

    [Test]
    public async Task IPv6OnlyListenerDoesNotBlockIPv4()
    {
        if (!Socket.OSSupportsIPv6) { Skip.Test("IPv6 unavailable"); return; }
        using var listener = new TcpListener(IPAddress.IPv6Any, 0);
        listener.Server.DualMode = false; listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await CoreListenerCheck.EnsureAvailableAsync([new(IPAddress.Loopback, port, ProtocolType.Tcp)]);
    }

    [Test]
    public async Task ClosedConnectionsAreNotReportedAsListeners()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
        using var accepted = await listener.AcceptTcpClientAsync();
        accepted.Client.Shutdown(SocketShutdown.Send);
        var buffer = new byte[1];
        Check(await client.GetStream().ReadAsync(buffer) == 0, "expected EOF after shutdown");
        client.Close(); accepted.Close(); listener.Stop();
        await CoreListenerCheck.EnsureAvailableAsync([new(IPAddress.Loopback, port, ProtocolType.Tcp)]);
    }

    private static void Check(bool condition, string error) { if (!condition) throw new Exception(error); }
}
