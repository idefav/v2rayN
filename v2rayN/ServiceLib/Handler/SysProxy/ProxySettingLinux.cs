namespace ServiceLib.Handler.SysProxy;

[SupportedOSPlatform("linux")]
public static class ProxySettingLinux
{
    private static readonly string _proxySetFileName = $"{Global.ProxySetLinuxShellFileName.Replace(Global.NamespaceSample, "")}.sh";

    public static async Task SetProxy(string host, int port, string exceptions, Action<string>? onFailure = null)
    {
        List<string> args = ["manual", host, port.ToString(), exceptions];
        await ExecCmd(args, onFailure);
    }

    public static async Task UnsetProxy(Action<string>? onFailure = null)
    {
        List<string> args = ["none"];
        await ExecCmd(args, onFailure);
    }

    private static async Task ExecCmd(List<string> args, Action<string>? onFailure)
    {
        var customSystemProxyScriptPath = AppManager.Instance.Config.SystemProxyItem?.CustomSystemProxyScriptPath;
        var fileName = (customSystemProxyScriptPath.IsNotEmpty() && File.Exists(customSystemProxyScriptPath))
            ? customSystemProxyScriptPath
            : await FileUtils.CreateLinuxShellFile(_proxySetFileName, EmbedUtils.GetEmbedText(Global.ProxySetLinuxShellFileName), false);

        // TODO: temporarily notify which script is being used
        NoticeManager.Instance.SendMessage(fileName);

        if (onFailure == null) await Utils.GetCliWrapOutput(fileName, args);
        else await SystemProxyCommand.Run(fileName, args, onFailure);
    }
}
