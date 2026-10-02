using System.Net.Http;

namespace ServiceLib.Tests.CoreConfig.Singbox;

[NotInParallel]
public class SshIntegrationTests
{
    private static readonly ConcurrentDictionary<int, StringBuilder> Errors = new();
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private static Process Start(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var process = Process.Start(info)!;
        var errors = new StringBuilder();
        Errors[process.Id] = errors;
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        return process;
    }

    [Test]
    public async Task LoopbackSshAuthenticationAndForwarding()
    {
        var python = Environment.GetEnvironmentVariable("SSH_TEST_PYTHON");
        var singbox = Environment.GetEnvironmentVariable("SSH_TEST_SINGBOX");
        if (string.IsNullOrEmpty(python) || string.IsNullOrEmpty(singbox))
        {
            Skip.Test("Set SSH_TEST_PYTHON (with paramiko) and SSH_TEST_SINGBOX for isolated integration tests.");
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "v2rayn-ssh-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ssh_server.py");
        using var fixture = Start(python, fixturePath, directory);
        try
        {
            var line = await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(line != null, "fixture failed to start");
            var endpoints = JsonNode.Parse(line!)!;
            using var direct = new HttpClient(new HttpClientHandler { UseProxy = false });
            var httpPort = endpoints["http"]!.GetValue<int>();
            var httpsPort = endpoints["https"]!.GetValue<int>();
            var keys = new List<(int Port, SshHostKey Key)>();
            foreach (var server in endpoints["servers"]!.AsArray())
            {
                var port = server!["port"]!.GetValue<int>();
                var key = await SshProfileService.FetchHostKeyAsync("127.0.0.1", port, CancellationToken.None);
                Check(key.PublicKey == server["key"]!.GetValue<string>(), "public key mismatch");
                keys.Add((port, key));
            }
            Check(await direct.GetStringAsync($"http://127.0.0.1:{httpPort}/auth-count") == "0",
                "host-key probe must stop before any authentication request");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await SshProfileService.FetchHostKeyAsync("127.0.0.1", keys[0].Port, cancelled.Token); }
                catch (OperationCanceledException) { goto CancellationVerified; }
                throw new Exception("Cancelled probe succeeded");
            }
        CancellationVerified:
            var stalled = new TcpListener(IPAddress.Loopback, 0);
            stalled.Start();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                try
                {
                    await SshProfileService.FetchHostKeyAsync("127.0.0.1",
                        ((IPEndPoint)stalled.LocalEndpoint).Port, timeout.Token);
                    throw new Exception("Stalled SSH handshake ignored cancellation");
                }
                catch (OperationCanceledException) { }
            }
            finally { stalled.Stop(); }
            var config = CoreConfigTestFactory.CreateConfig();
            CoreConfigTestFactory.BindAppManagerConfig(config);
            foreach (var (port, key) in keys)
            {
                var node = SshTests.Node();
                node.Port = port;
                node.Password = "fixture-password";
                node.SetProtocolExtra(node.GetProtocolExtra() with
                {
                    SshHostKey = key.PublicKey, SshHostKeyAlgorithm = key.Algorithm, SshTrustedPort = port
                });
                await Request(node, expectSuccess: true);
                if (key.Algorithm != "ssh-ed25519") continue;
                node.Password = "wrong-password";
                await Request(node, expectSuccess: false);
                node.Password = "";
                foreach (var encrypted in new[] { false, true })
                {
                    node.SetProtocolExtra(node.GetProtocolExtra() with
                    {
                        SshPrivateKeyAuth = true,
                        SshPrivateKeyPath = Path.Combine(directory, encrypted ? "client-encrypted" : "client"),
                        SshPrivateKeyPassphrase = encrypted ? "fixture-passphrase" : null
                    });
                    await Request(node, expectSuccess: true);
                }
                node.SetProtocolExtra(node.GetProtocolExtra() with { SshPrivateKeyPassphrase = "wrong-passphrase" });
                await Request(node, expectSuccess: false, expectInvalidKey: true);
                node.SetProtocolExtra(node.GetProtocolExtra() with
                {
                    SshPrivateKeyPassphrase = "fixture-passphrase", SshHostKey = SshTests.TestKey
                });
                await Request(node, expectSuccess: false);
            }

            async Task Request(ProfileItem node, bool expectSuccess, bool expectInvalidKey = false)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);
                if (expectSuccess)
                {
                    var full = new CoreConfigSingboxService(context).GenerateClientConfigContent();
                    Check(full.Success, full.Msg ?? "full configuration generation");
                    var fullPath = Path.Combine(directory, "full-config.json");
                    await File.WriteAllTextAsync(fullPath, full.Data!.ToString());
                    using var fullCheck = Start(singbox, "check", "-c", fullPath);
                    await fullCheck.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Check(fullCheck.ExitCode == 0, "full sing-box configuration check failed: " + Errors[fullCheck.Id]);
                }
                var generated = new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(localPort);
                Check(generated.Success, generated.Msg ?? "generation failed");
                var path = Path.Combine(directory, "config.json");
                await File.WriteAllTextAsync(path, generated.Data!.ToString());
                using (var check = Start(singbox, "check", "-c", path))
                {
                    await check.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    if (expectInvalidKey)
                    {
                        Check(check.ExitCode != 0, "wrong private-key passphrase must fail core validation");
                        return;
                    }
                    Check(check.ExitCode == 0, "sing-box check failed");
                }
                using var core = Start(singbox, "run", "-c", path);
                try
                {
                    using var client = new HttpClient(new HttpClientHandler
                    {
                        Proxy = new WebProxy($"socks5://127.0.0.1:{localPort}"),
                        ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.Host == "127.0.0.1"
                    }) { Timeout = TimeSpan.FromSeconds(3) };
                    var ready = false;
                    for (var i = 0; i < 40 && !core.HasExited; i++)
                    {
                        try
                        {
                            using var socket = new TcpClient();
                            await socket.ConnectAsync(IPAddress.Loopback, localPort);
                            ready = true;
                            break;
                        }
                        catch (SocketException) { await Task.Delay(50); }
                    }
                    Check(ready, "sing-box did not start");
                    try
                    {
                        var body = await client.GetStringAsync($"http://127.0.0.1:{httpPort}/");
                        Check(expectSuccess, "invalid credentials/key were accepted");
                        Check(body == "ssh-proxy-ok", "HTTP forwarding");
                        body = await client.GetStringAsync($"https://127.0.0.1:{httpsPort}/");
                        Check(body == "ssh-proxy-ok", "HTTPS forwarding");
                    }
                    catch (Exception e) when (!expectSuccess && e is HttpRequestException or TaskCanceledException) { }
                }
                finally
                {
                    if (!core.HasExited) core.Kill(true);
                    await core.WaitForExitAsync();
                }
            }
        }
        finally
        {
            if (!fixture.HasExited) fixture.Kill(true);
            await fixture.WaitForExitAsync();
            Directory.Delete(directory, true);
        }
    }
}
