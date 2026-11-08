using System.Diagnostics;

namespace AmazTool;

internal class Utils
{
    public static string GetExePath()
    {
        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    }

    public static string StartupPath()
    {
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    public static string GetPath(string fileName)
    {
        var startupPath = StartupPath();
        if (string.IsNullOrEmpty(fileName))
        {
            return startupPath;
        }
        return Path.Combine(startupPath, fileName);
    }

    public static bool IsPathUnderDirectory(string baseDir, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        var baseFull = Path.GetFullPath(baseDir);
        var targetFull = Path.GetFullPath(targetPath);

        baseFull = baseFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return targetFull.StartsWith(baseFull, GetPathComparison());
    }

    public static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public static void StartV2RayN()
        => StartApplication(GetPath(OperatingSystem.IsWindows() ? "v2rayN.exe" : "v2rayN"));

    internal static void StartApplication(string executable)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!);
        var bundle = OperatingSystem.IsMacOS() && directory.Name == "MacOS" && directory.Parent?.Name == "Contents"
            && directory.Parent.Parent is { Extension: ".app" } app ? app : null;
        var start = new ProcessStartInfo(bundle == null ? executable : "/usr/bin/open")
        {
            UseShellExecute = false,
            WorkingDirectory = directory.FullName
        };
        if (bundle != null)
        {
            start.ArgumentList.Add("-n");
            start.ArgumentList.Add(bundle.FullName);
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + executable);
        if (bundle == null) return;
        if (!process.WaitForExit(10_000))
            throw new RestartUnconfirmedException("Launch Services did not confirm the start request within 10 seconds.");
        if (process.ExitCode != 0) throw new IOException($"Launch Services failed to start {bundle.FullName} ({process.ExitCode}).");
    }
}

internal sealed class RestartUnconfirmedException(string message) : IOException(message);
