using System.Security.Authentication;
using Downloader;

namespace ServiceLib.Helper;

public class DownloaderHelper
{
    private static readonly Lazy<DownloaderHelper> _instance = new(() => new());
    public static DownloaderHelper Instance => _instance.Value;

    public async Task<string?> DownloadStringAsync(IWebProxy? webProxy, string url, string? userAgent,
        IReadOnlyDictionary<string, string>? requestHeaders = null, string? acceptHeader = null, CancellationToken cancellationToken = default)
    {
        if (url.IsNullOrEmpty())
        {
            return null;
        }

        Uri uri = new(url);
        //Authorization Header
        var headers = new WebHeaderCollection();
        if (uri.UserInfo.IsNotEmpty())
        {
            headers.Add(HttpRequestHeader.Authorization, "Basic " + Utils.Base64Encode(uri.UserInfo));
        }

        var requestConfiguration = new RequestConfiguration()
        {
            Headers = headers,
            Accept = acceptHeader,
            UserAgent = userAgent,
            ConnectTimeout = GetConnectTimeoutMs(webProxy != null),
            Proxy = webProxy
        };
        var downloadOpt = new DownloadConfiguration()
        {
            MaxTryAgainOnFailure = 2,
            RequestConfiguration = requestConfiguration,
            CustomHttpMessageHandlerFactory = () => HttpRequestHeadersHelper.CreateHandler(GetSocketsHttpHandler(requestConfiguration), requestHeaders),
        };

        await using var downloader = new Downloader.DownloadService(downloadOpt);
        downloader.DownloadFileCompleted += (sender, value) =>
        {
            if (value.Error != null)
            {
                throw value.Error;
            }
        };

        await using var stream = await downloader.DownloadFileTaskAsync(address: url, cancellationToken);
        using StreamReader reader = new(stream);

        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task DownloadDataAsync4Speed(IWebProxy webProxy, string url, Action<string> onProgress, CancellationToken cancellationToken = default)
    {
        if (url.IsNullOrEmpty())
        {
            throw new ArgumentNullException(nameof(url));
        }

        var requestConfiguration = new RequestConfiguration()
        {
            ConnectTimeout = GetConnectTimeoutMs(true),
            Proxy = webProxy
        };
        var downloadOpt = new DownloadConfiguration()
        {
            MaxTryAgainOnFailure = 2,
            RequestConfiguration = requestConfiguration,
            CustomHttpMessageHandlerFactory = () => GetSocketsHttpHandler(requestConfiguration),
        };

        var lastUpdateTime = DateTime.Now;
        var hasValue = false;
        double maxSpeed = 0;
        await using var downloader = new Downloader.DownloadService(downloadOpt);

        downloader.DownloadProgressChanged += (sender, value) =>
        {
            if (!(value.BytesPerSecondSpeed > 0))
            {
                return;
            }
            hasValue = true;
            if (value.BytesPerSecondSpeed > maxSpeed)
            {
                maxSpeed = value.BytesPerSecondSpeed;
            }

            var ts = DateTime.Now - lastUpdateTime;
            if (ts.TotalMilliseconds >= 1000)
            {
                lastUpdateTime = DateTime.Now;
                var speed = (maxSpeed / 1000 / 1000).ToString("#0.0");
                onProgress.Invoke(speed);
            }
        };
        downloader.DownloadFileCompleted += (sender, value) =>
        {
            if (hasValue && maxSpeed > 0)
            {
                var finalSpeed = (maxSpeed / 1000 / 1000).ToString("#0.0");
                onProgress.Invoke(finalSpeed);
            }
            else if (value.Error != null)
            {
                onProgress.Invoke(value.Error?.Message);
            }
            else
            {
                onProgress.Invoke("0");
            }
        };
        //progress.Invoke("......");
        await using var stream = await downloader.DownloadFileTaskAsync(address: url, cancellationToken);
    }

    public async Task DownloadFileAsync(IWebProxy? webProxy, FileDownloadRequest request, Action<FileDownloadState> onProgress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FilePath);
        if (!Uri.TryCreate(request.FileUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            throw new ArgumentException("Invalid download URL");
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await DownloadFileAttemptAsync(webProxy, request, onProgress, deadline.Token);
                return;
            }
            catch (Exception ex)
            {
                try { File.Delete(request.FilePath); }
                catch (Exception cleanup) { Logging.SaveLog("Download cleanup", cleanup); }
                cancellationToken.ThrowIfCancellationRequested();
                var source = new Uri(request.FileUrl).GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
                var reason = ex is OperationCanceledException ? "下载超时 / Download timed out" : ex.Message;
                var message = $"下载失败 / Download failed ({attempt}/{attempts}, {(webProxy == null ? "direct" : "proxy")}): {reason}. {source}";
                Logging.SaveLog(message);
                if (attempt == attempts || !IsTransientDownloadError(ex)) throw new IOException(message, ex);
                onProgress(new FileDownloadState { Request = request, RetryMessage =
                    $"下载重试 / Retrying download ({attempt + 1}/{attempts}): {reason}" });
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    private static bool IsTransientDownloadError(Exception error) => error switch
    {
        HttpRequestException http when http.InnerException is AuthenticationException => false,
        HttpRequestException http => http.StatusCode == null || http.StatusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500,
        OperationCanceledException => true,
        IOException => true,
        _ => false
    };

    private async Task DownloadFileAttemptAsync(IWebProxy? webProxy, FileDownloadRequest request, Action<FileDownloadState> onProgress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FilePath.IsNullOrEmpty())
        {
            throw new ArgumentNullException(nameof(request.FilePath));
        }
        if (File.Exists(request.FilePath))
        {
            File.Delete(request.FilePath);
        }

        var state = new FileDownloadState
        {
            Request = request,
        };

        var requestConfiguration = new RequestConfiguration
        {
            ConnectTimeout = GetConnectTimeoutMs(webProxy != null),
            Proxy = webProxy,
        };
        using var client = new HttpClient(GetSocketsHttpHandler(requestConfiguration)) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("v2rayN");
        using var response = await client.GetAsync(request.FileUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        state = state with { TotalBytes = response.Content.Headers.ContentLength ?? 0 };
        onProgress(state);
        var elapsed = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(request.FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        {
            var buffer = new byte[65536];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                state = state with { DownloadedBytes = state.DownloadedBytes + count };
                if (elapsed.Elapsed - lastReport < TimeSpan.FromMilliseconds(250)) continue;
                lastReport = elapsed.Elapsed;
                onProgress(state with { SpeedBytesPerSecond = state.DownloadedBytes / elapsed.Elapsed.TotalSeconds });
            }
            if (state.TotalBytes > 0 && state.DownloadedBytes != state.TotalBytes)
                throw new IOException("下载文件长度不符 / Download size mismatch");
            await output.FlushAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        onProgress(state with { Completed = true });
    }

    public async Task DownloadSmallFilesAsync(IWebProxy? webProxy, List<FileDownloadRequest> requests, Action<ReadOnlyMemory<FileDownloadState>> onProgress, CancellationToken cancellationToken = default)
    {
        if (requests is not { Count: > 0 })
        {
            throw new ArgumentNullException(nameof(requests));
        }

        var states = new FileDownloadState[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            states[i] = new FileDownloadState
            {
                Request = requests[i],
            };
        }
        var readOnlyStates = new ReadOnlyMemory<FileDownloadState>(states);

        var requestConfiguration = new RequestConfiguration()
        {
            ConnectTimeout = GetConnectTimeoutMs(webProxy != null),
            Proxy = webProxy,

            KeepAlive = true,
        };
        using var socketsHttpHandler = GetSocketsHttpHandler(requestConfiguration);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, requests.Count), parallelOptions, async (index, parallelCancellationToken) =>
        {
            var request = requests[index];
            var downloadOpt = new DownloadConfiguration()
            {
                RequestConfiguration = requestConfiguration,
                // ReSharper disable once AccessToDisposedClosure
                CustomHttpMessageHandlerFactory = () => socketsHttpHandler,
            };
            await using var downloader = new Downloader.DownloadService(downloadOpt);
            downloader.DownloadStarted += (sender, value) =>
            {
                states[index] = states[index] with
                {
                    DownloadedBytes = 0,
                    TotalBytes = value.TotalBytesToReceive,
                    SpeedBytesPerSecond = 0,
                    Completed = false,
                };
                onProgress.Invoke(readOnlyStates);
            };
            downloader.DownloadProgressChanged += (sender, value) =>
            {
                states[index] = states[index] with
                {
                    DownloadedBytes = value.ReceivedBytesSize,
                    TotalBytes = value.TotalBytesToReceive,
                    SpeedBytesPerSecond = value.BytesPerSecondSpeed,
                    Completed = false,
                };
                onProgress.Invoke(readOnlyStates);
            };
            downloader.DownloadFileCompleted += (sender, value) =>
            {
                var newState = states[index] with { Completed = true };
                if (value.Error != null)
                {
                    newState = newState with { Error = value.Error };
                }
                states[index] = newState;
                onProgress.Invoke(readOnlyStates);
            };
            await downloader.DownloadFileTaskAsync(request.FileUrl, request.FilePath, parallelCancellationToken);
        });
    }

    // https://github.com/bezzad/Downloader/blob/a75a6e431acd6cbba6293f7afdcf676544a09174/src/Downloader/SocketClient.cs#L45
    // There is a risk of MITM attacks
    // https://github.com/bezzad/Downloader/blob/a75a6e431acd6cbba6293f7afdcf676544a09174/src/Downloader/Extensions/ExceptionHelper.cs#L111
    private static SocketsHttpHandler GetSocketsHttpHandler(RequestConfiguration config)
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = config.AllowAutoRedirect,
            MaxAutomaticRedirections = config.MaximumAutomaticRedirections,
            AutomaticDecompression = config.AutomaticDecompression,
            PreAuthenticate = config.PreAuthenticate,
            UseCookies = config.CookieContainer != null,
            UseProxy = config.Proxy != null,
            MaxConnectionsPerServer = 1000,
            PooledConnectionIdleTimeout = config.KeepAliveTimeout,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromMilliseconds(config.ConnectTimeout),
        };

        // Set up the SslClientAuthenticationOptions for custom certificate validation
        if (config.ClientCertificates?.Count > 0)
        {
            handler.SslOptions.ClientCertificates = config.ClientCertificates;
        }

        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;
        //handler.SslOptions.RemoteCertificateValidationCallback = ExceptionHelper.CertificateValidationCallBack;

        var certificateChainPolicy = CertPemManager.Instance.BuildCertificateChainPolicy();
        if (certificateChainPolicy != null)
        {
            handler.SslOptions.CertificateChainPolicy = certificateChainPolicy;
            handler.SslOptions.RemoteCertificateValidationCallback = null;
        }

        // Configure keep-alive
        if (config.KeepAlive)
        {
            handler.KeepAlivePingTimeout = config.KeepAliveTimeout;
            handler.KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests;
        }

        // Configure credentials
        if (config.Credentials != null)
        {
            handler.Credentials = config.Credentials;
            handler.PreAuthenticate = config.PreAuthenticate;
        }

        // Configure cookies
        if (handler.UseCookies && config.CookieContainer != null)
        {
            handler.CookieContainer = config.CookieContainer;
        }

        // Configure proxy
        if (handler.UseProxy && config.Proxy != null)
        {
            handler.Proxy = config.Proxy;
        }

        // Add expect header
        if (!string.IsNullOrWhiteSpace(config.Expect))
        {
            handler.Expect100ContinueTimeout = TimeSpan.FromSeconds(1);
        }

        return handler;
    }

    private int GetConnectTimeoutMs(bool isProxy)
    {
        return (int)(isProxy ? Global.ProxyDownloadConnect : Global.DirectDownloadConnect).TotalMilliseconds;
    }
}
