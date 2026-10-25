using System.Security.Cryptography;

namespace ServiceLib.Services;

/// <summary>Resolve the explicitly pinned feature core before a retained user-directory core.</summary>
public static class BundledSshCore
{
    public const string ManifestName = "ssh-udp-core.json";

    private sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public string? Version { get; set; }
        public string? Sha256 { get; set; }
    }

    public static string? TryResolve(ECoreType? coreType, string bundledBinDirectory)
    {
        if (coreType != ECoreType.sing_box) return null;
        var directory = Path.Combine(bundledBinDirectory, "sing_box");
        var core = Path.Combine(directory, Utils.GetExeName("sing-box"));
        var manifestPath = Path.Combine(directory, ManifestName);
        if (!File.Exists(core) || !File.Exists(manifestPath)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
            if (manifest?.SchemaVersion != 1 || manifest.Version?.StartsWith("1.14.3-ssh-udp.", StringComparison.Ordinal) != true
                || manifest.Sha256?.Length != 64) return null;
            using var stream = File.OpenRead(core);
            var digest = Convert.ToHexString(SHA256.HashData(stream));
            return digest.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase) ? core : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
