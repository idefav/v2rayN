using System.Net.Http;

namespace ServiceLib.Services;

public enum ServerProbeState { Untested, Running, Passed, Responded, Failed, Timeout, Cancelled, Unsupported }
public sealed record ServerProbeStage(ServerProbeState State, long Milliseconds = 0, int? HttpStatus = null, string? Error = null)
{
    public string Display => State switch
    {
        ServerProbeState.Passed => $"{Milliseconds} ms",
        ServerProbeState.Responded => $"HTTP {HttpStatus} · {Milliseconds} ms",
        ServerProbeState.Running => ProxyCheckText.T("检测中", "Testing"),
        ServerProbeState.Failed => ProxyCheckText.T("失败", "Failed") + (Error == null ? "" : $" ({Error})"),
        ServerProbeState.Timeout => ProxyCheckText.T("超时", "Timeout"),
        ServerProbeState.Cancelled => ProxyCheckText.T("已取消", "Cancelled"),
        ServerProbeState.Unsupported => ProxyCheckText.T("不适用", "Not applicable"),
        _ => ProxyCheckText.T("未测试", "Untested")
    };
}
public sealed record ServerProbeResult(string NodeId, string Identity, Guid RunId, DateTimeOffset CheckedAt,
    ServerProbeStage Tcp, ServerProbeStage Proxy);

public interface IServerProbeService
{
    Task<ServerProbeResult> TestAsync(ProfileItem snapshot, Config config, Guid runId, CancellationToken token);
}

/// <summary>Owns only temporary test processes. Never calls the connection lifecycle or system proxy.</summary>
public sealed class ServerProbeService : IServerProbeService
{
    public static string Identity(ProfileItem node) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(node))));

    public async Task<ServerProbeResult> TestAsync(ProfileItem snapshot, Config config, Guid runId, CancellationToken token)
    {
        var node = JsonUtils.DeepCopy(snapshot);
        var settings = JsonUtils.DeepCopy(config);
        var tcp = new ServerProbeStage(ServerProbeState.Unsupported);
        var proxy = tcp;
        if (node.ConfigType is not (EConfigType.Custom or EConfigType.PolicyGroup or EConfigType.ProxyChain
            or EConfigType.Outbound or EConfigType.Hysteria2 or EConfigType.TUIC or EConfigType.WireGuard or EConfigType.MASQUE)
            && node.GetNetwork() != "kcp")
            tcp = await MeasureAsync(async ct =>
            {
                using var socket = new TcpClient();
                await socket.ConnectAsync(node.Address, node.Port, ct);
                return null;
            }, token);
        if (node.ConfigType != EConfigType.Custom && !token.IsCancellationRequested)
        {
            try
            {
                proxy = await RunProxyAsync(node, settings, async (port, ct) => await MeasureAsync(async requestToken =>
                {
                    using var handler = new SocketsHttpHandler
                    {
                        Proxy = new WebProxy($"socks5://127.0.0.1:{port}"), UseProxy = true,
                        AllowAutoRedirect = false, UseCookies = false
                    };
                    using var client = new HttpClient(handler);
                    using var response = await client.GetAsync(settings.SpeedTestItem.SpeedPingTestUrl,
                        HttpCompletionOption.ResponseHeadersRead, requestToken);
                    return (int)response.StatusCode;
                }, ct), token);
            }
            catch (OperationCanceledException) { proxy = new(token.IsCancellationRequested ? ServerProbeState.Cancelled : ServerProbeState.Timeout); }
            catch (SshSetupException error) { proxy = new(ServerProbeState.Failed, Error: error.Code); }
            catch { proxy = new(ServerProbeState.Failed, Error: "coreStart"); }
        }
        if (token.IsCancellationRequested) proxy = new(ServerProbeState.Cancelled);
        return new(node.IndexId, Identity(node), runId, DateTimeOffset.UtcNow, tcp, proxy);
    }

    internal static async Task<ServerProbeStage> MeasureAsync(Func<CancellationToken, Task<int?>> action, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Global.LocalFetch);
        var watch = Stopwatch.StartNew();
        try
        {
            var status = await action(deadline.Token);
            return new(status is null or >= 200 and < 300 ? ServerProbeState.Passed : ServerProbeState.Responded,
                watch.ElapsedMilliseconds, status);
        }
        catch (OperationCanceledException) { return new(token.IsCancellationRequested ? ServerProbeState.Cancelled : ServerProbeState.Timeout); }
        catch (HttpRequestException e) { return new(ServerProbeState.Failed, Error: e.HttpRequestError.ToString()); }
        catch (SocketException) { return new(ServerProbeState.Failed, Error: "connection"); }
    }

    internal static async Task<T> RunProxyAsync<T>(ProfileItem node, Config config,
        Func<int, CancellationToken, Task<T>> action, CancellationToken token)
    {
        config = JsonUtils.DeepCopy(config);
        config.TunModeItem.EnableTun = false;
        config.CoreBasicItem.LogEnabled = false; // Temporary cores must not append to the running client's logs.
        var core = AppManager.GetCoreType(config, node, node.ConfigType);
        if (core is not (ECoreType.sing_box or ECoreType.Xray)) throw new SshSetupException("coreUnsupported");
        var context = new CoreConfigContext { Node = node, RunCoreType = core, AppConfig = config, SimpleDnsItem = config.SimpleDNSItem,
            IsWindows = Utils.IsWindows(), IsMacOS = Utils.IsMacOS(), IsLinux = Utils.IsLinux() };
        var (resolved, validation) = await CoreConfigContextBuilder.ResolveNodeAsync(context, node, true);
        if (!validation.Success) throw new SshSetupException("configuration");
        context = context with { Node = resolved };
        var executable = CoreInfoManager.Instance.GetCoreExecFile(CoreInfoManager.Instance.GetCoreInfo(core), out _);
        if (string.IsNullOrEmpty(executable)) throw new SshSetupException("coreMissing");
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        var result = core == ECoreType.sing_box
            ? new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(port)
            : new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(port);
        if (!result.Success) throw new SshSetupException("configuration");
        var directory = Path.Combine(Path.GetTempPath(), "v2rayn-node-check-" + Guid.NewGuid().ToString("N"));
        new SshKeyStore(directory).EnsureDirectory();
        Process? process = null;
        Task output = Task.CompletedTask, errors = Task.CompletedTask;
        var stopped = true;
        try
        {
            var path = Path.Combine(directory, "config.json");
            using (var file = SshKeyStore.CreatePrivateFile(path))
                await file.WriteAsync(Encoding.UTF8.GetBytes(result.Data.ToString()!), token);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = directory, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            token.ThrowIfCancellationRequested();
            reservation.Stop();
            process = Process.Start(start) ?? throw new SshSetupException("coreStart");
            stopped = false;
            output = DrainAsync(process.StandardOutput); errors = DrainAsync(process.StandardError);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(8));
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new SshSetupException("coreStart");
                try { using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port, startup.Token); break; }
                catch (SocketException) { await Task.Delay(50, startup.Token); }
            }
            await Task.Delay(100, token);
            if (process.HasExited) throw new SshSetupException("coreStart");
            return await action(port, token);
        }
        finally
        {
            if (process != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync(cleanup.Token);
                    await Task.WhenAll(output, errors).WaitAsync(cleanup.Token);
                    stopped = true;
                }
                finally { process.Dispose(); }
            }
            if (stopped) Directory.Delete(directory, true);
        }
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer) != 0) { }
    }
}
