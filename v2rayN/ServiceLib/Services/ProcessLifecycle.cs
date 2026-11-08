using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Services;

public sealed record ProcessIdentity(int Pid, string StartTime, string Executable);
public enum ProcessStopStatus { Stopped, TimedOut, PermissionDenied, IdentityMismatch, Failed }
public sealed record ProcessStopResult(ProcessStopStatus Status, IReadOnlyList<ProcessIdentity> RemainingProcesses, string? Error = null)
{
    public bool Success => Status == ProcessStopStatus.Stopped;
    public static ProcessStopResult Stopped { get; } = new(ProcessStopStatus.Stopped, []);
    internal static ProcessStopResult Combine(IEnumerable<ProcessStopResult> results)
    {
        var failures = results.Where(r => !r.Success).ToArray();
        return failures.Length == 0 ? Stopped : new(failures[0].Status,
            failures.SelectMany(r => r.RemainingProcesses).Distinct().ToArray(),
            string.Join(Environment.NewLine, failures.Select(r => r.Error)));
    }
}

internal sealed record ProcessSnapshot(ProcessIdentity Identity, int ParentPid, bool Zombie);

internal static class UnixProcessControl
{
    private static readonly Dictionary<string, string?> PsEnvironment = new() { ["LC_ALL"] = "C", ["TZ"] = "UTC" };
    private const string PsColumns = "pid=,ppid=,lstart=,stat=,comm=";

    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    internal static string NormalizeStart(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Keep Linux identities in the kernel's native clock. Converting starttime to
    // wall time through .NET and ps can differ by a second (and after clock changes).
    internal static ProcessSnapshot? ReadLinuxProcess(int pid, string executable)
    {
        try { return ParseLinuxStat(pid, File.ReadAllText($"/proc/{pid}/stat"), executable); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static ProcessSnapshot ParseLinuxStat(int pid, string stat, string executable)
    {
        // comm is parenthesized and may itself contain spaces or closing parentheses.
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open < 1 || close <= open || !int.TryParse(stat.AsSpan(0, open).Trim(), out var actualPid) || actualPid != pid)
            throw new IOException($"Invalid Linux process identity: PID {pid}");
        var fields = stat[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || fields[0].Length != 1 || !int.TryParse(fields[1], out var parent)
            || !ulong.TryParse(fields[19], out _))
            throw new IOException($"Invalid Linux process starttime: PID {pid}");
        return new(new(pid, "linux:" + fields[19], executable), parent, fields[0] == "Z");
    }

    internal const string LinuxStartTimeShell = """
        IFS= read -r stat < /proc/$$/stat || exit 23
        fields=${stat##*) }
        read -r -a fields <<< "$fields"
        [[ "${fields[19]}" =~ ^[0-9]+$ ]] || exit 23
        birth="linux:${fields[19]}"
        """;

    internal static async Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(CancellationToken token)
    {
        var result = await Cli.Wrap("/bin/ps").WithArguments(["-axo", PsColumns])
            .WithEnvironmentVariables(PsEnvironment)
            .ExecuteBufferedAsync(token);
        var rows = new List<ProcessSnapshot>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var row = ParsePsRow(line);
            if (OperatingSystem.IsLinux())
            {
                // A process may exit after ps enumerates it. Other read/parse errors
                // remain failures; never silently treat a permission error as exit.
                var native = ReadLinuxProcess(row.Identity.Pid, row.Identity.Executable);
                if (native != null) rows.Add(native);
            }
            else rows.Add(row);
        }
        if (rows.Count == 0) throw new IOException("进程列表为空 / Process enumeration failed.");
        return rows;
    }

    // The BSD task-info call used by Process.StartTime is restricted across UIDs.
    // ps can read the same root process identity as the launch and stop scripts.
    internal static async Task<ProcessSnapshot?> ReadMacProcessAsync(int pid, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            var result = await Cli.Wrap("/bin/ps").WithArguments(["-p", pid.ToString(), "-o", PsColumns])
                .WithEnvironmentVariables(PsEnvironment).WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeout.Token).ConfigureAwait(false);
            return ParseMacProcessResult(pid, result.ExitCode, result.StandardOutput, result.StandardError);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"进程身份查询超时 / Process identity query timed out: PID {pid}.", ex);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            throw new InvalidOperationException($"无法核验进程身份 / Cannot verify process identity: PID {pid}. {ex.Message}", ex);
        }
    }

    internal static ProcessSnapshot? ParseMacProcessResult(int pid, int exitCode, string output, string error)
    {
        if (exitCode == 1 && string.IsNullOrWhiteSpace(output) && string.IsNullOrWhiteSpace(error))
            return null; // BSD ps found no selected process.
        if (exitCode != 0 || !string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"无法核验进程身份 / Cannot verify process identity: PID {pid}. {error.Trim()}");
        var row = ParsePsRow(output.Trim());
        if (output.Trim().Contains('\n') || row.Identity.Pid != pid)
            throw new InvalidOperationException($"进程查询返回不匹配的 PID / Unexpected process identity: PID {pid}.");
        return row;
    }

    private static ProcessSnapshot ParsePsRow(string line)
    {
        var fields = Regex.Split(line.Trim(), @"\s+", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (fields.Length < 9 || !int.TryParse(fields[0], out var pid) || !int.TryParse(fields[1], out var parent))
            throw new IOException("无法解析进程列表 / Unable to read process identities.");
        return new(new(pid, string.Join(" ", fields.Skip(2).Take(5)), string.Join(" ", fields.Skip(8))),
            parent, fields[7].StartsWith('Z'));
    }

    internal static IReadOnlyList<ProcessIdentity> Descendants(IReadOnlyList<ProcessSnapshot> snapshot, IEnumerable<ProcessIdentity> roots)
    {
        var owned = new Dictionary<int, ProcessIdentity>();
        foreach (var root in roots)
        {
            var row = snapshot.FirstOrDefault(p => p.Identity.Pid == root.Pid);
            if (row == null || row.Zombie) continue;
            if (row.Identity.StartTime != root.StartTime)
                throw new InvalidOperationException($"进程身份已变化，未发送终止信号 / Process identity changed: PID {root.Pid}");
            owned[root.Pid] = row.Identity;
        }
        bool added;
        do
        {
            added = false;
            foreach (var row in snapshot.Where(p => !p.Zombie))
                if (owned.ContainsKey(row.ParentPid) && owned.TryAdd(row.Identity.Pid, row.Identity)) added = true;
        } while (added);
        return owned.Values.ToArray();
    }

    internal static async Task<ProcessStopResult> StopAsync(ProcessService service, string? sudoPassword, CancellationToken token,
        string sudoExecutable = "/usr/bin/sudo", Func<CancellationToken, Task<IReadOnlyList<ProcessSnapshot>>>? snapshotProvider = null)
    {
        var errors = new StringBuilder();
        try
        {
            // Snapshot descendants before signalling. Keep these identities on the owner even if
            // a helper times out or its parent exits and the children are reparented to launchd/init.
            var readSnapshot = snapshotProvider ?? SnapshotAsync;
            var snapshot = await readSnapshot(token);
            var live = Descendants(snapshot, service.KnownProcesses);
            if (live.Count == 0)
            {
                // Failed sudo startup often leaves no live process. Do not demand a
                // second elevation just to acknowledge exit. Drain output first so a
                // late child-identity report cannot be lost when the wrapper exits.
                await service.WaitForWrapperExitAsync(token);
                snapshot = await readSnapshot(token);
                live = Descendants(snapshot, service.KnownProcesses);
                if (live.Count == 0) return ProcessStopResult.Stopped;
            }
            service.Remember(live);
            var script = await service.GetStopScriptAsync();
            var arguments = new List<string>();
            if (service.IsElevated) arguments.AddRange(["-S", "-p", "", "--", "/bin/bash"]);
            arguments.Add(script);
            foreach (var identity in service.KnownProcesses)
                arguments.AddRange([identity.Pid.ToString(), identity.StartTime]);

            var command = Cli.Wrap(service.IsElevated ? sudoExecutable : "/bin/bash").WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .WithStandardInputPipe(PipeSource.FromString(service.IsElevated ? (sudoPassword ?? "") + "\n" : ""))
                .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                {
                    if (line.StartsWith("STAGE\t", StringComparison.Ordinal))
                        Logging.SaveLog($"CoreLifecycle launch={service.LaunchId} stopStage={line[6..]}");
                    if (line.StartsWith("IDENTITY\t", StringComparison.Ordinal))
                    {
                        var fields = line.Split('\t', 4);
                        if (fields.Length == 4 && int.TryParse(fields[1], out var pid))
                            service.Remember([new(pid, fields[2], fields[3])]);
                    }
                }))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(errors));
            var result = await command.ExecuteAsync(token);
            if (result.ExitCode != 0)
            {
                var status = result.ExitCode switch { 20 => ProcessStopStatus.IdentityMismatch, 21 => ProcessStopStatus.PermissionDenied,
                    22 => ProcessStopStatus.TimedOut, 23 => ProcessStopStatus.Failed,
                    _ => service.IsElevated ? ProcessStopStatus.PermissionDenied : ProcessStopStatus.Failed };
                var description = service.IsElevated && status == ProcessStopStatus.PermissionDenied
                    ? "提权停止失败 / Elevated stop failed" : "停止内核失败 / Core stop failed";
                return new(status, service.KnownProcesses, $"{description} ({result.ExitCode}): {errors.ToString().Trim()}");
            }
            snapshot = await readSnapshot(token);
            var remaining = Descendants(snapshot, service.KnownProcesses);
            if (remaining.Count == 0) await service.WaitForWrapperExitAsync(token);
            return remaining.Count == 0 ? ProcessStopResult.Stopped
                : new(ProcessStopStatus.Failed, remaining, "停止后仍有所属进程存活 / Owned processes are still running.");
        }
        catch (OperationCanceledException)
        {
            return new(ProcessStopStatus.TimedOut, service.KnownProcesses, "停止内核超时，已中止重启 / Core stop timed out; reload aborted.");
        }
        catch (InvalidOperationException ex)
        {
            return new(ProcessStopStatus.IdentityMismatch, service.KnownProcesses, ex.Message);
        }
        catch (Exception ex)
        {
            return new(ProcessStopStatus.Failed, service.KnownProcesses, "无法确认内核退出 / Unable to verify core exit: " + ex.Message);
        }
    }
}
