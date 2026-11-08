using System.Globalization;

namespace ServiceLib.Services;

internal sealed record AppUpgradeRequest(string Archive, string Executable, int ParentPid, long ParentStartTicks,
    string StatusDirectory, string LogPath, string PendingRecord, string ExpectedSha256)
{
    internal const string HelperName = "AmazToolV2";
    internal const string Command = "upgrade-v2";

    internal string ReadyPath => Path.Combine(StatusDirectory, "ready");
    internal string CommitPath => Path.Combine(StatusDirectory, "commit");
    internal string ErrorPath => Path.Combine(StatusDirectory, "error.txt");
    internal string Workspace => Path.Combine(Path.GetDirectoryName(Executable)!, "." + Path.GetFileName(StatusDirectory));

    internal string[] Arguments() =>
    [
        Command, Archive, Executable, ParentPid.ToString(CultureInfo.InvariantCulture),
        ParentStartTicks.ToString(CultureInfo.InvariantCulture), StatusDirectory, LogPath, PendingRecord, ExpectedSha256
    ];

    internal static AppUpgradeRequest Parse(string[] args)
    {
        if (args.Length != 9 || args[0] != Command
            || !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 1
            || !long.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var birth) || birth <= 0
            || args[8].Length != 64 || !args[8].All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid application upgrade request.");
        foreach (var index in new[] { 1, 2, 5, 6, 7 })
            if (!Path.IsPathFullyQualified(args[index]))
                throw new ArgumentException("Upgrade paths must be absolute.");
        if (!Path.GetFileName(args[5]).StartsWith("v2rayn-upgrade-", StringComparison.Ordinal))
            throw new ArgumentException("Invalid upgrade status directory.");
        return new(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), pid, birth,
            Path.GetFullPath(args[5]), Path.GetFullPath(args[6]), Path.GetFullPath(args[7]), args[8]);
    }
}
