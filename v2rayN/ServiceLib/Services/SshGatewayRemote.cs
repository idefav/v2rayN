using Renci.SshNet;

namespace ServiceLib.Services;

/// <summary>Every connection (exec and SFTP) pins the confirmed key; no banner-based trust.</summary>
internal sealed class SshGatewayRemote : IDisposable
{
    private readonly ProfileItem _node;
    private readonly SshSetupTarget _target;
    private readonly PrivateKeyFile? _key;
    private readonly AuthenticationMethod _auth;
    internal SshGatewayRemote(ProfileItem snapshot)
    {
        _node = JsonUtils.DeepCopy(snapshot);
        if (_node.ConfigType != EConfigType.SSH || SshProfileService.Validate(_node) != null) throw new SshSetupException("configuration");
        var e = _node.GetProtocolExtra();
        _target = new(_node.Address, _node.Port, _node.Username, SshProfileService.ParseHostKey(e.SshHostKey!, e.SshHostKeyAlgorithm));
        if (e.SshPrivateKeyAuth == true)
        {
            _key = new SshKeyStore().Open(new(e.SshPrivateKeyPath!, null, null, null, false), e.SshPrivateKeyPassphrase);
            _auth = new PrivateKeyAuthenticationMethod(_node.Username, _key);
        }
        else _auth = new PasswordAuthenticationMethod(_node.Username, _node.Password);
    }

    internal Task<SshClient> ConnectAsync(CancellationToken token) => SshPinnedConnection.ConnectAsync(_target, _auth, token);

    internal async Task<(string Architecture, string Connection, bool Root)> InspectHostAsync(CancellationToken token)
    {
        using var client = await ConnectAsync(token);
        using var command = client.CreateCommand("uname -s; uname -m; id -u; printf '%s\\n' \"$SSH_CONNECTION\"");
        await command.ExecuteAsync(token);
        var lines = command.Result.Trim().Split('\n').Select(s => s.Trim()).ToArray();
        if (command.ExitStatus != 0 || lines.Length != 4 || lines[0] != "Linux") throw new SshSetupException("E_LINUX_REQUIRED");
        var arch = lines[1] switch { "x86_64" => "amd64", "aarch64" => "arm64", _ => throw new SshSetupException("E_ARCH_UNSUPPORTED") };
        return (arch, lines[3], lines[2] == "0");
    }

    internal async Task UploadAsync(string stage, string helper, string binary, Action created, CancellationToken token)
    {
        var info = new ConnectionInfo(_target.Address, _target.Port, _target.Username, _auth) { Timeout = TimeSpan.FromSeconds(12) };
        foreach (var algorithm in info.HostKeyAlgorithms.Keys.Where(a => a != _target.HostKey.Algorithm).ToArray()) info.HostKeyAlgorithms.Remove(algorithm);
        using var sftp = new SftpClient(info);
        var keyBytes = Convert.FromBase64String(_target.HostKey.PublicKey.Split(' ')[1]);
        sftp.HostKeyReceived += (_, e) => e.CanTrust = CryptographicOperations.FixedTimeEquals(keyBytes, e.HostKey);
        await sftp.ConnectAsync(token);
        using var cancel = token.Register(() => { try { sftp.Dispose(); } catch { } });
        await Task.Run(() =>
        {
            sftp.CreateDirectory(stage);
            created();
            sftp.ChangePermissions(stage, 700);
            foreach (var file in new[] { (helper, "helper"), (binary, "udpgw-server") })
            {
                token.ThrowIfCancellationRequested();
                using var source = File.OpenRead(file.Item1);
                sftp.UploadFile(source, stage + "/" + file.Item2, false);
                sftp.ChangePermissions(stage + "/" + file.Item2, 700);
            }
        }, token);
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    internal async Task<SshGatewayReply> HelperAsync(string stageId, string helperHash, string binaryHash, bool root, string sudoPassword,
        object request, Func<SshGatewayReply, CancellationToken, Task>? streaming, CancellationToken token)
    {
        // Each operation uses a fresh authenticated SSH connection, particularly
        // verification after a config reload. Credentials never enter argv.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var ct = deadline.Token;
        using var client = await ConnectAsync(ct);
        var script = SshGatewayAssets.Resource("udpgw-bootstrap.sh");
        var prefix = root ? "" : "sudo -S -p 'V2RAYN_UDPGW_SUDO:' -- ";
        using var command = client.CreateCommand(prefix + "sh -c " + Quote(script) + " v2rayn " + Quote(stageId) + " " + Quote(helperHash) + " " + Quote(binaryHash));
        using var cancel = ct.Register(() => { try { command.Dispose(); client.Disconnect(); } catch { } });
        var execution = command.ExecuteAsync(ct);
        var input = command.CreateInputStream();
        using var reader = new StreamReader(command.OutputStream, Encoding.UTF8, false, 1024, true);
        var errors = HandleSudoAsync(command.ExtendedOutputStream, input, sudoPassword, deadline);
        try
        {
            var ready = await ReadLineAsync(reader, ct);
            if (ready.StartsWith("V2RAYN_UDPGW_BOOTSTRAP_ERROR_", StringComparison.Ordinal))
                throw new SshSetupException(SafeCode("E_BOOTSTRAP_" + ready["V2RAYN_UDPGW_BOOTSTRAP_ERROR_".Length..]));
            if (ready != "V2RAYN_UDPGW_READY") throw new SshSetupException("E_REMOTE_BOOTSTRAP");
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, ProxyCheckService.Json) + "\n");
            await input.WriteAsync(bytes, ct);
            await input.FlushAsync(ct);
            SshGatewayReply reply;
            while (true)
            {
                var line = await ReadLineAsync(reader, ct);
                if (line.StartsWith("V2RAYN_UDPGW_BOOTSTRAP_ERROR_", StringComparison.Ordinal))
                    throw new SshSetupException(SafeCode("E_BOOTSTRAP_" + line["V2RAYN_UDPGW_BOOTSTRAP_ERROR_".Length..]));
                reply = JsonSerializer.Deserialize<SshGatewayReply>(line, ProxyCheckService.Json)
                    ?? throw new SshSetupException("E_REMOTE_PROTOCOL");
                if (reply.Version != 1) throw new SshSetupException("E_REMOTE_PROTOCOL");
                if (reply.State != "echo_ready") break;
                if (streaming == null) throw new SshSetupException("E_REMOTE_PROTOCOL");
                await streaming(reply, ct);
            }
            await execution.WaitAsync(ct);
            await errors;
            if (command.ExitStatus != 0) throw new SshSetupException("E_REMOTE_EXIT");
            if (reply.State != "ok") throw new SshSetupException(SafeCode(reply.Error));
            return reply;
        }
        catch
        {
            // Sudo failure cancels the transport before its task becomes
            // faulted. Await that completion rather than racing IsFaulted.
            if (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            {
                try { await errors; }
                catch (SshSetupException) { throw; }
                catch { }
            }
            throw;
        }
        finally
        {
            // The helper closes its channel after its reply. Sending EOF to
            // that already-closed channel must not hide the transaction result.
            try { input.Dispose(); } catch (InvalidOperationException) { }
            command.Dispose();
            try { await execution; } catch { }
            try { await errors; } catch { }
        }
    }

    private static async Task HandleSudoAsync(Stream stream, Stream input, string password, CancellationTokenSource deadline)
    {
        var buffer = new byte[256]; var tail = ""; var total = 0; var sent = false;
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if ((total += n) > 16384) throw new SshSetupException("E_REMOTE_OUTPUT");
                tail += Encoding.ASCII.GetString(buffer, 0, n);
                if (tail.Contains("V2RAYN_UDPGW_SUDO:", StringComparison.Ordinal))
                {
                    if (sent || password.Length == 0 || password.Contains('\n') || password.Contains('\r')) throw new SshSetupException("E_SUDO_PASSWORD");
                    sent = true; tail = "";
                    var bytes = Encoding.UTF8.GetBytes(password + "\n");
                    try { await input.WriteAsync(bytes, deadline.Token); await input.FlushAsync(deadline.Token); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                if (tail.Length > 64) tail = tail[^64..];
            }
        }
        catch { deadline.Cancel(); throw; }
    }

    internal static string SafeCode(string? code) => code != null && code.Length < 80 && code.StartsWith("E_", StringComparison.Ordinal)
        && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') ? code : "E_DEPLOY_FAILED";

    private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        // JSON helpers emit one bounded line, never raw remote output.
        var value = new StringBuilder(); var buffer = new char[1];
        while (value.Length < 65536)
        {
            var n = await reader.ReadAsync(buffer, token);
            if (n == 0) throw new SshSetupException("E_REMOTE_EOF");
            if (buffer[0] == '\n') return value.ToString();
            value.Append(buffer[0]);
        }
        throw new SshSetupException("E_REMOTE_OUTPUT");
    }

    internal async Task RemoveStageAsync(string stage, CancellationToken token)
    {
        using var client = await ConnectAsync(token);
        // Only the random stage created by this object is removed.
        using var command = client.CreateCommand("rm -f -- " + Quote(stage + "/helper") + " " + Quote(stage + "/udpgw-server") + " && rmdir -- " + Quote(stage));
        await command.ExecuteAsync(token);
    }

    public void Dispose() { _auth.Dispose(); _key?.Dispose(); }
}
