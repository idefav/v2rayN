namespace ServiceLib.Services;

/// <summary>Confirm an app update only after the downloaded executable is actually running.</summary>
public static class AppUpdateProvenance
{
    private static string RecordPath(string name) => Path.Combine(Utils.GetConfigPath(), name + ".json");

    public static void Stage(UpdateResult update, string archive)
        => Stage(update, archive, Utils.GetConfigPath(), Environment.ProcessPath!);

    internal static void Stage(UpdateResult update, string archive, string configDirectory, string executable)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
        var executableName = Path.GetFileName(executable);
        var entries = zip.Entries.Where(e => e.Name == executableName && e.FullName.Contains('/')).ToArray();
        if (entries.Length != 1) throw new InvalidDataException("No unique application executable in update archive");
        using var stream = entries[0].Open();
        var record = new InstalledUpdate
        {
            Repository = update.Repository!, Tag = update.Tag!, Version = update.Version!.ToString(),
            Sha256 = Convert.ToHexString(SHA256.HashData(stream)), Executable = executableName!
        };
        File.WriteAllText(Path.Combine(configDirectory, "pending-app-update.json"), JsonSerializer.Serialize(record));
    }

    public static void ClearPending() => File.Delete(RecordPath("pending-app-update"));

    public static void ConfirmStartup()
        => ConfirmStartup(Utils.GetConfigPath(), Environment.ProcessPath!);

    internal static void ConfirmStartup(string configDirectory, string executable)
    {
        var pending = Path.Combine(configDirectory, "pending-app-update.json");
        if (!File.Exists(pending)) return;
        try
        {
            var record = JsonSerializer.Deserialize<InstalledUpdate>(File.ReadAllText(pending));
            if (record != null && MatchesExecutable(record, executable))
            {
                File.Move(pending, Path.Combine(configDirectory, "installed-app-update.json"), true);
                Logging.SaveLog($"[App update] Confirmed {record.Repository} {record.Tag}");
            }
            else Logging.SaveLog("[App update] Downloaded executable is not running; update remains unconfirmed.");
        }
        catch (Exception ex) { Logging.SaveLog("[App update]", ex); }
    }

    public static string? Repository()
    {
        try
        {
            var file = RecordPath("installed-app-update");
            if (File.Exists(file))
            {
                var record = JsonSerializer.Deserialize<InstalledUpdate>(File.ReadAllText(file));
                if (record != null && MatchesExecutable(record, Environment.ProcessPath!)) return record.Repository;
            }
            var source = typeof(AppUpdateProvenance).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildRepository")?.Value;
            return string.IsNullOrWhiteSpace(source) ? null : UpdateSources.NormalizeRepository(source);
        }
        catch (Exception ex) { Logging.SaveLog("[App update source]", ex); return null; }
    }

    public static string CurrentVersion()
    {
        try
        {
            var file = RecordPath("installed-app-update");
            if (File.Exists(file))
            {
                var record = JsonSerializer.Deserialize<InstalledUpdate>(File.ReadAllText(file));
                if (record != null && MatchesExecutable(record, Environment.ProcessPath!)) return record.Version;
            }
            var tag = typeof(AppUpdateProvenance).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildReleaseTag")?.Value;
            if (UpdateSources.TryVersion(tag, out var version)) return version!.ToString();
        }
        catch (Exception ex) { Logging.SaveLog("[App update version]", ex); }
        return Utils.GetVersionInfo();
    }

    private static bool MatchesExecutable(InstalledUpdate record, string executable) =>
        record.SchemaVersion == 1 && UpdateSources.TryVersion(record.Version, out _) && !string.IsNullOrWhiteSpace(record.Repository)
        && InstalledUpdateStore.Digest(executable).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase);
}
