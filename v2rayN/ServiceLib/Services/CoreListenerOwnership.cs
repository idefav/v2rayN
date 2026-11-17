using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Services;

internal static class CoreListenerOwnership
{
    internal static async Task<bool> OwnsAsync(ProcessService process, IReadOnlyList<CoreListener> endpoints, CancellationToken token)
    {
        if (endpoints.Count == 0) return true;
        var owners = process.KnownProcesses.Select(p => p.Pid).ToHashSet();
        if (Utils.IsNonWindows())
        {
            var snapshot = await UnixProcessControl.SnapshotAsync(token);
            var core = process.CoreIdentity;
            if (core == null || !snapshot.Any(p => p.Identity.Pid == core.Pid && p.Identity.StartTime == core.StartTime && !p.Zombie)) return false;
            process.Remember(UnixProcessControl.Descendants(snapshot, process.KnownProcesses));
            owners = process.KnownProcesses.Where(p => snapshot.Any(s => s.Identity.Pid == p.Pid && s.Identity.StartTime == p.StartTime && !s.Zombie))
                .Select(p => p.Pid).ToHashSet();
        }
        else if (OperatingSystem.IsWindows())
        {
            var verified = WindowsProcessTree.Snapshot(process.KnownProcesses);
            process.Remember(verified);
            owners = verified.Select(p => p.Pid).ToHashSet();
        }
        if (owners.Count == 0) return false;
        var actual = new List<CoreListener>();
        foreach (var protocol in endpoints.Select(p => p.Protocol).Distinct())
        {
            if (Utils.IsNonWindows())
            {
                var args = new List<string> { "-nP", "-a", "-p", string.Join(",", owners), "-i" + (protocol == ProtocolType.Tcp ? "TCP" : "UDP"), "-Fptn" };
                if (protocol == ProtocolType.Tcp) args.AddRange(["-sTCP:LISTEN"]);
                var command = Cli.Wrap(Utils.IsMacOS() ? "/usr/sbin/lsof" : "lsof").WithArguments(args);
                if (process.IsElevated)
                    command = Cli.Wrap("/usr/bin/sudo").WithArguments(new[] { "-S", "-p", "", "--", Utils.IsMacOS() ? "/usr/sbin/lsof" : "lsof" }.Concat(args))
                        .WithStandardInputPipe(PipeSource.FromString((AppManager.Instance.LinuxSudoPwd ?? "") + "\n"));
                var result = await command.WithValidation(CommandResultValidation.None).ExecuteBufferedAsync(token);
                if (result.ExitCode is not (0 or 1)) throw new IOException("无法确认监听归属 / Cannot inspect core listeners: lsof exit " + result.ExitCode);
                actual.AddRange(ParseLsof(result.StandardOutput, protocol, owners));
            }
            else
            {
                // -p tcp/udp omits the corresponding IPv6 table on Windows.
                var result = await Cli.Wrap("netstat").WithArguments(["-ano"])
                    .ExecuteBufferedAsync(token);
                actual.AddRange(ParseNetstat(result.StandardOutput, owners));
                break;
            }
        }
        return endpoints.All(expected => actual.Any(a => Covers(a, expected)));
    }

    internal static bool Covers(CoreListener owned, CoreListener expected)
    {
        if (owned.Port != expected.Port || owned.Protocol != expected.Protocol) return false;
        var address = owned.Address.IsIPv4MappedToIPv6 ? owned.Address.MapToIPv4() : owned.Address;
        var target = expected.Address.IsIPv4MappedToIPv6 ? expected.Address.MapToIPv4() : expected.Address;
        // A listener on one specific address does not establish ownership of a wildcard.
        // Reachability is checked separately for dual-stack sockets.
        return address.Equals(target) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Any) && target.AddressFamily == AddressFamily.InterNetwork;
    }

    internal static IReadOnlyList<CoreListener> ParseLsof(string output, ProtocolType protocol, IReadOnlySet<int> owners)
    {
        var result = new List<CoreListener>();
        var owned = false; var ipv6 = false;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line[0] == 'p') owned = int.TryParse(line.AsSpan(1), out var pid) && owners.Contains(pid);
            else if (line[0] == 't') ipv6 = line[1..] == "IPv6";
            else if (line[0] == 'n' && owned)
            {
                var address = line[1..];
                if (address.Contains("->", StringComparison.Ordinal)) continue;
                if (address.StartsWith("*:")) address = (ipv6 ? "[::]" : "0.0.0.0") + address[1..];
                if (IPEndPoint.TryParse(address, out var endpoint)) result.Add(new(endpoint.Address, endpoint.Port, protocol));
            }
        }
        return result;
    }

    internal static IReadOnlyList<CoreListener> ParseNetstat(string output, IReadOnlySet<int> owners)
    {
        var result = new List<CoreListener>();
        foreach (var line in output.Split('\n'))
        {
            var f = Regex.Split(line.Trim(), @"\s+");
            if (f.Length < 4 || !int.TryParse(f[^1], out var pid) || !owners.Contains(pid)
                || !IPEndPoint.TryParse(f[1], out var endpoint)) continue;
            if (f[0] == "TCP" && f.Length >= 5 && f[3] == "LISTENING") result.Add(new(endpoint.Address, endpoint.Port, ProtocolType.Tcp));
            if (f[0] == "UDP") result.Add(new(endpoint.Address, endpoint.Port, ProtocolType.Udp));
        }
        return result;
    }
}
