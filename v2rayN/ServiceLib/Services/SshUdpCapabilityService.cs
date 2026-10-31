namespace ServiceLib.Services;

public interface ISshUdpCapabilityService
{
    Task<SshUdpCapabilitySnapshot> DetectAsync(ProfileItem draft, CancellationToken token);
}

public sealed class SshUdpCapabilityService : ISshUdpCapabilityService
{
    internal static JsonObject WorkerOptions(ProfileItem node)
    {
        if (node.ConfigType != EConfigType.SSH || SshProfileService.Validate(node) != null)
            throw new SshSetupException("configuration");
        var e = node.GetProtocolExtra();
        return new JsonObject
        {
            ["server"] = node.Address, ["server_port"] = node.Port, ["user"] = node.Username,
            ["password"] = e.SshPrivateKeyAuth == true ? "" : node.Password,
            ["private_key_path"] = e.SshPrivateKeyAuth == true ? e.SshPrivateKeyPath : null,
            ["private_key_passphrase"] = e.SshPrivateKeyAuth == true ? e.SshPrivateKeyPassphrase : null,
            ["host_key"] = new JsonArray(JsonValue.Create(e.SshHostKey)),
            ["host_key_algorithms"] = new JsonArray(JsonValue.Create(e.SshHostKeyAlgorithm)),
            ["udp_mode"] = SshUdpPolicy.Mode(e), ["udp_gateway"] = SshUdpPolicy.Gateway(e),
        };
    }

    public async Task<SshUdpCapabilitySnapshot> DetectAsync(ProfileItem draft, CancellationToken token)
    {
        var node = JsonUtils.DeepCopy(draft);
        SshUdpCapabilitySnapshot result;
        try
        {
            var response = await ProxyCheckService.WorkerAsync(new("draft-ssh-udp", "UDP", "ssh-udp-capabilities", "", "ssh"),
                10, token, draftSsh: WorkerOptions(node));
            result = response.SshCapabilities ?? new() { Reason = "E_PROBE_COMPONENT_OR_RESPONSE" };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            result = new() { Reason = e is OperationCanceledException ? "E_PROBE_TIMEOUT" : "E_PROBE_FAILED" };
        }
        return result with { Address = node.Address, Port = node.Port, Username = node.Username };
    }
}
