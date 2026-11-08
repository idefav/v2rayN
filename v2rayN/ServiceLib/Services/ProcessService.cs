namespace ServiceLib.Services;

public class ProcessService : IDisposable
{
    private readonly Process _process;
    private readonly Func<bool, string, Task>? _updateFunc;
    private readonly ConcurrentDictionary<int, ProcessIdentity> _identities = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, ProcessService> FailedStops = new();
    private readonly TaskCompletionSource<ProcessIdentity> _coreIdentityReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _recentErrors = new();
    private bool _isDisposed;
    private bool _started;
    private bool _stopConfirmed;
    private string? _stopScriptPath;
    internal string? TemporaryConfigPath { get; set; }
    internal string? LaunchScriptPath { get; set; }
    internal bool IsElevated { get; set; }
    internal string? IdentityMarker { get; set; }
    internal string ExpectedExecutable { get; set; }
    internal string? ConfigPath { get; set; }
    internal ProcessIdentity? CoreIdentity { get; private set; }
    internal bool IsReady { get; set; }
    internal bool StartupReported { get; private set; }
    public string LaunchId { get; } = Guid.NewGuid().ToString("N");
    internal IReadOnlyList<ProcessIdentity> KnownProcesses => _identities.Values.ToArray();
    internal static bool HasUnstoppedConfig(string path) => FailedStops.Values.Any(p => p.ConfigPath == path || p.TemporaryConfigPath == path);
    internal string RecentError => string.Join(Environment.NewLine, _recentErrors);
    public int Id => _process.Id;
    public IntPtr Handle => _process.Handle;
    public bool HasExited => HasExitedAsync(CancellationToken.None).GetAwaiter().GetResult();

    internal async Task<bool> HasExitedAsync(CancellationToken token)
    {
        if (!_started || _isDisposed || _stopConfirmed) return true;
        var identity = CoreIdentity;
        if (!IsElevated || identity == null) return _process.HasExited;
        if (OperatingSystem.IsLinux())
        {
            var current = UnixProcessControl.ReadLinuxProcess(identity.Pid, ExpectedExecutable);
            return current == null || current.Zombie || current.Identity.StartTime != identity.StartTime;
        }
        if (!OperatingSystem.IsMacOS()) return _process.HasExited;
        var row = await UnixProcessControl.ReadMacProcessAsync(identity.Pid, token).ConfigureAwait(false);
        if (row == null || row.Zombie) return true;
        if (row.Identity.StartTime != identity.StartTime)
            throw new InvalidOperationException($"内核进程身份已变化 / Core process identity changed: PID {identity.Pid}.");
        return false;
    }

    public ProcessService(string fileName, string arguments, string workingDirectory, bool displayLog,
        bool redirectInput, Dictionary<string, string>? environmentVars, Func<bool, string, Task>? updateFunc)
    {
        _updateFunc = updateFunc;
        ExpectedExecutable = fileName;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName, Arguments = arguments, WorkingDirectory = workingDirectory,
                UseShellExecute = false, RedirectStandardInput = redirectInput,
                RedirectStandardOutput = displayLog, RedirectStandardError = displayLog, CreateNoWindow = true,
                StandardOutputEncoding = displayLog ? Encoding.UTF8 : null,
                StandardErrorEncoding = displayLog ? Encoding.UTF8 : null,
            },
            EnableRaisingEvents = true
        };
        if (environmentVars != null)
            foreach (var kv in environmentVars) _process.StartInfo.Environment[kv.Key] = kv.Value;
        if (displayLog)
        {
            _process.OutputDataReceived += (_, e) => ReceiveLine(e.Data, false);
            _process.ErrorDataReceived += (_, e) => ReceiveLine(e.Data, true);
        }
    }

    internal void SetArguments(IEnumerable<string> arguments)
    {
        _process.StartInfo.Arguments = "";
        foreach (var arg in arguments) _process.StartInfo.ArgumentList.Add(arg);
    }

    internal void Remember(IEnumerable<ProcessIdentity> identities)
    {
        foreach (var identity in identities)
            _identities.TryAdd(identity.Pid, identity);
    }

    public async Task StartAsync(string? pwd = null)
    {
        _process.Start();
        _started = true;
        // An unverified owner still prevents cleanup failures from losing its PID.
        // Unix startup replaces this provisional record with one native identity;
        // subsequent snapshots must never overwrite it after a PID has been reused.
        var initialIdentity = new ProcessIdentity(Id, Utils.IsNonWindows()
            ? "unverified:" + LaunchId : _process.StartTime.ToUniversalTime().Ticks.ToString(), ExpectedExecutable);
        Remember([initialIdentity]);
        if (!IsElevated) CoreIdentity = initialIdentity;
        if (_process.StartInfo.RedirectStandardOutput)
        {
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        // Capture the wrapper independently of the elevated child. A later wrapper exit
        // must never erase the child's identity.
        if (Utils.IsNonWindows())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var row = (await UnixProcessControl.SnapshotAsync(timeout.Token)).FirstOrDefault(p => p.Identity.Pid == Id);
            if (!_process.HasExited)
            {
                if (row == null) throw new IOException("无法获取启动进程身份 / Unable to identify the started process.");
                _identities[Id] = row.Identity;
                if (!IsElevated) CoreIdentity = row.Identity;
            }
        }
        else
        {
            CoreIdentity = new(Id, _process.StartTime.ToUniversalTime().Ticks.ToString(), ExpectedExecutable);
            Remember([CoreIdentity]);
        }
        if (_process.StartInfo.RedirectStandardInput)
        {
            await _process.StandardInput.WriteLineAsync(pwd);
            _process.StandardInput.Close(); // A rejected password must not leave sudo waiting forever.
        }
        Logging.SaveLog($"CoreLifecycle launch={LaunchId} wrapper={Id} elevated={IsElevated} config={ConfigPath}");
    }

    internal async Task WaitForIdentityAsync(CancellationToken token)
    {
        if (!IsElevated) return;
        while (!_coreIdentityReady.Task.IsCompleted)
        {
            token.ThrowIfCancellationRequested();
            if (_process.HasExited) throw new InvalidOperationException("提权启动失败 / Elevated core launch failed. " + RecentError);
            await Task.WhenAny(_coreIdentityReady.Task, Task.Delay(50, token));
        }
        await _coreIdentityReady.Task;
    }

    internal Task WaitForWrapperExitAsync(CancellationToken token) => _started ? _process.WaitForExitAsync(token) : Task.CompletedTask;

    private void ReceiveLine(string? line, bool error)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (IdentityMarker != null && line.StartsWith(IdentityMarker + "\t", StringComparison.Ordinal))
        {
            var fields = line.Split('\t', 3);
            if (fields.Length == 3 && int.TryParse(fields[1], out var pid) && pid > 1 && fields[2].Length > 0)
            {
                CoreIdentity = new(pid, UnixProcessControl.NormalizeStart(fields[2]), ExpectedExecutable);
                Remember([CoreIdentity]);
                _coreIdentityReady.TrySetResult(CoreIdentity);
            }
            return;
        }
        if (line.Contains("sing-box started (", StringComparison.Ordinal)) StartupReported = true;
        if (error)
        {
            _recentErrors.Enqueue(line.Length > 1000 ? line[..1000] : line);
            while (_recentErrors.Count > 6) _recentErrors.TryDequeue(out _);
        }
        _ = _updateFunc?.Invoke(false, line + Environment.NewLine);
    }

    internal async Task<string> GetStopScriptAsync()
    {
        if (_stopScriptPath != null) return _stopScriptPath;
        var path = Path.Combine(Path.GetTempPath(), $"v2rayn-stop-{LaunchId}.sh");
        await File.WriteAllTextAsync(path, EmbedUtils.GetEmbedText(Utils.IsMacOS()
            ? Global.KillAsSudoOSXShellFileName : Global.KillAsSudoLinuxShellFileName));
        if (Utils.IsNonWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return _stopScriptPath = path;
    }

    public async Task<ProcessStopResult> StopAndWaitAsync()
    {
        await _stopGate.WaitAsync();
        try
        {
            if (_stopConfirmed || !_started) return ProcessStopResult.Stopped;
            var elapsed = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ProcessStopResult result;
            if (Utils.IsNonWindows())
                result = await UnixProcessControl.StopAsync(this, IsElevated ? AppManager.Instance.LinuxSudoPwd : null, timeout.Token);
            else if (OperatingSystem.IsWindows())
                result = await StopWindowsAsync(timeout.Token);
            else result = new(ProcessStopStatus.Failed, KnownProcesses, "Unsupported process platform.");
            _stopConfirmed = result.Success;
            if (result.Success) { FailedStops.TryRemove(LaunchId, out _); IsReady = false; }
            else FailedStops[LaunchId] = this;
            Logging.SaveLog($"CoreLifecycle launch={LaunchId} stop={result.Status} elapsedMs={elapsed.ElapsedMilliseconds} remaining={string.Join(",", result.RemainingProcesses.Select(p => p.Pid))} error={result.Error}");
            return result;
        }
        finally { _stopGate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private async Task<ProcessStopResult> StopWindowsAsync(CancellationToken token)
    {
        try
        {
            Remember(WindowsProcessTree.Snapshot(KnownProcesses));
            foreach (var identity in KnownProcesses.Reverse())
            {
                var current = WindowsProcessTree.Read(identity.Pid);
                if (current == null) continue;
                if (current.StartTime != identity.StartTime)
                    return new(ProcessStopStatus.IdentityMismatch, KnownProcesses, "Process identity changed.");
                try
                {
                    using var process = Process.GetProcessById(identity.Pid);
                    if (!process.HasExited) process.Kill(true);
                }
                catch (ArgumentException) { } // Exited after the identity check.
                catch (InvalidOperationException) when (WindowsProcessTree.Read(identity.Pid) == null) { }
            }
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = WindowsProcessTree.Snapshot(KnownProcesses);
                if (remaining.Count == 0) break;
                Remember(remaining);
                await Task.Delay(50, token);
            }
            await _process.WaitForExitAsync(token);
            return ProcessStopResult.Stopped;
        }
        catch (OperationCanceledException) { return new(ProcessStopStatus.TimedOut, KnownProcesses, "Core stop timed out."); }
        catch (Win32Exception ex) { return new(ProcessStopStatus.PermissionDenied, KnownProcesses, ex.Message); }
        catch (InvalidOperationException ex) { return new(ProcessStopStatus.IdentityMismatch, KnownProcesses, ex.Message); }
        catch (Exception ex) { return new(ProcessStopStatus.Failed, KnownProcesses, ex.Message); }
    }

    public async Task StopAsync()
    {
        var result = await StopAndWaitAsync();
        if (!result.Success && _updateFunc != null) await _updateFunc(true, result.Error ?? "Core stop failed.");
    }

    internal static async Task RetryFailedStopsAsync(IEnumerable<int> excludedPids)
    {
        var excluded = excludedPids.ToHashSet();
        await Task.WhenAll(FailedStops.Values.Where(p => !p.KnownProcesses.Any(i => excluded.Contains(i.Pid))).Select(async process =>
        {
            if ((await process.StopAndWaitAsync()).Success) process.Dispose();
        }));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        // Disposal is not proof of termination. In particular it must never forget a
        // root-owned process after a failed sudo command.
        if (_started && !_stopConfirmed)
        {
            FailedStops[LaunchId] = this;
            return;
        }
        _process.Dispose();
        _isDisposed = true;
        foreach (var path in new[] { TemporaryConfigPath, LaunchScriptPath, _stopScriptPath })
            if (path != null) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }
}
