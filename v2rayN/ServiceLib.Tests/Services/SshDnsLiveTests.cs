using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class SshDnsLiveTests
{
    private sealed record Fixture(bool DisposableFixture, int Port, string Username, string PrivateKeyPath, string HostKey);
    [Test]
    public async Task DisposableUbuntuDeploymentUsesProductionCoordinatorAndRollsBack()
    {
        var path = Environment.GetEnvironmentVariable("SSH_DNS_TEST_PROFILE");
        if (string.IsNullOrEmpty(path)) { Skip.Test("Requires explicitly disposable Ubuntu 24.04 fixture"); return; }
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(path), ProxyCheckService.Json)!;
        if (!fixture.DisposableFixture || fixture.Port < 1024) throw new Exception("Disposable loopback fixture required");
        var node = new ProfileItem { ConfigType = EConfigType.SSH, IndexId = "dns-disposable-fixture",
            Address = "127.0.0.1", Port = fixture.Port, Username = fixture.Username };
        var host = SshProfileService.ParseHostKey(fixture.HostKey);
        node.SetProtocolExtra(new() { SshPrivateKeyAuth = true, SshPrivateKeyPath = fixture.PrivateKeyPath,
            SshHostKey = host.PublicKey, SshHostKeyAlgorithm = host.Algorithm, SshTrustedAddress = node.Address, SshTrustedPort = node.Port });
        var config = CoreConfigTestFactory.CreateConfig();
        config.IndexId = "untouched-current-node";
        CoreConfigTestFactory.BindAppManagerConfig(config);
        await CertPemManager.Instance.Init(config);
        var service = new SshDnsService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        await using var monitor = await SshDnsRemote.OpenAsync(node, "", timeout.Token);
        using var connection = new SshGatewayRemote(node);
        using var ssh = await connection.ConnectAsync(timeout.Token);
        var command = "systemctl show ssh systemd-resolved systemd-networkd -p MainPID; ip -j -4 route";
        using var beforeCommand = ssh.CreateCommand(command);
        await beforeCommand.ExecuteAsync(timeout.Token);
        var before = beforeCommand.Result;
        try
        {
            await using var deployment = await service.PrepareAsync(node, "", timeout.Token);
            Console.WriteLine("PREFLIGHT " + deployment.Plan.ToJsonString());
            var result = await deployment.InstallAsync(timeout.Token);
            Console.WriteLine("VERIFICATION " + JsonSerializer.Serialize(result));
            await result.Verdict.Should().BeEqualTo(SshDnsVerdict.Match);
            var status = await monitor.SendAsync(new { version = 1, action = "status" }, timeout.Token);
            await status["phase"]!.GetValue<string>().Should().BeEqualTo("committed");
            await using var repeated = await service.PrepareAsync(node, "", timeout.Token);
            await repeated.Plan["phase"]!.GetValue<string>().Should().BeEqualTo("committed");
            using var afterCommand = ssh.CreateCommand(command);
            await afterCommand.ExecuteAsync(timeout.Token);
            await afterCommand.Result.Should().BeEqualTo(before);
            await config.IndexId.Should().BeEqualTo("untouched-current-node");
            await ssh.IsConnected.Should().BeTrue();
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var status = await monitor.SendAsync(new { version = 1, action = "status" }, cleanup.Token);
            if (status["id"] != null && status["phase"]?.GetValue<string>() != "rolled_back")
                await service.RollbackAsync(node, "", cleanup.Token);
            Console.WriteLine("FINAL " + (await monitor.SendAsync(new { version = 1, action = "status" }, cleanup.Token)).ToJsonString());
        }
    }
}
