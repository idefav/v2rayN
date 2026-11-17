namespace ServiceLib.Handler.SysProxy;

public static class SysProxyHandler
{
    private static readonly string _tag = "SysProxyHandler";
    private static readonly Lazy<PacManager> _pacManager = new(() => new PacManager());
    private static readonly SemaphoreSlim _proxyGate = new(1, 1);

    // Process-only switch for the isolated UI review executable; never persisted in user configuration.
    public static bool ChangesEnabled { get; set; } = true;

    internal static async Task<ProxyApplyOutcome> ApplyConnectionAsync(ProxyApplyRequest request, Action<string> onFailure,
        Action<ProxyApplyOutcome> publish, CancellationToken token)
    {
        await _proxyGate.WaitAsync(token);
        try
        {
            return await ConnectionProxyApply.EnsureAsync(request, token,
                (r, ct) => UpdateSysProxyCore(r.Config, false, onFailure, ct, r.Port), MatchesAsync, publish);
        }
        finally { _proxyGate.Release(); }
    }

    public static async Task<bool> UpdateSysProxy(Config config, bool forceDisable, Action<string>? onFailure = null, CancellationToken cancellationToken = default,
        int? localPort = null)
    {
        config = JsonUtils.DeepCopy(config);
        var port = localPort ?? AppManager.GetLocalPort(config, EInboundProtocol.socks);
        await _proxyGate.WaitAsync(cancellationToken);
        try { return await UpdateSysProxyCore(config, forceDisable, onFailure, cancellationToken, port); }
        finally { _proxyGate.Release(); }
    }

    private static async Task<bool> UpdateSysProxyCore(Config config, bool forceDisable, Action<string>? onFailure, CancellationToken cancellationToken, int port)
    {
        if (AppManager.Instance.IsExiting && !forceDisable) return false;
        if (!ChangesEnabled)
        {
            onFailure?.Invoke("System proxy changes are disabled in UI review.");
            return false;
        }
        var type = config.SystemProxyItem.SysProxyType;

        if (forceDisable && type != ESysProxyType.Unchanged)
        {
            type = ESysProxyType.ForcedClear;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failed = false;
            void Failed(string error) { failed = true; onFailure?.Invoke(error); }
            if (port <= 0)
            {
                onFailure?.Invoke(ResUI.CheckServerSettings);
                return false;
            }
            switch (type)
            {
                case ESysProxyType.ForcedChange when Utils.IsWindows():
                    var (strProxy, strExceptions) = GetWindowsProxyString(config, port);
                    ProxySettingWindows.SetProxy(strProxy, strExceptions, 2);
                    break;

                case ESysProxyType.ForcedChange when Utils.IsLinux():
                    var exceptions = SanitizeExceptions(config);
                    await ProxySettingLinux.SetProxy(Global.Loopback, port, exceptions, Failed, cancellationToken, config.SystemProxyItem.CustomSystemProxyScriptPath);
                    break;

                case ESysProxyType.ForcedChange when Utils.IsMacOS():
                    var exceptions2 = SanitizeExceptions(config);
                    if (!await ProxySettingOSX.SetProxy(Global.Loopback, port, exceptions2, Failed, cancellationToken, config.SystemProxyItem.CustomSystemProxyScriptPath)) return false;
                    break;

                case ESysProxyType.ForcedClear when Utils.IsWindows():
                    ProxySettingWindows.UnsetProxy();
                    break;

                case ESysProxyType.ForcedClear when Utils.IsLinux():
                    await ProxySettingLinux.UnsetProxy(Failed, cancellationToken, config.SystemProxyItem.CustomSystemProxyScriptPath);
                    break;

                case ESysProxyType.ForcedClear when Utils.IsMacOS():
                    if (!await ProxySettingOSX.UnsetProxy(Failed, cancellationToken, config.SystemProxyItem.CustomSystemProxyScriptPath)) return false;
                    break;

                case ESysProxyType.Pac when Utils.IsWindows():
                    await SetWindowsProxyPac(port, AppManager.GetLocalPort(config, EInboundProtocol.pac),
                        config.SystemProxyItem.CustomSystemProxyPacPath, cancellationToken);
                    break;
            }

            if (type != ESysProxyType.Pac && Utils.IsWindows())
            {
                _pacManager.Value.Stop();
            }
            if (failed) return false;
            if (Utils.IsMacOS() && config.SystemProxyItem.CustomSystemProxyScriptPath.IsNullOrEmpty()
                && type is ESysProxyType.ForcedChange or ESysProxyType.ForcedClear)
            {
                var actual = await MacRecoveryPlatform.ReadProxyAsync(type == ESysProxyType.ForcedChange ? port : 0, cancellationToken);
                if (!actual.Matches)
                {
                    Failed("System proxy read-back mismatch: " + actual.Detail);
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            onFailure?.Invoke(ex.Message);
            Logging.SaveLog(_tag, ex);
            return false;
        }
        return true;
    }

    private static string SanitizeExceptions(Config config)
    {
        var exceptions = config.SystemProxyItem.SystemProxyExceptions;
        if (exceptions.IsNullOrEmpty())
        {
            return string.Empty;
        }

        var items = exceptions
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Replace(" ", string.Empty))
            .Where(item => item.Length > 0);

        return string.Join(',', items);
    }

    private static (string strProxy, string strExceptions) GetWindowsProxyString(Config config, int port)
    {
        var strExceptions = config.SystemProxyItem.SystemProxyExceptions.Replace(" ", "");
        if (config.SystemProxyItem.NotProxyLocalAddress)
        {
            strExceptions = $"<local>;{strExceptions}";
        }

        var strProxy = string.Empty;
        if (config.SystemProxyItem.SystemProxyAdvancedProtocol.IsNullOrEmpty())
        {
            strProxy = $"{Global.Loopback}:{port}";
        }
        else
        {
            strProxy = config.SystemProxyItem.SystemProxyAdvancedProtocol
                .Replace("{ip}", Global.Loopback)
                .Replace("{http_port}", port.ToString())
                .Replace("{socks_port}", port.ToString());
        }

        return (strProxy, strExceptions);
    }

    [SupportedOSPlatform("windows")]
    private static async Task SetWindowsProxyPac(int port, int portPac, string? customPath, CancellationToken token)
    {
        await _pacManager.Value.StartAsync(port, portPac, customPath, token);
        token.ThrowIfCancellationRequested();
        var strProxy = $"{Global.HttpProtocol}{Global.Loopback}:{portPac}/pac?t={DateTime.Now.Ticks}";
        ProxySettingWindows.SetProxy(strProxy, "", 4);
    }

    internal static async Task<bool?> MatchesAsync(ProxyApplyRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var config = request.Config;
        var type = config.SystemProxyItem.SysProxyType;
        if (type == ESysProxyType.Unchanged) return true;
        if (!ChangesEnabled || config.SystemProxyItem.CustomSystemProxyScriptPath.IsNotEmpty()) return null;
        if (Utils.IsMacOS() && type is ESysProxyType.ForcedChange or ESysProxyType.ForcedClear)
        {
            var text = await MacRecoveryPlatform.CommandAsync("/usr/sbin/scutil", ["--proxy"], token);
            if (!MacRecoveryPlatform.ParseProxy(text, type == ESysProxyType.ForcedChange ? request.Port : 0).Matches) return false;
            if (type == ESysProxyType.ForcedClear) return true;
            var block = Regex.Match(text, @"(?s)ExceptionsList\s*:\s*<array>\s*\{(.*?)\}").Groups[1].Value;
            var actual = Regex.Matches(block, @"(?m)^\s*\d+\s*:\s*(.*?)\s*$").Select(m => m.Groups[1].Value).Order().ToArray();
            var expected = SanitizeExceptions(config).Split(',', StringSplitOptions.RemoveEmptyEntries).Order().ToArray();
            return actual.SequenceEqual(expected);
        }
        if (OperatingSystem.IsWindows() && type is ESysProxyType.ForcedChange or ESysProxyType.ForcedClear or ESysProxyType.Pac)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key == null) return null;
            var enabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0;
            if (type == ESysProxyType.Pac)
            {
                var pacPort = AppManager.GetLocalPort(config, EInboundProtocol.pac);
                return !enabled && _pacManager.Value.Matches(request.Port, pacPort, config.SystemProxyItem.CustomSystemProxyPacPath)
                    && Uri.TryCreate(key.GetValue("AutoConfigURL") as string, UriKind.Absolute, out var url)
                    && url.Scheme == "http" && url.Host == Global.Loopback && url.Port == pacPort && url.AbsolutePath == "/pac";
            }
            if (type == ESysProxyType.ForcedClear) return !enabled && string.IsNullOrEmpty(key.GetValue("AutoConfigURL") as string);
            var (server, exceptions) = GetWindowsProxyString(config, request.Port);
            return enabled && (key.GetValue("ProxyServer") as string) == server && (key.GetValue("ProxyOverride") as string ?? "") == exceptions;
        }
        return null;
    }
}
