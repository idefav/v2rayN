namespace ServiceLib.Tests.CoreConfig.Singbox;

[NotInParallel]
public partial class SshGatewayLiveTests
{
    private sealed record Fixture(bool DisposableFixture, int Port, string Username, string PrivateKeyPath, string HostKey, bool NeedsBypass, string SudoPassword = "");

    [Test]
    public async Task DisposableLinuxDeploymentAndRepeatedInstallVerifyTheRealGateway()
    {
        var path = Environment.GetEnvironmentVariable("SSH_UDPGW_TEST_PROFILE");
        if (string.IsNullOrEmpty(path)) { Skip.Test("Set SSH_UDPGW_TEST_PROFILE for an explicitly disposable loopback-forwarded Linux fixture."); return; }
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(path), ProxyCheckService.Json)!;
        if (!fixture.DisposableFixture || fixture.Port < 1024) throw new Exception("disposable fixture required");
        var node = new ProfileItem { ConfigType = EConfigType.SSH, IndexId = "live-gateway-draft", Address = "127.0.0.1", Port = fixture.Port, Username = fixture.Username };
        var host = SshProfileService.ParseHostKey(fixture.HostKey);
        node.SetProtocolExtra(new() { SshPrivateKeyAuth = true, SshPrivateKeyPath = fixture.PrivateKeyPath, SshHostKey = host.PublicKey,
            SshHostKeyAlgorithm = host.Algorithm, SshTrustedAddress = node.Address, SshTrustedPort = node.Port });
        var config = CoreConfigTestFactory.CreateConfig(); config.IndexId = "untouched-running-profile";
        CoreConfigTestFactory.BindAppManagerConfig(config);
        await CertPemManager.Instance.Init(config);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        SshUdpDeploymentRecord? previous = null;
        for (var i = 0; i < 2; i++)
        {
            await using var deployment = await new SshGatewayDeploymentService().PrepareAsync(node, fixture.SudoPassword, timeout.Token);
            if (i == 0 && deployment.Plan.Ssh.NeedsBypass != fixture.NeedsBypass) throw new Exception("unexpected SSH policy");
            var record = await deployment.InstallAsync(timeout.Token);
            if (record.State != "committed" || record.Port is < 7300 or > 7399 || record.Version != "0.8.7") throw new Exception("unverified deployment");
            if (previous != null && (previous.InstallationId != record.InstallationId || previous.Port != record.Port)) throw new Exception("repeat install replaced managed identity");
            if (config.IndexId != "untouched-running-profile" || node.GetProtocolExtra().SshUdpForwarding == true) throw new Exception("running profile changed");
            previous = record;
        }
    }
}
