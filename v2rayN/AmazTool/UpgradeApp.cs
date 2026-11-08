using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ServiceLib.Services;

namespace AmazTool;

internal static class UpgradeApp
{
    private static readonly HashSet<string> UserData = new(StringComparer.OrdinalIgnoreCase)
    {
        "guiConfigs", "binConfigs", "guiLogs", "guiTemps", "guiBackups", "guiFonts", "NotStoreConfigHere.txt",
    };

    private sealed record Payload(string RelativePath, string StagedPath);
    private sealed record Replacement(string Target, string? Backup)
    {
        internal bool Installed { get; set; }
    }

    internal static int Run(string[] args)
    {
        AppUpgradeRequest? request = null;
        try
        {
            request = AppUpgradeRequest.Parse(args);
            Install(request);
            return 0;
        }
        catch (Exception ex)
        {
            if (request != null)
            {
                Log(request, ex.ToString());
                try { File.WriteAllText(request.ErrorPath, ex.Message); } catch { }
            }
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Install(AppUpgradeRequest request)
    {
        var installDirectory = Path.GetDirectoryName(request.Executable)!;
        var expectedName = OperatingSystem.IsWindows() ? "v2rayN.exe" : "v2rayN";
        if (!Path.GetFileName(request.Executable).Equals(expectedName, Utils.GetPathComparison())
            || !File.Exists(request.Executable) || Utils.IsPathUnderDirectory(installDirectory, Utils.GetExePath()))
            throw new IOException("The updater must run outside the target installation.");

        using var parent = Process.GetProcessById(request.ParentPid);
        if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks)
            throw new InvalidOperationException("The application process identity changed before update preparation.");

        var lockKey = OperatingSystem.IsLinux() ? installDirectory : installDirectory.ToUpperInvariant();
        using var mutex = new Mutex(false, "v2rayn-upgrade-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockKey))));
        if (!mutex.WaitOne(0)) throw new IOException("Another update is already running for this installation.");
        var createdWorkspace = false;
        var retainBackup = false;
        try
        {
            CheckLink(installDirectory);
            CheckLink(request.Workspace);
            if (Directory.Exists(request.Workspace) || File.Exists(request.Workspace))
                throw new IOException("The upgrade workspace already exists.");
            Directory.CreateDirectory(request.Workspace);
            createdWorkspace = true;
            Log(request, "Preparing update.");
            var files = Stage(request, installDirectory, expectedName);
            Log(request, "Prepared update. Waiting for the application to exit.");
            using (File.Create(request.ReadyPath)) { }
            if (!parent.WaitForExit(60_000)) throw new TimeoutException("The application did not exit within 60 seconds.");
            if (!File.Exists(request.CommitPath)) throw new IOException("The application did not authorize the prepared update.");

            var replacements = new List<Replacement>();
            var createdDirectories = new List<string>();
            try
            {
                Log(request, "Replacing application files.");
                ReplaceFiles(files, installDirectory, request.Workspace, replacements, createdDirectories);
                Log(request, "Starting the updated application.");
                Utils.StartApplication(request.Executable);
            }
            catch (RestartUnconfirmedException)
            {
                // Launch Services may already have started the new executable.
                // Retain both versions until its status can be established.
                retainBackup = true;
                throw;
            }
            catch (Exception ex)
            {
                Log(request, "Update failed. Restoring previous files. " + ex.Message);
                retainBackup = !Restore(request, replacements, createdDirectories);
                if (!retainBackup)
                {
                    TryDelete(request, request.PendingRecord);
                    try { Utils.StartApplication(request.Executable); }
                    catch (Exception restart) { Log(request, "Could not restart the previous version. " + restart.Message); }
                }
                throw;
            }
            Log(request, "Launch requested. The application will confirm the installed executable.");
            TryDelete(request, request.Archive);
        }
        finally
        {
            if (retainBackup) Log(request, "Backup retained at " + request.Workspace);
            if (createdWorkspace && !retainBackup)
            {
                try { Directory.Delete(request.Workspace, true); }
                catch (Exception ex) { Log(request, "Could not remove update staging. " + ex.Message); }
            }
            mutex.ReleaseMutex();
        }
    }

    private static IReadOnlyList<Payload> Stage(AppUpgradeRequest request, string installDirectory, string expectedName)
    {
        using var archive = ZipFile.OpenRead(request.Archive);
        var comparison = Utils.GetPathComparison();
        var seen = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var files = new List<Payload>();
        string? archiveRoot = null;
        string? application = null;
        var payloadRoot = Path.Combine(request.Workspace, "payload");
        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.TrimEnd('/').Split('/');
            if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains('\\') || p.Contains(':')
                || p.EndsWith(' ') || p.EndsWith('.')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Unsafe update archive entry: " + entry.FullName);
            archiveRoot ??= parts[0];
            if (!archiveRoot.Equals(parts[0], StringComparison.Ordinal))
                throw new InvalidDataException("The update archive must have a single root directory.");
            if (entry.FullName.EndsWith('/')) continue;
            if (parts.Length < 2) throw new InvalidDataException("The update archive must contain an application directory.");
            if (UserData.Contains(parts[1])) continue;
            if (parts[1].StartsWith(".v2rayn-upgrade-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The archive targets an upgrade workspace.");
            var relative = Path.Combine(parts[1..]);
            if (!seen.Add(relative)) throw new InvalidDataException("Duplicate update path: " + relative);
            _ = Target(installDirectory, relative);
            var staged = Path.Combine(payloadRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            entry.ExtractToFile(staged);
            if (!OperatingSystem.IsWindows())
            {
                var mode = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1FF);
                if (mode != 0) File.SetUnixFileMode(staged, mode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (relative == AppUpgradeRequest.HelperName)
                    File.SetUnixFileMode(staged, File.GetUnixFileMode(staged) | UnixFileMode.UserExecute);
            }
            files.Add(new(relative, staged));
            if (relative.Equals(expectedName, comparison)) application = staged;
        }
        if (application == null || !Digest(application).Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The staged application does not match the downloaded executable.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(application, File.GetUnixFileMode(application) | UnixFileMode.UserExecute);
        return files;
    }

    private static void ReplaceFiles(IReadOnlyList<Payload> files, string directory, string workspace,
        List<Replacement> replacements, List<string> createdDirectories)
    {
        foreach (var file in files)
        {
            var target = Target(directory, file.RelativePath);
            if (file.RelativePath.StartsWith("bin" + Path.DirectorySeparatorChar, Utils.GetPathComparison()) && File.Exists(target))
                continue;
            CreateParents(Path.GetDirectoryName(target)!, directory, createdDirectories);
            string? backup = null;
            if (File.Exists(target))
            {
                backup = Path.Combine(workspace, "backup", file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(target, backup);
            }
            var replacement = new Replacement(target, backup);
            replacements.Add(replacement);
            File.Move(file.StagedPath, target);
            replacement.Installed = true;
        }
    }

    private static void CreateParents(string directory, string root, List<string> created)
    {
        if (directory == root || Directory.Exists(directory)) return;
        CreateParents(Path.GetDirectoryName(directory)!, root, created);
        Directory.CreateDirectory(directory);
        created.Add(directory);
    }

    private static bool Restore(AppUpgradeRequest request, List<Replacement> replacements, List<string> directories)
    {
        var restored = true;
        foreach (var file in replacements.AsEnumerable().Reverse())
        {
            try
            {
                if (file.Installed) File.Delete(file.Target);
                if (file.Backup != null) File.Move(file.Backup, file.Target);
            }
            catch (Exception ex)
            {
                restored = false;
                Log(request, "Rollback failed for " + file.Target + ". " + ex.Message);
            }
        }
        foreach (var directory in directories.AsEnumerable().Reverse())
        {
            try { Directory.Delete(directory); }
            catch (Exception ex) { Log(request, "Could not remove staged directory. " + ex.Message); }
        }
        return restored;
    }

    private static string Target(string root, string relative)
    {
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!Utils.IsPathUnderDirectory(root, target)) throw new InvalidDataException("Update path is outside the installation.");
        for (var path = target; !path.Equals(root, Utils.GetPathComparison()); path = Path.GetDirectoryName(path)!)
            CheckLink(path);
        return target;
    }

    private static void CheckLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cannot replace a symbolic link: " + path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(AppUpgradeRequest request, string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Log(request, "Could not delete " + path + ". " + ex.Message); }
    }

    private static void Log(AppUpgradeRequest request, string message)
    {
        try { File.AppendAllText(request.LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
    }
}
