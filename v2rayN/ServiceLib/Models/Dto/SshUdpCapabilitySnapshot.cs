namespace ServiceLib.Models.Dto;

public sealed record SshUdpCapabilitySnapshot
{
    public string Custom { get; init; } = "unknown";
    public string Reason { get; init; } = "E_NOT_CHECKED";
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    public string HostFingerprint { get; init; } = "";
    public string Address { get; init; } = "";
    public int Port { get; init; }
    public string Username { get; init; } = "";
    public bool PolicyKnown { get; init; }
    public bool ForwardProxyEnabled { get; init; }
    public bool BypassSupported { get; init; }
    public string[] BypassEndpoints { get; init; } = [];
    public string Gateway { get; init; } = "unknown";
    public string GatewayReason { get; init; } = "";
    public bool CustomSelectable => Custom is "enabled" or "unknown";
}

public sealed record SshUdpDeploymentRecord
{
    public string TransactionId { get; init; } = "";
    public string InstallationId { get; init; } = "";
    public string Version { get; init; } = "";
    public string Service { get; init; } = "v2rayn-udpgw.service";
    public int Port { get; init; }
    public string HostFingerprint { get; init; } = "";
    public string Address { get; init; } = "";
    public string Username { get; init; } = "";
    public int SshPort { get; init; }
    public string State { get; init; } = "unknown";
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
