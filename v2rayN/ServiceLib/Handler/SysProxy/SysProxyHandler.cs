namespace ServiceLib.Handler.SysProxy;

public static class SysProxyHandler
{
    private static readonly string _tag = "SysProxyHandler";
    private static readonly Lazy<PacManager> _pacManager = new(() => new PacManager());
    private static readonly SemaphoreSlim _proxyGate = new(1, 1);

    // Process-only switch for the isolated UI review executable; never persisted in user configuration.
    public static bool ChangesEnabled { get; set; } = true;

    public static async Task<bool> UpdateSysProxy(Config config, bool forceDisable, Action<string>? onFailure = null, CancellationToken cancellationToken = default)
    {
        await _proxyGate.WaitAsync(cancellationToken);
        try { return await UpdateSysProxyCore(config, forceDisable, onFailure, cancellationToken); }
        finally { _proxyGate.Release(); }
    }

    private static async Task<bool> UpdateSysProxyCore(Config config, bool forceDisable, Action<string>? onFailure, CancellationToken cancellationToken)
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
            var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
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
                    await ProxySettingLinux.SetProxy(Global.Loopback, port, exceptions, Failed);
                    break;

                case ESysProxyType.ForcedChange when Utils.IsMacOS():
                    var exceptions2 = SanitizeExceptions(config);
                    if (!await ProxySettingOSX.SetProxy(Global.Loopback, port, exceptions2, Failed, cancellationToken)) return false;
                    break;

                case ESysProxyType.ForcedClear when Utils.IsWindows():
                    ProxySettingWindows.UnsetProxy();
                    break;

                case ESysProxyType.ForcedClear when Utils.IsLinux():
                    await ProxySettingLinux.UnsetProxy(Failed);
                    break;

                case ESysProxyType.ForcedClear when Utils.IsMacOS():
                    if (!await ProxySettingOSX.UnsetProxy(Failed, cancellationToken)) return false;
                    break;

                case ESysProxyType.Pac when Utils.IsWindows():
                    await SetWindowsProxyPac(port);
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
    private static async Task SetWindowsProxyPac(int port)
    {
        var portPac = AppManager.Instance.GetLocalPort(EInboundProtocol.pac);
        await _pacManager.Value.StartAsync(port, portPac);
        var strProxy = $"{Global.HttpProtocol}{Global.Loopback}:{portPac}/pac?t={DateTime.Now.Ticks}";
        ProxySettingWindows.SetProxy(strProxy, "", 4);
    }
}
