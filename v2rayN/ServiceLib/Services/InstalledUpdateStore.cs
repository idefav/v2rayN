namespace ServiceLib.Services;

public sealed class InstalledUpdate
{
    public int SchemaVersion { get; set; } = 1;
    public string Repository { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Version { get; set; } = "";
    public string Executable { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

/// <summary>Only explicitly installed updates can override a pinned bundled core.</summary>
public sealed class InstalledUpdateStore(string root)
{
    private static readonly ConcurrentDictionary<string, InstalledUpdate> Pending = new();
    public string Root { get; } = Path.GetFullPath(root);
    public static InstalledUpdateStore For(ECoreType type) => new(Path.Combine(Utils.GetBinPath(""), "updates", type.ToString()));
    private string ActiveFile => Path.Combine(Root, "active.json");

    public InstalledUpdate? Read()
    {
        if (!Pending.TryGetValue(Root, out var record) && !File.Exists(ActiveFile)) return null;
        try
        {
            record ??= JsonSerializer.Deserialize<InstalledUpdate>(File.ReadAllText(ActiveFile));
            if (record?.SchemaVersion != 1 || record.Sha256?.Length != 64 || string.IsNullOrWhiteSpace(record.Executable)
                || !UpdateSources.TryVersion(record.Version, out _) || string.IsNullOrWhiteSpace(record.Repository))
                throw new InvalidDataException("Invalid update record");
            _ = UpdateSources.NormalizeRepository(record.Repository);
            var executable = Resolve(record.Executable);
            if (!File.Exists(executable) || !Digest(executable).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Updated core is missing or its SHA-256 does not match");
            return record;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Logging.SaveLog($"[Core update] {Root}: {ex.Message}; falling back to bundled/legacy core.");
            return null;
        }
    }

    public string Resolve(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (Path.IsPathRooted(relativePath) || !path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Update executable is outside the update directory");
        return path;
    }

    public void Write(InstalledUpdate? record)
    {
        Directory.CreateDirectory(Root);
        if (record == null) { File.Delete(ActiveFile); return; }
        _ = Resolve(record.Executable);
        var temporary = ActiveFile + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record));
            File.Move(temporary, ActiveFile, true);
        }
        finally { File.Delete(temporary); }
    }

    public static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string? Repository(ECoreType type)
    {
        if (type == ECoreType.v2rayN) return AppUpdateProvenance.Repository();
        if (For(type).Read() is { } record) return record.Repository;
        if (BundledSshCore.TryResolve(type, Utils.GetBaseDirectory("bin")) != null) return "idefav/sing-box";
        return null;
    }

    internal async Task ActivateAsync(InstalledUpdate candidate, Func<Task<bool>> restart)
    {
        if (!Pending.TryAdd(Root, candidate)) throw new InvalidOperationException("A core update is already being activated");
        try
        {
            if (!await restart()) throw new InvalidOperationException("The updated core could not start");
            Write(candidate);
        }
        catch
        {
            Pending.TryRemove(Root, out _);
            try
            {
                if (!await restart()) Logging.SaveLog("[Core update] Previous core restored but restart failed.");
            }
            catch (Exception ex) { Logging.SaveLog("[Core update rollback]", ex); }
            throw;
        }
        finally { Pending.TryRemove(Root, out _); }
    }
}
