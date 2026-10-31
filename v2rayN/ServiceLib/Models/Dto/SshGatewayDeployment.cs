namespace ServiceLib.Models.Dto;

public sealed record SshGatewayInstallation(string InstallationId, string Version, string Architecture, string BinarySha256, int Port, string Service);
public sealed record SshGatewaySshPolicy(string Binary, string Config, string Service, bool ForwardProxy, bool BypassSupported, string[] BypassEndpoints, bool NeedsBypass);
public sealed record SshGatewayPlan(string Token, string Architecture, int Port, SshGatewayInstallation? Existing, SshGatewaySshPolicy Ssh);
public sealed record SshGatewayTransaction(string Id, string State, SshGatewayInstallation Installation, DateTimeOffset Deadline, string? Reason);
internal sealed record SshGatewayReply(int Version, string State, string? Error, SshGatewayPlan? Plan, SshGatewayTransaction? Transaction, string? EchoTarget, string? EchoToken);

public interface ISshGatewayDeployment : IAsyncDisposable
{
    ProfileItem Node { get; }
    SshGatewayPlan Plan { get; }
    Task<SshUdpDeploymentRecord> InstallAsync(CancellationToken token);
}
