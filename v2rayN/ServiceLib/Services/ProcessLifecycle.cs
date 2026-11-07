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
    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    internal static string NormalizeStart(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static async Task<IReadOnlyList<ProcessSnapshot>> SnapshotAsync(CancellationToken token)
    {
        var result = await Cli.Wrap("/bin/ps").WithArguments(["-axo", "pid=,ppid=,lstart=,stat=,comm="])
            .WithEnvironmentVariables(new Dictionary<string, string?> { ["LC_ALL"] = "C" })
            .ExecuteBufferedAsync(token);
        var rows = new List<ProcessSnapshot>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = Regex.Split(line.Trim(), @"\s+", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (fields.Length < 9 || !int.TryParse(fields[0], out var pid) || !int.TryParse(fields[1], out var parent))
                throw new IOException("无法解析进程列表 / Unable to read process identities.");
            rows.Add(new(new(pid, string.Join(" ", fields.Skip(2).Take(5)), string.Join(" ", fields.Skip(8))), parent, fields[7].StartsWith('Z')));
        }
        if (rows.Count == 0) throw new IOException("进程列表为空 / Process enumeration failed.");
        return rows;
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
