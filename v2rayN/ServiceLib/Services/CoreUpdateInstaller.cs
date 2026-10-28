using System.Formats.Tar;
using System.IO.Compression;
using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Services;

public sealed class CoreUpdateInstaller
{
    public Task InstallAsync(ECoreType type, UpdateResult update, string archive, Config config,
        Func<string, Task<bool>> restart, CancellationToken cancellationToken = default)
        => InstallAsync(type, update, archive, InstalledUpdateStore.For(type),
            (exe, info, token) => ValidateCurrentConfigAsync(type, exe, info, config, token), restart, cancellationToken);

    internal async Task InstallAsync(ECoreType type, UpdateResult update, string archive, InstalledUpdateStore store,
        Func<string, CoreInfo, CancellationToken, Task<bool>> validateConfig,
        Func<string, Task<bool>> restart, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(store.Root, Guid.NewGuid().ToString("N"));
        var activated = false;
        Directory.CreateDirectory(directory);
        try
        {
            Extract(archive, directory, Utils.GetExeName("mihomo"));
            var info = CoreInfoManager.Instance.GetCoreInfo(type)!;
            var names = info.CoreExes!.Select(Utils.GetExeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matches = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Where(f => names.Contains(Path.GetFileName(f))).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Archive must contain exactly one matching core executable");
            var executable = matches[0];
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            var versionOutput = await RunAsync(executable, [info.VersionArg!], info, cancellationToken);
            var actual = UpdateService.ParseCoreVersion(versionOutput);
            if (actual == null || !actual.Equals(update.Version)) throw new InvalidDataException("Downloaded core version does not match the selected release");

            var usesCore = await validateConfig(executable, info, cancellationToken);
            var record = new InstalledUpdate
            {
                Repository = update.Repository!, Tag = update.Tag!, Version = actual.ToString(),
                Executable = Path.GetRelativePath(store.Root, executable), Sha256 = InstalledUpdateStore.Digest(executable)
            };
            await store.ActivateAsync(record, () => usesCore
                ? restart($"{type} 更新／切换来源：{record.Repository} {record.Tag}") : Task.FromResult(true));
            activated = true;
        }
        finally
        {
            if (!activated) Directory.Delete(directory, true);
        }
    }

    internal static void Extract(string archive, string directory, string gzipName)
    {
        Directory.CreateDirectory(directory);
        if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            while (reader.GetNextEntry() is { } entry)
            {
                var target = SafePath(directory, entry.Name);
                if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("Unsupported archive entry: " + entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = File.Create(target);
                entry.DataStream?.CopyTo(output);
            }
        }
        else if (archive.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var output = File.Create(SafePath(directory, gzipName));
            gzip.CopyTo(output);
        }
        else
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                var target = SafePath(directory, entry.FullName);
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Archive symbolic links are not supported");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, false);
            }
        }
    }

    private static string SafePath(string directory, string name)
    {
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, name.Replace('\\', '/')));
        if (!path.StartsWith(root, StringComparison.Ordinal) && path != root.TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidDataException("Archive path escapes its destination");
        return path;
    }

    private static async Task<string> RunAsync(string executable, string[] args, CoreInfo info, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await Cli.Wrap(executable).WithArguments(args)
            .WithWorkingDirectory(Path.GetDirectoryName(executable)!)
            .WithEnvironmentVariables(new Dictionary<string, string?>(info.Environment)).ExecuteBufferedAsync(timeout.Token);
        return result.StandardOutput + "\n" + result.StandardError;
    }

    private static async Task<bool> ValidateCurrentConfigAsync(ECoreType type, string executable, CoreInfo info, Config config, CancellationToken token)
    {
        var profile = await ConfigHandler.GetDefaultServer(config);
        if (profile == null) return false;
        var all = await CoreConfigContextBuilder.BuildAll(config, profile);
        var contexts = new[] { all.MainResult?.Context, all.PreSocksResult?.Context }.Where(c => c?.RunCoreType == type).ToArray();
        if (contexts.Length == 0) return false;
        if (!all.Success) throw new InvalidOperationException("Cannot validate the current core configuration");
        foreach (var context in contexts)
        {
            var file = Utils.GetBinConfigPath("update-check-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var generated = await CoreConfigHandler.GenerateClientConfig(context!, file);
                if (!generated.Success) throw new InvalidOperationException(generated.Msg);
                string[] args = type switch
                {
                    ECoreType.sing_box => ["check", "-c", file],
                    ECoreType.Xray => ["run", "-test", "-c", file],
                    ECoreType.mihomo => ["-t", "-f", file, "-d", Utils.GetBinPath("")],
                    _ => throw new NotSupportedException()
                };
                await RunAsync(executable, args, info, token);
            }
            finally { File.Delete(file); }
        }
        return true;
    }
}
