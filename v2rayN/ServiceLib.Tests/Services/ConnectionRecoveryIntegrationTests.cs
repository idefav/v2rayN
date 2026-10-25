using System.Net.Http;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class ConnectionRecoveryIntegrationTests
{
    [Test]
    [Arguments("exited")]
    [Arguments("stalled")]
    [Arguments("partial")]
    [Arguments("external-failure")]
    public async Task OnlyLocalFaultRestartsCurrentCoreOnSameProxyPort(string scenario)
    {
        var localFault = scenario == "exited";
        var python = Environment.GetEnvironmentVariable("SSH_TEST_PYTHON");
        var singbox = Environment.GetEnvironmentVariable("SSH_TEST_SINGBOX");
        if (string.IsNullOrEmpty(python) || string.IsNullOrEmpty(singbox))
        {
            Skip.Test("Set SSH_TEST_PYTHON and SSH_TEST_SINGBOX for isolated SSH recovery test."); return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "ssh-recovery-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        Process? core = null;
        using var fixture = Start(python, Path.Combine(AppContext.BaseDirectory, "Fixtures", "ssh_server.py"), directory);
        try
        {
            var endpoints = JsonNode.Parse((await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!)!;
            var ssh = endpoints["servers"]![0]!;
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var config = new
            {
                log = new { level = "error" },
                inbounds = new[] { new { type = "mixed", listen = "127.0.0.1", listen_port = proxyPort } },
                outbounds = new[] { new { type = "ssh", tag = "proxy", server = "127.0.0.1", server_port = ssh["port"]!.GetValue<int>(),
                    user = "test", password = "fixture-password", host_key = new[] { ssh["key"]!.GetValue<string>() } } },
                route = new { final = "proxy" },
            };
            var path = Path.Combine(directory, "core.json"); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config));
            core = Start(singbox, "run", "-c", path);
            var originalPid = core.Id;
            if (!await RecoveryProbe.WaitForListenerAsync(proxyPort, CancellationToken.None)) throw new Exception("core did not listen");
            var snapshot = new RecoveryContext("fixture", proxyPort, $"https://127.0.0.1:{endpoints["https"]!.GetValue<int>()}/check", 1);
            var fallback = snapshot.TestUrl;
            Task<RecoveryProbeResult> Probe(RecoveryContext selected, CancellationToken token) => RecoveryProbe.ProbeAsync(selected, token,
                fallback: fallback, handlerFactory: () => new SocketsHttpHandler
                {
                    SslOptions = new() { RemoteCertificateValidationCallback = (_, _, _, _) => true },
                });
            if (!(await Probe(snapshot, CancellationToken.None)).Success) throw new Exception("initial fixture SSH/TLS request failed");
            using var control = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var baseUrl = $"http://127.0.0.1:{endpoints["http"]!.GetValue<int>()}";
            using var downloadClient = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{proxyPort}"), UseProxy = true });
            using var download = scenario is "partial" or "external-failure"
                ? await downloadClient.GetAsync(baseUrl + "/stream", HttpCompletionOption.ResponseHeadersRead) : null;
            var transfer = download?.Content.ReadAsByteArrayAsync();
            if (transfer?.IsCompleted == true) throw new Exception("stream fixture did not remain active");
            if (localFault) { core.Kill(true); await core.WaitForExitAsync(); }
            else if (scenario == "stalled") await control.GetStringAsync(baseUrl + "/stall-ssh");
            else
            {
                snapshot = snapshot with { TestUrl = "http://127.0.0.2:80/unreachable" };
                if (scenario == "external-failure") fallback = "http://127.0.0.3:80/unreachable";
            }
            var restarts = 0;
            var service = new ConnectionRecoveryService(() => snapshot, _ => Task.FromResult<string?>("loopback fixture"),
                (_, _) => Task.FromResult(new ProxyCheckResult(true, "fixture")), (_, _) => Task.FromResult(true), Probe,
                async (request, token) =>
                {
                    if (!await request.Confirm(token)) return false;
                    restarts++;
                    if (!core.HasExited) { core.Kill(true); await core.WaitForExitAsync(); }
                    core.Dispose();
                    await control.GetStringAsync(baseUrl + "/resume-ssh", token);
                    core = Start(singbox, "run", "-c", path);
                    return await RecoveryProbe.WaitForListenerAsync(proxyPort, token);
                }, _ => { }, options: new() { StableDelay = TimeSpan.Zero, RetryDelay = TimeSpan.Zero },
                localHealth: async (_, token) => new(core.HasExited ? LocalHealth.Failed : LocalHealth.Healthy,
                    LocalHealth.Healthy, await RecoveryProbe.CheckSocksAsync(proxyPort, token)));
            var elapsed = Stopwatch.StartNew();
            await service.CheckAsync("fixture wake", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            if (localFault)
            {
                if (restarts != 1 || core.Id == originalPid || service.State != RecoveryState.Idle || elapsed.Elapsed >= TimeSpan.FromSeconds(30))
                    throw new Exception("exited core did not recover within deadline");
            }
            else
            {
                var expected = scenario == "partial" ? RecoveryState.Idle : RecoveryState.Degraded;
                if (restarts != 0 || core.Id != originalPid || service.State != expected)
                    throw new Exception("stalled SSH restarted a healthy local SOCKS service");
                if (scenario == "stalled") await control.GetStringAsync(baseUrl + "/resume-ssh");
                if (transfer != null)
                {
                    var bytes = await transfer.WaitAsync(TimeSpan.FromSeconds(10));
                    var expectedBytes = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i % 256)).ToArray();
                    if (!SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(expectedBytes)))
                        throw new Exception("active transfer was interrupted or corrupted");
                }
            }
            snapshot = snapshot with { TestUrl = $"https://127.0.0.1:{endpoints["https"]!.GetValue<int>()}/check" };
            fallback = snapshot.TestUrl;
            if (!(await Probe(snapshot, CancellationToken.None)).Success) throw new Exception("restored SSH/TLS path failed");
        }
        finally
        {
            if (core != null) { if (!core.HasExited) { core.Kill(true); await core.WaitForExitAsync(); } core.Dispose(); }
            if (!fixture.HasExited) { fixture.Kill(true); await fixture.WaitForExitAsync(); }
            Directory.Delete(directory, true);
        }
    }

    private static Process Start(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        // Drain errors; they may contain temporary fixture paths, so do not persist them.
        process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
        return process;
    }
}
