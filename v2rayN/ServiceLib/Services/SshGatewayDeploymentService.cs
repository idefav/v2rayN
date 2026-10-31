namespace ServiceLib.Services;

public interface ISshGatewayDeploymentService
{
    Task<ISshGatewayDeployment> PrepareAsync(ProfileItem snapshot, string sudoPassword, CancellationToken token);
}

public sealed class SshGatewayDeploymentService : ISshGatewayDeploymentService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<ISshGatewayDeployment> PrepareAsync(ProfileItem snapshot, string sudoPassword, CancellationToken token)
    {
        if (!await Gate.WaitAsync(0, token)) throw new SshSetupException("E_DEPLOY_BUSY");
        Session? session = null;
        try
        {
            session = new(JsonUtils.DeepCopy(snapshot), sudoPassword);
            await session.PrepareAsync(token);
            return session;
        }
        catch
        {
            if (session != null) await session.DisposeAsync(); else Gate.Release();
            throw;
        }
    }

    private sealed class Session : ISshGatewayDeployment
    {
        public ProfileItem Node { get; }
        public SshGatewayPlan Plan { get; private set; } = null!;
        private string _sudoPassword;
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "v2rayn-udpgw-" + Guid.NewGuid().ToString("N"));
        private string Stage => "/tmp/v2rayn-udpgw-" + _id;
        private readonly SshGatewayRemote _remote;
        private string _helperHash = "", _binaryHash = "", _connection = "";
        private bool _root, _uploaded, _disposed;
        private readonly SshKeyStore _store = new();
        private SshUdpDeploymentRecord _record;

        internal Session(ProfileItem node, string sudoPassword)
        {
            Node = node; _sudoPassword = sudoPassword; _remote = new(node);
            var e = node.GetProtocolExtra();
            _record = new() { TransactionId = _id, Address = node.Address, SshPort = node.Port, Username = node.Username,
                HostFingerprint = SshProfileService.ParseHostKey(e.SshHostKey!, e.SshHostKeyAlgorithm).Fingerprint };
        }

        internal async Task PrepareAsync(CancellationToken token)
        {
            new SshKeyStore(_directory).EnsureDirectory();
            var host = await _remote.InspectHostAsync(token);
            _connection = host.Connection; _root = host.Root;
            var assets = await SshGatewayAssets.PrepareAsync(host.Architecture, _directory, token);
            _helperHash = assets.HelperHash; _binaryHash = assets.BinaryHash;
            await _remote.UploadAsync(Stage, assets.Helper, assets.Binary, () => _uploaded = true, token);
            // Reconcile unfinished records before a new install; a failed
            // connection never implies that the remote transaction rolled back.
            _store.EnsureDirectory();
            foreach (var file in Directory.EnumerateFiles(_store.DirectoryPath, "udpgw-deploy-*.json"))
            {
                SshUdpDeploymentRecord? old;
                try { old = JsonSerializer.Deserialize<SshUdpDeploymentRecord>(await File.ReadAllTextAsync(file, token), ProxyCheckService.Json); }
                catch (JsonException) { continue; }
                if (old == null || old.Address != _record.Address || old.SshPort != _record.SshPort || old.Username != _record.Username
                    || old.HostFingerprint != _record.HostFingerprint || old.State is "committed" or "rolled_back" or "failed") continue;
                SshGatewayReply status;
                try { status = await CallAsync("status", token, transactionId: old.TransactionId); }
                catch (SshSetupException e) when (e.Code == "E_TRANSACTION_UNKNOWN")
                {
                    _store.WriteMetadata(file, old with { State = "failed", UpdatedAt = DateTimeOffset.UtcNow });
                    continue;
                }
                if (status.Transaction is not { } tx) throw new SshSetupException("E_TRANSACTION_UNKNOWN");
                _store.WriteMetadata(file, old with { State = tx.State, UpdatedAt = DateTimeOffset.UtcNow });
                if (tx.State is not ("committed" or "rolled_back" or "failed")) throw new SshSetupException("E_PENDING_TRANSACTION");
            }
            var response = await CallAsync("inspect", token);
            Plan = response.Plan ?? throw new SshSetupException("E_REMOTE_PROTOCOL");
            if (Plan.Port is < 7300 or > 7399 || Plan.Architecture != host.Architecture || Plan.Token.Length != 64)
                throw new SshSetupException("E_REMOTE_PROTOCOL");
        }

        private Task<SshGatewayReply> CallAsync(string action, CancellationToken token,
            Func<SshGatewayReply, CancellationToken, Task>? streaming = null, string? transactionId = null)
            => _remote.HelperAsync(_id, _helperHash, _binaryHash, _root, _sudoPassword,
                new { version = 1, action, transactionId = transactionId ?? _id, planToken = Plan?.Token ?? "",
                    binarySha256 = _binaryHash, username = Node.Username, sshConnection = _connection, enableBypass = Plan?.Ssh.NeedsBypass ?? false }, streaming, token);

        private void Record(string state, SshGatewayTransaction? transaction = null)
        {
            _record = _record with { State = state, UpdatedAt = DateTimeOffset.UtcNow,
                InstallationId = transaction?.Installation.InstallationId ?? _record.InstallationId,
                Port = transaction?.Installation.Port ?? Plan.Port, Version = transaction?.Installation.Version ?? "0.8.7" };
            _store.WriteMetadata(Path.Combine(_store.DirectoryPath, "udpgw-deploy-" + _id + ".json"), _record);
        }

        public async Task<SshUdpDeploymentRecord> InstallAsync(CancellationToken token)
        {
            // Durable, credential-free intent precedes all remote mutations.
            Record("unknown");
            try
            {
                var response = await CallAsync("install", token);
                var transaction = response.Transaction ?? throw new SshSetupException("E_REMOTE_PROTOCOL");
                Record(transaction.State, transaction);
                if (transaction.State != "awaiting_verification") throw new SshSetupException("E_TRANSACTION_STATE");
                var draft = JsonUtils.DeepCopy(Node);
                var gateway = $"127.0.0.1:{Plan.Port}";
                draft.SetProtocolExtra(draft.GetProtocolExtra() with { SshUdpForwarding = true, SshUdpMode = SshUdpPolicy.Tun2Proxy, SshUdpGateway = gateway });
                // A new worker establishes a NEW SSH transport after any reload.
                var capability = await new SshUdpCapabilityService().DetectAsync(draft, token);
                if (capability.Gateway != "ready" || (Plan.Ssh.NeedsBypass && (!capability.PolicyKnown || !capability.BypassEndpoints.Contains(gateway))))
                    throw new SshSetupException("E_NEW_CONNECTION_VERIFICATION");
                await CallAsync("echo", token, async (echo, ct) =>
                {
                    if (string.IsNullOrEmpty(echo.EchoTarget) || echo.EchoToken?.Length != 64) throw new SshSetupException("E_REMOTE_PROTOCOL");
                    await new SshConnectionTestService().RunProxyAsync(draft, async (port, password, coreToken) =>
                    {
                        var result = await ProxyCheckService.WorkerAsync(new("deployment-echo", "UDP", "udp-echo", echo.EchoTarget, "socks",
                            $"socks5://probe:{password}@127.0.0.1:{port}", Query: echo.EchoToken), 10, coreToken);
                        if (result.State != "passed") throw new SshSetupException("E_UDP_ECHO");
                        return true;
                    }, ct);
                });
                var committed = await CallAsync("commit", token);
                if (committed.Transaction?.State != "committed") throw new SshSetupException("E_COMMIT_UNKNOWN");
                Record("committed", committed.Transaction);
                return _record;
            }
            catch
            {
                using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    var status = await CallAsync("status", reconcile.Token);
                    if (status.Transaction is { } transaction)
                    {
                        Record(transaction.State, transaction);
                        // An explicit failed verification/cancel requests rollback;
                        // if disconnected the durable remote timer remains armed.
                        if (transaction.State is "prepared" or "awaiting_verification")
                        {
                            var rollback = await CallAsync("rollback", reconcile.Token);
                            if (rollback.Transaction is { } restored) Record(restored.State, restored);
                        }
                    }
                }
                catch (SshSetupException e) when (e.Code == "E_TRANSACTION_UNKNOWN") { Record("failed"); }
                catch { Record("unknown"); }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return; _disposed = true; _sudoPassword = "";
            try
            {
                if (_uploaded)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await _remote.RemoveStageAsync(Stage, timeout.Token); } catch { }
                }
            }
            finally
            {
                _remote.Dispose();
                try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
                finally { Gate.Release(); }
            }
        }
    }
}
