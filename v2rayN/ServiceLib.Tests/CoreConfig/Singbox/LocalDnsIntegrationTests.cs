using System.Net.Http;

namespace ServiceLib.Tests.CoreConfig.Singbox;

[NotInParallel]
public class LocalDnsIntegrationTests
{
    [Test]
    public async Task MdnsNameReachesLanServiceWithGlobalModeAndPublicDnsUnavailable()
    {
        var singbox = Environment.GetEnvironmentVariable("MDNS_TEST_SINGBOX");
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(singbox))
        {
            Skip.Test("Set MDNS_TEST_SINGBOX on macOS for the isolated Bonjour integration test.");
            return;
        }

        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .First(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        var name = "v2rayn-mdns-" + Guid.NewGuid().ToString("N");
        var host = name + ".local";
        var directory = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(directory);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var http = new TcpListener(IPAddress.Any, 0);
        http.Start();
        var port = ((IPEndPoint)http.LocalEndpoint).Port;
        var serving = ServeAsync(http, stop.Token);
        using var registration = Start("/usr/bin/dns-sd", "-P", name, "_http._tcp", "local", port.ToString(), host, address.ToString());
        Process? core = null;
        Task<string>? errors = null;
        try
        {
            // Allow Bonjour's initial multicast probes to complete. dns-sd buffers
            // its redirected output, so its printed callback is not a readiness signal.
            await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);
            if (registration.HasExited)
                throw new Exception("Bonjour registration failed: " + await registration.StandardError.ReadToEndAsync());

            var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
            config.SimpleDNSItem.DirectDNS = "127.0.0.1:9";
            config.SimpleDNSItem.RemoteDNS = "127.0.0.1:9";
            config.SimpleDNSItem.FakeIP = true;
            CoreConfigTestFactory.BindAppManagerConfig(config);
            var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);
            node.Port = 9; // The proxy and unicast DNS must never handle the LAN request.
            var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);
            var generated = new CoreConfigSingboxService(context).GenerateClientConfigContent();
            if (!generated.Success) throw new Exception(generated.Msg);
            var content = JsonNode.Parse(generated.Data!.ToString()!)!;
            content["log"] = new JsonObject { ["level"] = "debug" };
            var proxyPort = FreePort();
            content["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "mixed", ["listen"] = "127.0.0.1", ["listen_port"] = proxyPort,
            });
            content["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{FreePort()}", ["default_mode"] = "Global",
                },
            };
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, content.ToJsonString(), stop.Token);
            core = Start(singbox, "run", "-c", path);
            errors = core.StandardError.ReadToEndAsync();
            if (!await RecoveryProbe.WaitForListenerAsync(proxyPort, stop.Token))
                throw new Exception("sing-box did not listen: " + (core.HasExited ? await errors : "startup timeout"));
            using var client = new HttpClient(new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{proxyPort}"), UseProxy = true,
            }) { Timeout = TimeSpan.FromSeconds(10) };
            foreach (var domain in new[] { host, host + "." })
            {
                using var response = await client.GetAsync($"http://{domain}:{port}/", stop.Token);
                if (response.StatusCode != HttpStatusCode.NoContent)
                    throw new Exception("mDNS LAN request failed: " + response.StatusCode);
            }
            core.Kill();
            await core.WaitForExitAsync(stop.Token);
            if ((await errors).Contains("NXDOMAIN", StringComparison.OrdinalIgnoreCase))
                throw new Exception("LAN requests still reached a unicast resolver");
        }
        catch (Exception error)
        {
            if (core != null && !core.HasExited) { core.Kill(); await core.WaitForExitAsync(); }
            throw new Exception("mDNS integration failed. Core log: " + (errors == null ? "not started" : await errors), error);
        }
        finally
        {
            stop.Cancel();
            http.Stop();
            await serving;
            if (core != null)
            {
                if (!core.HasExited) { core.Kill(); await core.WaitForExitAsync(); }
                core.Dispose();
            }
            if (!registration.HasExited) { registration.Kill(); await registration.WaitForExitAsync(); }
            Directory.Delete(directory, true);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Process Start(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken token)
    {
        var connections = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                connections.Add(RespondAsync(await listener.AcceptTcpClientAsync(token)));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(connections); }

        async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var reader = new StreamReader(client.GetStream(), leaveOpen: true);
                    if (await reader.ReadLineAsync(token) == null) return;
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
                    await client.GetStream().WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
    }
}
