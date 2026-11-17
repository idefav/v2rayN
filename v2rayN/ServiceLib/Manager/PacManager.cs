namespace ServiceLib.Manager;

public class PacManager
{
    private const string Tag = "PacManager";
    private CancellationTokenSource? _cts;
    private int _pacPort;
    private int _httpPort;
    private string? _customPath;
    private TcpListener? _tcpListener;
    private byte[] _writeContent = [];

    internal bool Matches(int httpPort, int pacPort, string? customPath)
        => _tcpListener != null && _cts?.IsCancellationRequested == false && _httpPort == httpPort
            && _pacPort == pacPort && _customPath == customPath;

    public async Task StartAsync(int httpPort, int pacPort, string? customPath = null, CancellationToken token = default)
    {
        var content = await InitText(httpPort, customPath, token);
        token.ThrowIfCancellationRequested();
        _writeContent = content;
        _httpPort = httpPort;
        _customPath = customPath;

        if (_tcpListener is not null && _pacPort == pacPort)
        {
            return;
        }

        Stop();
        var cts = new CancellationTokenSource();
        var listener = TcpListener.Create(pacPort);
        listener.Start();

        _cts = cts;
        _pacPort = pacPort;
        _httpPort = httpPort;
        _customPath = customPath;
        _tcpListener = listener;
        _ = ListenLoopAsync(listener, cts.Token);
    }

    private async Task<byte[]> InitText(int httpPort, string? customSystemProxyPacPath, CancellationToken token)
    {
        var fileName = customSystemProxyPacPath.IsNotEmpty() && File.Exists(customSystemProxyPacPath)
            ? customSystemProxyPacPath
            : Path.Combine(Utils.GetConfigPath(), "pac.txt");

        // TODO: temporarily notify which script is being used
        NoticeManager.Instance.SendMessage(fileName);

        if (!File.Exists(fileName))
        {
            var pac = EmbedUtils.GetEmbedText(Global.PacFileName);
            await File.AppendAllTextAsync(fileName, pac, token);
        }

        var pacText = await File.ReadAllTextAsync(fileName, token);
        pacText = pacText.Replace("__PROXY__", $"PROXY 127.0.0.1:{httpPort};DIRECT;");

        var sb = new StringBuilder();
        sb.AppendLine("HTTP/1.0 200 OK");
        sb.AppendLine("Content-type:application/x-ns-proxy-autoconfig");
        sb.AppendLine("Connection:close");
        sb.AppendLine("Content-Length:" + Encoding.UTF8.GetByteCount(pacText));
        sb.AppendLine();
        sb.Append(pacText);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private async Task ListenLoopAsync(TcpListener listener, CancellationToken token)
    {
        var buffer = new byte[1024];
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                await using var stream = client.GetStream();
                _ = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                await stream.WriteAsync(_writeContent, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _tcpListener?.Stop();
        _cts?.Dispose();
        _cts = null;
        _pacPort = 0;
        _httpPort = 0;
        _customPath = null;
        _tcpListener = null;
    }
}
