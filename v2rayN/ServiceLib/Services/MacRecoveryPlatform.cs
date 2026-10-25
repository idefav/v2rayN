using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Services;

internal static class MacRecoveryPlatform
{
    [SupportedOSPlatform("macos")]
    internal static async Task<string> CommandAsync(string file, IEnumerable<string> arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var result = await Cli.Wrap(file).WithArguments(arguments).WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeout.Token).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StandardOutput : string.Empty;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Keep command timeouts distinct from cancellation of the recovery job.
            // Neither the command arguments nor captured output belong in diagnostics.
            throw new TimeoutException("Recovery platform command timed out.");
        }
    }

    [SupportedOSPlatform("macos")]
    public static async Task<string?> NetworkAsync(CancellationToken token)
    {
        var route = await CommandAsync("/sbin/route", ["-n", "get", "default"], token).ConfigureAwait(false);
        if (route.Length == 0)
            route = await CommandAsync("/sbin/route", ["-n", "get", "-inet6", "default"], token).ConfigureAwait(false);
        var name = Regex.Match(route, @"(?m)^\s*interface:\s*(\S+)").Groups[1].Value;
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name && n.OperationalStatus == OperationalStatus.Up);
        if (nic == null) return null;
        var addresses = nic.GetIPProperties().UnicastAddresses.Select(a => a.Address)
            .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(a => a.ToString()).Order().ToArray();
        return addresses.Length == 0 ? null : name + ":" + string.Join(",", addresses);
    }

    [SupportedOSPlatform("macos")]
    public static async Task<ProxyCheckResult> ReadProxyAsync(int port, CancellationToken token)
        => ParseProxy(await CommandAsync("/usr/sbin/scutil", ["--proxy"], token).ConfigureAwait(false), port);

    internal static ProxyCheckResult ParseProxy(string text, int port)
    {
        string Value(string key) => Regex.Match(text, @"(?m)^\s*" + key + @"\s*:\s*(\S+)").Groups[1].Value;
        var values = new[] { "HTTP", "HTTPS", "SOCKS" }.Select(protocol =>
            (Protocol: protocol, Enabled: Value(protocol + "Enable"), Host: Value(protocol + "Proxy"), Port: Value(protocol + "Port"))).ToArray();
        var matches = port == 0
            ? text.Contains("<dictionary>", StringComparison.Ordinal) && values.All(p => p.Enabled != "1")
            : values.All(p => p.Enabled == "1" && p.Host == Global.Loopback && p.Port == port.ToString());
        // Include only known fields, never raw command output or credentials.
        return new(matches, string.Join("; ", values.Select(p => $"{p.Protocol} enabled={p.Enabled == "1"} loopback={p.Host == Global.Loopback} port={SafePort(p.Port)}")));
    }

    private static int SafePort(string value) => int.TryParse(value, out var port) ? port : 0;
}

internal enum ProbeOutcome { Success, HttpRejected, Timeout, Dns, Connection, Tls, Transport, InvalidTarget, Cancelled }
internal sealed record ProbeTargetResult(string Host, ProbeOutcome Outcome, int? StatusCode, long ElapsedMs);
internal sealed record RecoveryProbeResult(IReadOnlyList<ProbeTargetResult> Targets)
{
    public bool Success => Targets.Any(t => t.Outcome == ProbeOutcome.Success);
}

internal static class RecoveryProbe
{
    public static async Task<RecoveryProbeResult> ProbeAsync(RecoveryContext context, CancellationToken token,
        Action<string>? log = null, string fallback = "https://www.gstatic.com/generate_204",
        Func<SocketsHttpHandler>? handlerFactory = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var handler = handlerFactory?.Invoke() ?? new SocketsHttpHandler();
        handler.Proxy = new WebProxy($"socks5://{Global.Loopback}:{context.Port}");
        handler.UseProxy = true;
        handler.AllowAutoRedirect = false;
        handler.ConnectTimeout = TimeSpan.FromSeconds(5);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var winnerFound = false;
        var results = new List<ProbeTargetResult>();
        var requests = new[] { context.TestUrl, fallback }.Distinct().Select(Request).ToList();
        try
        {
            while (requests.Count > 0)
            {
                var completed = await Task.WhenAny(requests).ConfigureAwait(false);
                requests.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                results.Add(result);
                if (result.Outcome == ProbeOutcome.Success)
                {
                    winnerFound = true;
                    timeout.Cancel();
                    results.AddRange(await Task.WhenAll(requests).ConfigureAwait(false));
                    requests.Clear();
                    break;
                }
            }
            token.ThrowIfCancellationRequested();
            foreach (var result in results)
                log?.Invoke($"probe host={result.Host} outcome={result.Outcome} HTTP={result.StatusCode} elapsedMs={result.ElapsedMs}");
            return new(results);
        }
        finally
        {
            timeout.Cancel();
            await Task.WhenAll(requests).ConfigureAwait(false);
        }

        async Task<ProbeTargetResult> Request(string url)
        {
            var elapsed = Stopwatch.StartNew();
            var host = "<invalid>";
            ProbeTargetResult Result(ProbeOutcome outcome, int? status = null) => new(host, outcome, status, elapsed.ElapsedMilliseconds);
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return Result(ProbeOutcome.InvalidTarget);
                host = uri.IdnHost;
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                return Result(response.IsSuccessStatusCode ? ProbeOutcome.Success : ProbeOutcome.HttpRejected, (int)response.StatusCode);
            }
            catch (UriFormatException)
            {
                // TryCreate can accept a URI whose internationalized hostname cannot
                // be converted to IDN. Keep this target from poisoning a good fallback.
                host = "<invalid>";
                return Result(ProbeOutcome.InvalidTarget);
            }
            catch (OperationCanceledException)
            {
                // A successful sibling cancels the remaining requests. Caller cancellation
                // is rethrown by the outer method and never logged as a target failure.
                return Result(winnerFound ? ProbeOutcome.Cancelled : ProbeOutcome.Timeout);
            }
            catch (HttpRequestException ex)
            {
                return Result(Classify(ex));
            }
            catch (IOException) { return Result(ProbeOutcome.Transport); }
        }
    }

    internal static ProbeOutcome Classify(HttpRequestException error) => error.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => ProbeOutcome.Dns,
        HttpRequestError.ConnectionError => ProbeOutcome.Connection,
        HttpRequestError.SecureConnectionError => ProbeOutcome.Tls,
        _ => ProbeOutcome.Transport
    };

    public static Task<LocalRecoveryResult> LocalAsync(RecoveryContext context, CancellationToken token)
        => LocalAsync(context, token, CoreManager.Instance.GetRecoveryProcessHealth);

    internal static async Task<LocalRecoveryResult> LocalAsync(RecoveryContext context, CancellationToken token,
        Func<(LocalHealth Main, LocalHealth Pre)> processHealth)
    {
        var socks = await CheckSocksAsync(context.Port, token).ConfigureAwait(false);
        // A required process may exit while the handshake is pending.
        var processes = processHealth();
        return new(processes.Main, processes.Pre, socks);
    }

    internal static async Task<LocalHealth> CheckSocksAsync(int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(Global.Loopback, port, timeout.Token).ConfigureAwait(false);
            var stream = socket.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token).ConfigureAwait(false);
            var reply = new byte[2];
            await stream.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
            return reply[0] == 5 && reply[1] == 0 ? LocalHealth.Healthy : LocalHealth.Failed;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return LocalHealth.Failed; }
        catch (SocketException ex)
        {
            token.ThrowIfCancellationRequested();
            return ClassifySocksFailure(ex.SocketErrorCode);
        }
        catch (IOException ex)
        {
            token.ThrowIfCancellationRequested();
            return ex.InnerException is SocketException socketError
                ? ClassifySocksFailure(socketError.SocketErrorCode) : LocalHealth.Failed;
        }
    }

    internal static LocalHealth ClassifySocksFailure(SocketError error) => error switch
    {
        SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.ConnectionAborted
            or SocketError.TimedOut or SocketError.Shutdown => LocalHealth.Failed,
        // Resource exhaustion, permission failures and other local OS errors do not
        // establish that restarting the proxy would help.
        _ => LocalHealth.Unknown
    };

    public static async Task<bool> WaitForListenerAsync(int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (await CheckSocksAsync(port, timeout.Token).ConfigureAwait(false) == LocalHealth.Healthy) return true;
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }
}
