namespace ServiceLib.Tests.CoreConfig.Singbox;

public partial class SshGatewayLiveTests
{
    [Test]
    public async Task DisposableLinuxFailureRollbackAndPersistentWatchdog()
    {
        if (Environment.GetEnvironmentVariable("SSH_UDPGW_LIFECYCLE") != "1")
        { Skip.Test("Explicit SSH_UDPGW_LIFECYCLE=1 required; leaves one disposable transaction for reboot recovery."); return; }
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("SSH_UDPGW_TEST_PROFILE")!), ProxyCheckService.Json)!;
        if (!fixture.DisposableFixture || fixture.Port < 1024) throw new Exception("disposable fixture required");
        var host = SshProfileService.ParseHostKey(fixture.HostKey);
        var node = new ProfileItem { ConfigType = EConfigType.SSH, Address = "127.0.0.1", Port = fixture.Port, Username = fixture.Username };
        node.SetProtocolExtra(new() { SshPrivateKeyAuth = true, SshPrivateKeyPath = fixture.PrivateKeyPath,
            SshHostKey = host.PublicKey, SshHostKeyAlgorithm = host.Algorithm, SshTrustedAddress = node.Address, SshTrustedPort = node.Port });
        var config = CoreConfigTestFactory.CreateConfig(); CoreConfigTestFactory.BindAppManagerConfig(config);
        await CertPemManager.Instance.Init(config);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); var ct = timeout.Token;
        using var remote = new SshGatewayRemote(node);
        var target = await remote.InspectHostAsync(ct);
        var directory = Path.Combine(Path.GetTempPath(), "udpgw-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var stages = new List<string>();
        try
        {
            var assets = await SshGatewayAssets.PrepareAsync(target.Architecture, directory, ct);
            var id = Guid.NewGuid().ToString("N"); var stage = "/tmp/v2rayn-udpgw-" + id;
            await remote.UploadAsync(stage, assets.Helper, assets.Binary, () => stages.Add(stage), ct);
            Task<SshGatewayReply> Call(string action, string tx, string token = "", string? binaryHash = null, bool bypass = false)
                => remote.HelperAsync(id, assets.HelperHash, assets.BinaryHash, target.Root, fixture.SudoPassword,
                    new { version = 1, action, transactionId = tx, planToken = token, binarySha256 = binaryHash ?? assets.BinaryHash,
                        username = node.Username, sshConnection = target.Connection, enableBypass = bypass }, null, ct);

            try { await Call("inspect", id, binaryHash: new string('0', 64)); throw new Exception("bad checksum accepted"); }
            catch (SshSetupException e) when (e.Code == "E_BINARY_CHECKSUM") { }

            var baseline = (await Call("inspect", id)).Plan!;
            if (baseline.Existing == null) throw new Exception("run the successful deployment test first");
            var pending = await Call("install", id, baseline.Token, bypass: baseline.Ssh.NeedsBypass);
            if (pending.Transaction?.State != "awaiting_verification") throw new Exception("transaction not pending");
            try { await Call("commit", id); throw new Exception("commit without UDP evidence accepted"); }
            catch (SshSetupException e) when (e.Code == "E_ECHO_REQUIRED") { }
            if ((await Call("rollback", id)).Transaction?.State != "rolled_back") throw new Exception("rollback failed");

            // Test-only executable passes compatibility preflight then fails to
            // start. Production SshGatewayAssets only admits pinned official ZIPs.
            var failedBinary = Path.Combine(directory, "failing-udpgw-server");
            await File.WriteAllTextAsync(failedBinary, "#!/bin/sh\nif [ \"$1\" = --version ]; then echo 'udpgw-server 0.8.7'; exit 0; fi\nexit 99\n", ct);
            var failedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(failedBinary, ct))).ToLowerInvariant();
            var failedId = Guid.NewGuid().ToString("N"); var failedStage = "/tmp/v2rayn-udpgw-" + failedId;
            await remote.UploadAsync(failedStage, assets.Helper, failedBinary, () => stages.Add(failedStage), ct);
            Task<SshGatewayReply> FailCall(string action, string token = "")
                => remote.HelperAsync(failedId, assets.HelperHash, failedHash, target.Root, fixture.SudoPassword,
                    new { version = 1, action, transactionId = failedId, planToken = token, binarySha256 = failedHash,
                        username = node.Username, sshConnection = target.Connection, enableBypass = false }, null, ct);
            var failurePlan = (await FailCall("inspect")).Plan!;
            if (failurePlan.Ssh.NeedsBypass) throw new Exception("failure fixture must already have reachable gateway");
            try { await FailCall("install", failurePlan.Token); throw new Exception("failed service accepted"); }
            catch (SshSetupException e) when (e.Code is "E_SERVICE_START" or "E_SERVICE_LISTENER") { }
            if ((await FailCall("status")).Transaction?.State != "rolled_back") throw new Exception("start failure did not roll back");
            var restored = (await Call("inspect", id)).Plan!;
            if (restored.Existing?.InstallationId != baseline.Existing.InstallationId) throw new Exception("managed identity lost");

            // Simulate a client disappearing after installation. No commit or
            // rollback is sent; the persistent remote timer owns recovery.
            var abandoned = Guid.NewGuid().ToString("N");
            var result = await Call("install", abandoned, restored.Token, bypass: restored.Ssh.NeedsBypass);
            if (result.Transaction?.State != "awaiting_verification") throw new Exception("watchdog fixture not armed");
            Console.WriteLine("Disposable watchdog transaction: " + abandoned);
        }
        finally
        {
            foreach (var stage in stages) try { await remote.RemoveStageAsync(stage, CancellationToken.None); } catch { }
            Directory.Delete(directory, true);
        }
    }
}
