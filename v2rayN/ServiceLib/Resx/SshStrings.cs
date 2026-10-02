namespace ServiceLib.Resx;

public static class SshStrings
{
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
    public static string TcpOnly => ResUI.ResourceManager.GetString("SshTcpOnly", ResUI.Culture) ?? "SshTcpOnly";
}
