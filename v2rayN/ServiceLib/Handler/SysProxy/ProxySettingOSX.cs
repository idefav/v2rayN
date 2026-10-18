namespace ServiceLib.Handler.SysProxy;

[SupportedOSPlatform("macos")]
public static class ProxySettingOSX
{
    private static readonly string _proxySetFileName = $"{Global.ProxySetOSXShellFileName.Replace(Global.NamespaceSample, "")}.sh";

    public static async Task<bool> SetProxy(string host, int port, string exceptions, Action<string>? onFailure = null, CancellationToken cancellationToken = default)
    {
        List<string> args = ["set", host, port.ToString()];
        if (exceptions.IsNotEmpty())
        {
            args.AddRange(exceptions.Split(','));
        }

        return await ExecCmd(args, onFailure, cancellationToken);
    }

    public static async Task<bool> UnsetProxy(Action<string>? onFailure = null, CancellationToken cancellationToken = default)
    {
        List<string> args = ["clear"];
        return await ExecCmd(args, onFailure, cancellationToken);
    }

    private static async Task<bool> ExecCmd(List<string> args, Action<string>? onFailure, CancellationToken cancellationToken)
    {
        var customSystemProxyScriptPath = AppManager.Instance.Config.SystemProxyItem?.CustomSystemProxyScriptPath;
        var fileName = (customSystemProxyScriptPath.IsNotEmpty() && File.Exists(customSystemProxyScriptPath))
            ? customSystemProxyScriptPath
            : await FileUtils.CreateLinuxShellFile(_proxySetFileName, EmbedUtils.GetEmbedText(Global.ProxySetOSXShellFileName), true);

        // TODO: temporarily notify which script is being used
        NoticeManager.Instance.SendMessage(fileName);

        return await SystemProxyCommand.Run(fileName, args, onFailure, cancellationToken);
    }
}
