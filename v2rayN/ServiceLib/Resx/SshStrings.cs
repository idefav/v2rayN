namespace ServiceLib.Resx;

public static partial class SshStrings
{
    public static string UdpForwarding => ProxyCheckText.T("启用 SSH UDP 转发", "Enable SSH UDP forwarding");
    public static string UdpMode => ProxyCheckText.T("UDP 转发模式", "UDP forwarding mode");
    public static string UdpCustom => ProxyCheckText.T("自定义 SSH UDP 扩展", "Custom SSH UDP extension");
    public static string UdpTun2Proxy => ProxyCheckText.T("tun2proxy 网关", "tun2proxy gateway");
    public static string UdpGateway => ProxyCheckText.T("网关地址（从 SSH 服务端访问）", "Gateway address (as reached by the SSH server)");
    public static string UdpModeNotice => ProxyCheckText.T("自定义模式需要配套 OpenSSH UDP 扩展。tun2proxy 模式需要 udpgw-server 和 TCP 转发权限；网关连接受服务端 ForwardProxy 影响，网关 UDP 出口需单独配置。两种模式均需要配套核心。", "Custom mode requires the OpenSSH UDP extension. tun2proxy requires udpgw-server and TCP forwarding permission; ForwardProxy affects gateway access and the gateway's UDP egress is configured separately. Both modes require the bundled core.");
    public static string InvalidUdpMode => ProxyCheckText.T("请选择有效的 SSH UDP 转发模式。", "Select a valid SSH UDP forwarding mode.");
    public static string InvalidUdpGateway => ProxyCheckText.T("网关地址格式应为主机:端口或 [IPv6]:端口，端口范围 1–65535。", "Gateway must be host:port or [IPv6]:port, with a port from 1 to 65535.");
    public static string AllowHttp3 => ProxyCheckText.T("允许 HTTP3：忽略当前规则集的默认“阻断udp443”规则", "Allow HTTP3: skip the routing set's default UDP 443 block");
    public static string UdpNotice => ProxyCheckText.T("默认关闭。开启后通过 SSH 传输 UDP，失败不会回退直连。允许 HTTP3 只跳过未附加目标条件的内置 UDP 443 拦截；其他自定义拒绝规则保留。", "Disabled by default. UDP travels over SSH with no direct fallback. HTTP3 bypasses only the unqualified built-in UDP 443 block; other custom reject rules are preserved.");
    private static string Get(string name) => ResUI.ResourceManager.GetString("Ssh" + name, ResUI.Culture) ?? name;
    public static string TestConnection => Get(nameof(TestConnection));
    public static string StopTest => Get(nameof(StopTest));
    public static string TestResult(SshConnectionTestResult result)
    {
        return $"{Get("TestTarget")}: {result.Url}\n{TestStage(true, result.Login)}\n{TestStage(false, result.Proxy)}";
    }
    public static string TestStage(bool login, SshTestStage stage)
    {
        var state = Get("Test" + stage.State);
        var detail = stage.Error == null ? "" : " · " + Get("TestError_" + stage.Error);
        var http = stage.HttpStatus == null ? "" : $" · HTTP {stage.HttpStatus}";
        var elapsed = stage.State is SshTestState.Running or SshTestState.Waiting or SshTestState.Skipped ? "" : $" · {stage.ElapsedMs} ms";
        return $"{Get(login ? "TestLogin" : "TestProxy")}: {state}{http}{elapsed}{detail}";
    }
    public static string SetupTitle => Get(nameof(SetupTitle));
    public static string SetupKey => Get(nameof(SetupKey));
    public static string KeysFound => Get(nameof(KeysFound));
    public static string NoKeysFound => Get(nameof(NoKeysFound));
    public static string ReuseKey => Get(nameof(ReuseKey));
    public static string GenerateKey => Get(nameof(GenerateKey));
    public static string KeyLocked => Get(nameof(KeyLocked));
    public static string KeyUnencrypted => Get(nameof(KeyUnencrypted));
    public static string SamePublicKey => Get(nameof(SamePublicKey));
    public static string SetupRun => Get(nameof(SetupRun));
    public static string SetupCancel => Get(nameof(SetupCancel));
    public static string SetupPassword => Get(nameof(SetupPassword));
    public static string SetupPassphrase => Get(nameof(SetupPassphrase));
    public static string ConfirmPassphrase => Get(nameof(ConfirmPassphrase));
    public static string SetupNotice => Get(nameof(SetupNotice));
    public static string SetupVerified => Get(nameof(SetupVerified));
    public static string ManualKey => Get(nameof(ManualKey));
    public static string ManualHelp => Get(nameof(ManualHelp));
    public static string RemoteMayRemain => Get(nameof(RemoteMayRemain));
    public static string DefaultKeySaveFailed => Get(nameof(DefaultKeySaveFailed));
    public static string StageConnecting => Get(nameof(StageConnecting));
    public static string StageGenerating => Get(nameof(StageGenerating));
    public static string StageInstalling => Get(nameof(StageInstalling));
    public static string StageVerifying => Get(nameof(StageVerifying));
    public static string ErrorKeyPermissions => Get(nameof(ErrorKeyPermissions));
    public static string ErrorKeyChanged => Get(nameof(ErrorKeyChanged));
    public static string ErrorInvalidKey => Get(nameof(ErrorInvalidKey));
    public static string ErrorBusy => Get(nameof(ErrorBusy));
    public static string ErrorPasswordRequired => Get(nameof(ErrorPasswordRequired));
    public static string ErrorPassphraseMismatch => Get(nameof(ErrorPassphraseMismatch));
    public static string ErrorAuthentication => Get(nameof(ErrorAuthentication));
    public static string ErrorHostKey => Get(nameof(ErrorHostKey));
    public static string ErrorRemoteUnsupported => Get(nameof(ErrorRemoteUnsupported));
    public static string ErrorRemoteConflict => Get(nameof(ErrorRemoteConflict));
    public static string ErrorAuthorizedKeys => Get(nameof(ErrorAuthorizedKeys));
    public static string ErrorVerifyFailed => Get(nameof(ErrorVerifyFailed));
    public static string ErrorStorage => Get(nameof(ErrorStorage));
    public static string ErrorConnection => Get(nameof(ErrorConnection));
    public static string ErrorTimeout => Get(nameof(ErrorTimeout));
    public static string SetupStage(string stage) => stage switch
    {
        "connecting" => StageConnecting, "generating" => StageGenerating,
        "installing" => StageInstalling, _ => StageVerifying,
    };
    public static string SetupError(string code) => code switch
    {
        "keyPermissions" => ErrorKeyPermissions, "keyChanged" => ErrorKeyChanged,
        "invalidKey" => ErrorInvalidKey, "busy" => ErrorBusy, "endpoint" => InvalidEndpoint,
        "passwordRequired" => ErrorPasswordRequired, "passphraseMismatch" => ErrorPassphraseMismatch,
        "authentication" => ErrorAuthentication, "hostKey" => ErrorHostKey,
        "remoteUnsupported" => ErrorRemoteUnsupported, "remoteConflict" => ErrorRemoteConflict,
        "authorizedKeys" => ErrorAuthorizedKeys, "verifyFailed" => ErrorVerifyFailed,
        "storage" => ErrorStorage, "timeout" => ErrorTimeout, "cancelled" => Cancelled,
        _ => ErrorConnection,
    };
    public static string LoginPassword => ResUI.ResourceManager.GetString("SshLoginPassword", ResUI.Culture) ?? "Password";
    public static string Title => ResUI.ResourceManager.GetString("SshTitle", ResUI.Culture) ?? "SshTitle";
    public static string PrivateKeyAuth => ResUI.ResourceManager.GetString("SshPrivateKeyAuth", ResUI.Culture) ?? "SshPrivateKeyAuth";
    public static string PrivateKeyPath => ResUI.ResourceManager.GetString("SshPrivateKeyPath", ResUI.Culture) ?? "SshPrivateKeyPath";
    public static string Passphrase => ResUI.ResourceManager.GetString("SshPassphrase", ResUI.Culture) ?? "SshPassphrase";
    public static string HostKey => ResUI.ResourceManager.GetString("SshHostKey", ResUI.Culture) ?? "SshHostKey";
    public static string Fetch => ResUI.ResourceManager.GetString("SshFetch", ResUI.Culture) ?? "SshFetch";
    public static string Trust => ResUI.ResourceManager.GetString("SshTrust", ResUI.Culture) ?? "SshTrust";
    public static string ConfirmKey => ResUI.ResourceManager.GetString("SshConfirmKey", ResUI.Culture) ?? "SshConfirmKey";
    public static string Pinned => ResUI.ResourceManager.GetString("SshPinned", ResUI.Culture) ?? "SshPinned";
    public static string Unconfirmed => ResUI.ResourceManager.GetString("SshUnconfirmed", ResUI.Culture) ?? "SshUnconfirmed";
    public static string InvalidEndpoint => ResUI.ResourceManager.GetString("SshInvalidEndpoint", ResUI.Culture) ?? "SshInvalidEndpoint";
    public static string InvalidKeyFile => ResUI.ResourceManager.GetString("SshInvalidKeyFile", ResUI.Culture) ?? "SshInvalidKeyFile";
    public static string PasswordRequired => ResUI.ResourceManager.GetString("SshPasswordRequired", ResUI.Culture) ?? "SshPasswordRequired";
    public static string InvalidHostKey => ResUI.ResourceManager.GetString("SshInvalidHostKey", ResUI.Culture) ?? "SshInvalidHostKey";
    public static string FetchFailed => ResUI.ResourceManager.GetString("SshFetchFailed", ResUI.Culture) ?? "SshFetchFailed";
    public static string Fetching => ResUI.ResourceManager.GetString("SshFetching", ResUI.Culture) ?? "SshFetching";
    public static string Cancelled => ResUI.ResourceManager.GetString("SshCancelled", ResUI.Culture) ?? "SshCancelled";
    public static string DnsUnsupported => ResUI.ResourceManager.GetString("SshDnsUnsupported", ResUI.Culture) ?? "SshDnsUnsupported";
    public static string TcpOnly => ProxyCheckText.T("默认代理 TCP；UDP 可选择自定义 SSH 扩展或 tun2proxy 网关。", "TCP is proxied by default; UDP can use the custom SSH extension or a tun2proxy gateway.");
}
