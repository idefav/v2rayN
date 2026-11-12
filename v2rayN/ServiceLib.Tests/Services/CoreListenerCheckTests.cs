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
    public async Task BoundSocketWithoutListenerBlocksUntilReleased()
    {
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        // .NET enables native SO_REUSEADDR during TCP Bind on Unix. Disable it
        // afterwards so this is a real reservation even on Linux before Listen.
        reserved.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        var port = ((IPEndPoint)reserved.LocalEndPoint!).Port;
        Check(!CoreListenerCheck.ActiveListeners().Any(p => p.Port == port && p.Protocol == ProtocolType.Tcp),
            "fixture unexpectedly appears in the listener table");
        var error = await BindError(port);
        Check(error?.Contains("Cannot bind listener") == true && error.Contains("AddressAlreadyInUse") && error.Contains(port.ToString()),
            "bound-only socket was missed or misreported as a listening process");
        reserved.Listen(1);
        Check(reserved.IsBound, "unknown socket was modified by the check");
        reserved.Close();
        await CoreListenerCheck.EnsureAvailableAsync([new(IPAddress.Loopback, port, ProtocolType.Tcp)]);
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(); // The successful probe must release its own socket.
    }

    [Test]
    public async Task ElevatedCheckAlwaysUsesTargetLaunchCredentials()
    {
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        reserved.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        var endpoint = new CoreListener(IPAddress.Loopback, ((IPEndPoint)reserved.LocalEndPoint!).Port, ProtocolType.Tcp);
        var probes = 0;
        await CoreListenerCheck.EnsureAvailableAsync([endpoint], (target, token) =>
        {
            Check(target == endpoint && !token.IsCancellationRequested, "wrong privileged probe request");
            probes++;
            return Task.FromResult(SocketError.Success);
        });
        Check(probes == 1, "failed local bind was not rechecked with launch credentials");
        var denied = false;
        try
        {
            await CoreListenerCheck.EnsureAvailableAsync([endpoint], (_, _) => throw new IOException("elevation refused"));
        }
        catch (IOException ex) { denied = ex.Message.Contains("elevation refused"); }
        Check(denied, "elevation failure was treated as an available port");
        reserved.Listen(1);
        try { await CoreListenerCheck.EnsureAvailableAsync([endpoint], (_, _) => { probes++; return Task.FromResult(SocketError.AddressAlreadyInUse); }); }
        catch (IOException) { }
        Check(probes >= 2, "a known listener was not checked with target credentials");
        var previousProbes = probes;
        reserved.Close();
        await CoreListenerCheck.EnsureAvailableAsync([endpoint], (_, _) => { probes++; return Task.FromResult(SocketError.Success); });
        Check(probes == previousProbes + 1, "an available port was not checked with target credentials");
    }

    [Test]
    public async Task DualStackWildcardMatchesActualIpv4BindRules()
    {
        if (!Socket.OSSupportsIPv6) { Skip.Test("IPv6 unavailable"); return; }
        using var listener = new TcpListener(IPAddress.IPv6Any, 0);
        listener.Server.DualMode = true; listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // BSD permits a separate, more-specific IPv4 listener in cases where
        // Linux rejects it. Compare against an independent real listener, not
        // an assumption that every IPv6 wildcard owns every IPv4 address.
        var denied = false;
        using (var ipv4 = new TcpListener(IPAddress.Loopback, port))
        {
            if (OperatingSystem.IsWindows()) ipv4.ExclusiveAddressUse = false;
            try { ipv4.Start(); } catch (SocketException) { denied = true; }
        }
        Check((await BindError(port) != null) == denied, "wildcard conflict disagreed with the kernel's actual bind rules");
    }

    [Test]
    public async Task ProbeNeverSharesAReusableLiveListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Check(await BindError(port) != null, "probe shared another process's live listener");
    }

    [Test]
    public async Task CancelledCheckDoesNotProbeOrStartAnything()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var probes = 0;
        var observed = false;
        try
        {
            await CoreListenerCheck.EnsureAvailableAsync([new(IPAddress.Loopback, 10808, ProtocolType.Tcp)],
                (_, _) => { probes++; return Task.FromResult(SocketError.Success); }, cancelled.Token);
        }
        catch (OperationCanceledException) { observed = true; }
        Check(observed && probes == 0, "cancelled check continued probing");
    }

    [Test]
    public void HelperProtocolRejectsInvalidAndAmbiguousResults()
    {
        using var output = new StringWriter();
        Check(ListenerBindProbe.Run([ListenerBindProbe.Command, "tcp", "127.0.0.1", "0"], output) == 2
            && output.ToString() == "", "invalid command entered the helper's normal execution path");
        Check(ListenerBindProbe.ParseResult(ListenerBindProbe.Marker + "\tAddressAlreadyInUse\n") == SocketError.AddressAlreadyInUse,
            "busy response was lost");
        foreach (var response in new[] { "", "Success", ListenerBindProbe.Marker + "\t999999", ListenerBindProbe.Marker + "\tSuccess\nextra" })
        {
            var denied = false;
            try { ListenerBindProbe.ParseResult(response); } catch (IOException) { denied = true; }
            Check(denied, "invalid helper response was accepted");
        }
    }

    [Test]
    public async Task PackagedHelperChecksRealBoundSocketAndExits()
    {
        var helper = Environment.GetEnvironmentVariable("V2RAYN_TEST_BIND_HELPER");
        if (string.IsNullOrWhiteSpace(helper)) { Skip.Test("packaged probe helper not configured"); return; }
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        reserved.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        var port = ((IPEndPoint)reserved.LocalEndPoint!).Port;
        Check(await RunHelper(helper, port) == SocketError.AddressAlreadyInUse, "real helper missed bound-only socket");
        reserved.Close();
        Check(await RunHelper(helper, port) == SocketError.Success, "real helper kept the port reserved");
    }

    [Test]
    public async Task ElevatedHelperChecksRealBoundSocketAndExits()
    {
        var helper = Environment.GetEnvironmentVariable("V2RAYN_TEST_BIND_HELPER");
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(helper)
            || Environment.GetEnvironmentVariable("V2RAYN_TEST_BIND_SUDO") != "1")
        { Skip.Test("isolated sudo probe not enabled"); return; }
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        reserved.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        var endpoint = new CoreListener(IPAddress.Loopback, ((IPEndPoint)reserved.LocalEndPoint!).Port, ProtocolType.Tcp);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Check(await CoreAdminManager.CheckListenerAsSudoAsync(endpoint, timeout.Token, helper) == SocketError.AddressAlreadyInUse,
            "elevated helper missed the reserved port");
        reserved.Close();
        Check(await CoreAdminManager.CheckListenerAsSudoAsync(endpoint, timeout.Token, helper) == SocketError.Success,
            "elevated helper did not release its socket");
    }

    [Test]
    public async Task MacRootTimeWaitRequiresMatchingLaunchCredentials()
    {
        var helper = Environment.GetEnvironmentVariable("V2RAYN_TEST_BIND_HELPER");
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(helper)
            || Environment.GetEnvironmentVariable("V2RAYN_TEST_BIND_SUDO") != "1")
        { Skip.Test("isolated macOS cross-UID probe not enabled"); return; }
        using var fixture = new Process { StartInfo = new("/usr/bin/sudo") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var argument in new[] { "-n", "--", "/usr/bin/python3", Path.Combine(AppContext.BaseDirectory, "Fixtures", "root_time_wait.py") })
            fixture.StartInfo.ArgumentList.Add(argument);
        fixture.Start();
        var output = fixture.StandardOutput.ReadToEndAsync();
        var error = fixture.StandardError.ReadToEndAsync();
        try { await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
        Check(fixture.ExitCode == 0, "root TIME_WAIT fixture failed: " + await error);
        var port = int.Parse((await output).Trim());
        var endpoint = new CoreListener(IPAddress.Loopback, port, ProtocolType.Tcp);
        Check(!CoreListenerCheck.ActiveListeners().Any(p => p.Port == port && p.Protocol == ProtocolType.Tcp), "root listener did not exit");
        Check(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Any(p => p.LocalEndPoint.Port == port && p.State == TcpState.TimeWait),
            "fixture did not leave TIME_WAIT");
        Check(await BindError(port) != null, "ordinary process unexpectedly reused root TIME_WAIT");
        await CoreListenerCheck.EnsureAvailableAsync([endpoint], (target, token) => CoreAdminManager.CheckListenerAsSudoAsync(target, token, helper));
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

    private static async Task<string?> BindError(int port)
    {
        try { await CoreListenerCheck.EnsureAvailableAsync([new(IPAddress.Loopback, port, ProtocolType.Tcp)]); }
        catch (IOException ex) { return ex.Message; }
        return null;
    }

    private static async Task<SocketError> RunHelper(string helper, int port)
    {
        using var process = new Process { StartInfo = new(helper) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var argument in new[] { ListenerBindProbe.Command, "tcp", "127.0.0.1", port.ToString() }) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Check(process.ExitCode == 0, "helper command failed: " + await error);
        return ListenerBindProbe.ParseResult(await output);
    }

    private static void Check(bool condition, string error) { if (!condition) throw new Exception(error); }
}
