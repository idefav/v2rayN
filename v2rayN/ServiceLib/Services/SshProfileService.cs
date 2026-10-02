using System.Buffers.Binary;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ServiceLib.Services;

public record SshHostKey(string PublicKey, string Algorithm, string Fingerprint);

public static class SshProfileService
{
    public static SshHostKey ParseHostKey(string value, string? algorithm = null)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new FormatException();
        var bytes = Convert.FromBase64String(parts[1]);
        var offset = 0;
        byte[] ReadField()
        {
            if (bytes.Length - offset < 4) throw new FormatException();
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            if (length > bytes.Length - offset) throw new FormatException();
            var result = bytes.AsSpan(offset, (int)length).ToArray();
            offset += (int)length;
            return result;
        }
        var type = Encoding.ASCII.GetString(ReadField());
        if (parts[0] != type) throw new FormatException();
        switch (type)
        {
            case "ssh-ed25519":
                if (ReadField().Length != 32) throw new FormatException();
                break;
            case "ecdsa-sha2-nistp256":
            case "ecdsa-sha2-nistp384":
            case "ecdsa-sha2-nistp521":
                var curve = Encoding.ASCII.GetString(ReadField());
                var point = ReadField();
                var size = type.EndsWith("256") ? 65 : type.EndsWith("384") ? 97 : 133;
                if (type != "ecdsa-sha2-" + curve || point.Length != size || point[0] != 4)
                    throw new FormatException();
                break;
            case "ssh-rsa":
                if (ReadField().Length == 0 || ReadField().Length < 128) throw new FormatException();
                break;
            default:
                throw new FormatException();
        }
        if (offset != bytes.Length) throw new FormatException();
        algorithm ??= type == "ssh-rsa" ? "rsa-sha2-512" : type;
        if (type == "ssh-rsa"
            ? algorithm is not ("rsa-sha2-512" or "rsa-sha2-256")
            : algorithm != type)
            throw new FormatException();
        return new($"{type} {Convert.ToBase64String(bytes)}", algorithm,
            "SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('='));
    }

    public static async Task<SshHostKey> FetchHostKeyAsync(string address, int port, CancellationToken token)
    {
        // No user credentials are supplied. Reject the key exchange after collecting the public key.
        var info = new ConnectionInfo(address, port, "host-key-probe", new NoneAuthenticationMethod("host-key-probe"))
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        using var client = new SshClient(info);
        SshHostKey? key = null;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = false;
            // HostKeyName is the negotiated signature algorithm; the blob contains the public-key type.
            var length = BinaryPrimitives.ReadUInt32BigEndian(e.HostKey.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(e.HostKey, 4, checked((int)length));
            key = ParseHostKey(type + " " + Convert.ToBase64String(e.HostKey), e.HostKeyName);
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await client.ConnectAsync(timeout.Token);
        }
        catch (SshConnectionException) when (key != null && !timeout.IsCancellationRequested)
        {
            // Expected: CanTrust=false deliberately stops before authentication.
        }
        timeout.Token.ThrowIfCancellationRequested();
        return key ?? throw new InvalidOperationException(SshStrings.FetchFailed);
    }

    public static string? Validate(ProfileItem item, bool checkFile = true, bool requireTrust = true)
    {
        if (string.IsNullOrWhiteSpace(item.Address) || item.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(item.Username))
            return SshStrings.InvalidEndpoint;
        var extra = item.GetProtocolExtra();
        if (extra.SshPrivateKeyAuth == true)
        {
            if (string.IsNullOrWhiteSpace(extra.SshPrivateKeyPath)) return SshStrings.InvalidKeyFile;
            if (checkFile)
            {
                try { using var file = File.OpenRead(extra.SshPrivateKeyPath); }
                catch { return SshStrings.InvalidKeyFile; }
            }
        }
        else if (string.IsNullOrEmpty(item.Password)) return SshStrings.PasswordRequired;
        if (!requireTrust) return null;
        if (extra.SshTrustedAddress != item.Address.Trim() || extra.SshTrustedPort != item.Port)
            return SshStrings.Unconfirmed;
        try
        {
            if (string.IsNullOrEmpty(extra.SshHostKeyAlgorithm)) return SshStrings.InvalidHostKey;
            ParseHostKey(extra.SshHostKey ?? "", extra.SshHostKeyAlgorithm);
        }
        catch { return SshStrings.InvalidHostKey; }
        return null;
    }
}
