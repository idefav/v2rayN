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
        var result = await Cli.Wrap(file).WithArguments(arguments).WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(timeout.Token).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput : string.Empty;
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

internal static class RecoveryProbe
{
    public static async Task<bool> ProbeAsync(RecoveryContext context, CancellationToken token,
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
        var requests = new[] { context.TestUrl, fallback }.Distinct().Select(Request).ToList();
        try
        {
            while (requests.Count > 0)
            {
                var completed = await Task.WhenAny(requests).ConfigureAwait(false);
                requests.Remove(completed);
                if (await completed.ConfigureAwait(false)) return true;
            }
            token.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            timeout.Cancel();
            await Task.WhenAll(requests).ConfigureAwait(false);
        }

        async Task<bool> Request(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                log?.Invoke("probe HTTP=" + (int)response.StatusCode);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                log?.Invoke("probe failed=" + ex.GetType().Name);
                return false;
            }
        }
    }

    public static async Task<bool> WaitForListenerAsync(int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    using var socket = new TcpClient();
                    await socket.ConnectAsync(Global.Loopback, port, timeout.Token).ConfigureAwait(false);
                    var stream = socket.GetStream();
                    await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token).ConfigureAwait(false);
                    var reply = new byte[2];
                    await stream.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
                    if (reply[0] == 5 && reply[1] == 0) return true;
                }
                catch (Exception ex) when (ex is SocketException or IOException) { }
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }
}
