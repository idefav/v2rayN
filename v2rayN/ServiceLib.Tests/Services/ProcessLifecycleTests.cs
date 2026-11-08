namespace ServiceLib.Tests.Services;

[NotInParallel]
public class ProcessLifecycleTests
{
    [Test]
    public void MacTargetedQueryDistinguishesExitFromQueryFailure()
    {
        var row = UnixProcessControl.ParseMacProcessResult(123, 0,
            " 123 1 Sun Nov  8 01:02:03 2026 S /tmp/内核 with spaces\n", "")!;
        Check(row.Identity.StartTime == "Sun Nov 8 01:02:03 2026", "start time is not normalized");
        Check(row.Identity.Executable == "/tmp/内核 with spaces" && !row.Zombie, "executable was truncated");
        Check(UnixProcessControl.ParseMacProcessResult(123, 1, "", "") == null, "missing PID is not exited");
        Check(UnixProcessControl.ParseMacProcessResult(123, 0,
            "123 1 Sun Nov 8 01:02:03 2026 Z /tmp/core", "")!.Zombie, "zombie was not detected");
        foreach (var result in new[] { (1, "", "permission denied"), (0, "", ""),
            (0, "124 1 Sun Nov 8 01:02:03 2026 S /tmp/core", "") })
        {
            var rejected = false;
            try { UnixProcessControl.ParseMacProcessResult(123, result.Item1, result.Item2, result.Item3); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { rejected = true; }
            Check(rejected, "invalid process information was treated as exit");
        }
    }

    [Test]
    public void LinuxIdentityUsesRawKernelTicksWithParenthesizedCommands()
    {
        var fields = new[] { "S", "42" }.Concat(Enumerable.Repeat("0", 17)).Append("123456789");
        var row = UnixProcessControl.ParseLinuxStat(123, "123 (worker ) with spaces)) " + string.Join(" ", fields), "worker");
        Check(row.Identity.StartTime == "linux:123456789" && row.ParentPid == 42 && !row.Zombie,
            "kernel identity was converted to wall time or command text shifted the fields");
        var rejected = false;
        try { UnixProcessControl.ParseLinuxStat(124, "123 (worker) " + string.Join(" ", fields), "worker"); }
        catch (IOException) { rejected = true; }
        Check(rejected, "a different PID was accepted");
        rejected = false;
        try { UnixProcessControl.ParseLinuxStat(123, "123 (worker) S 42", "worker"); }
        catch (IOException) { rejected = true; }
        Check(rejected, "truncated identity was accepted");
    }

    [Test]
    public async Task NativeIdentityReplacesProvisionalRecordAndRemainsImmutable()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix identity fixture"); return; }
        using var process = await Sleep();
        try
        {
            var identity = process.CoreIdentity!;
            Check(process.KnownProcesses.Single(p => p.Pid == process.Id) == identity,
                "wrapper kept a different provisional timestamp");
            Check(!identity.StartTime.StartsWith("unverified:"), "startup did not verify the identity");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var native = (await UnixProcessControl.SnapshotAsync(timeout.Token)).Single(p => p.Identity.Pid == process.Id);
            Check(native.Identity.StartTime == identity.StartTime, "native start time changed between reads");
            process.Remember([identity with { StartTime = "different-process" }]);
            Check(process.KnownProcesses.Single(p => p.Pid == process.Id) == identity, "later snapshot replaced an owned identity");
            process.IsElevated = true;
            Check(!process.HasExited, "live native identity was compared against a different clock");
        }
        finally
        {
            process.IsElevated = false;
            var stopped = await process.StopAndWaitAsync();
            Check(stopped.Success, stopped.Error ?? "native identity cleanup failed");
        }
    }

    [Test]
    public async Task LinuxLaunchMarkerMatchesSnapshotAndStopScript()
    {
        if (!OperatingSystem.IsLinux()) { Skip.Test("Linux launch identity fixture"); return; }
        using var process = new ProcessService("/bin/bash", "", Path.GetTempPath(), true, false, null, null)
        { IsElevated = true, IdentityMarker = "__LINUX_IDENTITY_TEST" };
        process.SetArguments(["-c", UnixProcessControl.LinuxStartTimeShell +
            "\nprintf '__LINUX_IDENTITY_TEST\\t%s\\t%s\\n' \"$$\" \"$birth\"\nexec /bin/sleep 30\n"]);
        try
        {
            await process.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForIdentityAsync(timeout.Token);
            Check(process.CoreIdentity!.StartTime.StartsWith("linux:") && !process.HasExited, "launch marker did not report kernel identity");
            Check(process.CoreIdentity.StartTime == process.KnownProcesses.Single(p => p.Pid == process.Id).StartTime,
                "launch marker and snapshot use different clocks");
        }
        finally
        {
            process.IsElevated = false;
            var stopped = await process.StopAndWaitAsync();
            Check(stopped.Success, stopped.Error ?? "launch identity cleanup failed");
        }
    }

    [Test]
    [Arguments("parent-exits")]
    [Arguments("ignore")]
    [Arguments("slow")]
    [Arguments("already-orphaned")]
    public async Task StopsRealTreeEvenAfterParentExitAndReleasesListener(string mode)
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix process tree fixture"); return; }
        var ready = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new ProcessService(Environment.GetEnvironmentVariable("SSH_TEST_PYTHON") ?? "python3", "",
            AppContext.BaseDirectory, true, false, null, (_, line) =>
            {
                if (line.StartsWith("FIXTURE ")) ready.TrySetResult(JsonNode.Parse(line[8..])!);
                return Task.CompletedTask;
            });
        process.SetArguments([Path.Combine(AppContext.BaseDirectory, "Fixtures", "process_tree.py"), mode]);
        int child = 0;
        try
        {
            await process.StartAsync();
            var fixture = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            child = fixture["child"]!.GetValue<int>();
            var port = fixture["port"]!.GetValue<int>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            process.Remember(UnixProcessControl.Descendants(await UnixProcessControl.SnapshotAsync(timeout.Token), process.KnownProcesses));
            if (mode == "already-orphaned")
            {
                using var parent = Process.GetProcessById(process.Id);
                parent.Kill(); await parent.WaitForExitAsync();
            }
            var elapsed = Stopwatch.StartNew();
            var result = await process.StopAndWaitAsync();
            Check(result.Success, result.Error ?? "stop failed");
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(11), "stop exceeded deadline");
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Check((await process.StopAndWaitAsync()).Success, "stop was not idempotent");
            Check(process.KnownProcesses.Any(p => p.Pid == child), "child identity was lost");
        }
        finally
        {
            if (child > 0) KillFixture(child);
            try { await process.StopAndWaitAsync(); } catch { }
        }
    }

    [Test]
    public async Task BoundSocketAbortsReloadBeforeConfigReplacementOrProcessLaunch()
    {
        EnsureIsolatedStorage();
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        reserved.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        var port = ((IPEndPoint)reserved.LocalEndPoint!).Port;
        var attempts = 0;
        var manager = new CoreManager(_ => { attempts++; return ("missing-test-core", ""); });
        var config = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        var before = File.Exists(config) ? await File.ReadAllBytesAsync(config) : null;
        var source = Path.Combine(Path.GetTempPath(), "bound-core-" + Guid.NewGuid() + ".json");
        const string sentinel = "{\"old\":true}";
        try
        {
            await File.WriteAllTextAsync(config, sentinel);
            await File.WriteAllTextAsync(source, $$"""{"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":{{port}}}],"outbounds":[{"type":"direct"}]}""");
            var result = await manager.LoadCore(new CoreConfigContext
            {
                Node = new ProfileItem { ConfigType = EConfigType.Custom, CoreType = ECoreType.sing_box, Address = source },
                RunCoreType = ECoreType.sing_box, AppConfig = new Config(),
            }, null);
            Check(result == CoreLoadResult.Failed && attempts == 0, "reload launched a core on a reserved port");
            Check(await File.ReadAllTextAsync(config) == sentinel, "failed bind replaced the previous config");
            Check(manager.LastError?.Contains("Cannot bind listener") == true, "missing bind diagnostic");
            reserved.Listen(1);
            Check(reserved.IsBound, "reload altered an unknown socket");
        }
        finally
        {
            if (before == null) File.Delete(config); else await File.WriteAllBytesAsync(config, before);
            File.Delete(source);
        }
    }

    [Test]
    public async Task FailedStopPreservesOwnerAndConfigAndStillStopsOtherCore()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix identity fault fixture"); return; }
        EnsureIsolatedStorage();
        using var main = await Sleep();
        using var pre = await Sleep();
        var launchAttempts = 0;
        var manager = new CoreManager(_ => { launchAttempts++; return ("/bin/false", ""); });
        Field("_processService").SetValue(manager, main);
        Field("_processPreService").SetValue(manager, pre);
        var identities = (ConcurrentDictionary<int, ProcessIdentity>)typeof(ProcessService)
            .GetField("_identities", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var original = identities[main.Id];
        identities[main.Id] = original with { StartTime = "wrong-birth-time" };
        var config = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        var before = File.Exists(config) ? await File.ReadAllBytesAsync(config) : null;
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        const string sentinel = "{\"old\":true}";
        try
        {
            await File.WriteAllTextAsync(config, sentinel);
            await File.WriteAllTextAsync(source, """{"inbounds":[],"outbounds":[{"type":"direct"}]}""");
            var result = await manager.LoadCore(new CoreConfigContext
            {
                Node = new ProfileItem { ConfigType = EConfigType.Custom, CoreType = ECoreType.sing_box, Address = source },
                RunCoreType = ECoreType.sing_box, AppConfig = new Config(),
            }, null);
            Check(result == CoreLoadResult.Failed, "reload continued after failed stop");
            Check(launchAttempts == 0, "new launch was attempted after failed stop");
            Check(await File.ReadAllTextAsync(config) == sentinel, "running config was overwritten");
            Check(ReferenceEquals(Field("_processService").GetValue(manager), main), "failed owner was forgotten");
            Check(Field("_processPreService").GetValue(manager) == null, "other core was not stopped");
            Check(!main.HasExited, "identity mismatch killed an unverified process");
            Check(manager.LastError?.Contains("identity", StringComparison.OrdinalIgnoreCase) == true, "missing stop error");
            main.Dispose();
            Check(!main.HasExited, "Dispose silently terminated or forgot a live process");
        }
        finally
        {
            identities[main.Id] = original;
            Check((await manager.CoreStop()).Success, "retry did not stop retained owner");
            if (before == null) File.Delete(config); else await File.WriteAllBytesAsync(config, before);
            File.Delete(source);
        }
    }

    [Test]
    public async Task HelperTimeoutRetainsIdentitiesForRetry()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix helper timeout fixture"); return; }
        using var process = await Sleep();
        var helper = Path.Combine(Path.GetTempPath(), "stalled-elevation-" + Guid.NewGuid() + ".sh");
        await File.WriteAllTextAsync(helper, "#!/bin/bash\nexec /bin/sleep 30\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        process.IsElevated = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var result = await UnixProcessControl.StopAsync(process, null, timeout.Token, helper);
            Check(result.Status == ProcessStopStatus.TimedOut && result.RemainingProcesses.Count > 0, "timeout lost identity");
            Check(!process.HasExited, "cancelled stop killed fixture");
        }
        finally
        {
            process.IsElevated = false;
            Check((await process.StopAndWaitAsync()).Success, "retry failed");
            File.Delete(helper);
        }
    }

    [Test]
    public async Task EnumerationFailureNeverSignalsTheProcess()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix enumeration fixture"); return; }
        using var process = await Sleep();
        try
        {
            var result = await UnixProcessControl.StopAsync(process, null, CancellationToken.None,
                snapshotProvider: _ => throw new IOException("enumeration refused"));
            Check(result.Status == ProcessStopStatus.Failed && !process.HasExited && result.RemainingProcesses.Count > 0,
                "enumeration failure was mistaken for process exit");
        }
        finally { Check((await process.StopAndWaitAsync()).Success, "fixture cleanup failed"); }
    }

    [Test]
    public async Task ElevatedStopFailureKeepsProcessUntilSuccessfulRetry()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix sudo fixture"); return; }
        using var process = await Sleep();
        process.IsElevated = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Inject a rejected elevation command at the executable boundary. No authentication attempt.
        var result = await UnixProcessControl.StopAsync(process, "", timeout.Token, "/usr/bin/false");
        Check(!result.Success && result.RemainingProcesses.Count > 0 && !process.HasExited, "sudo failure forgot owner");
        process.IsElevated = false;
        Check((await process.StopAndWaitAsync()).Success, "normal retry failed");
    }

    [Test]
    public async Task ExitedElevatedWrapperDoesNotNeedAnotherAuthorizationToCleanUp()
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix wrapper exit fixture"); return; }
        using var process = await Sleep();
        using (var wrapper = Process.GetProcessById(process.Id))
        { wrapper.Kill(); await wrapper.WaitForExitAsync(); }
        process.IsElevated = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await UnixProcessControl.StopAsync(process, "", timeout.Token, "/usr/bin/false");
        Check(result.Success, "already exited wrapper incorrectly required sudo");
        Check((await process.StopAndWaitAsync()).Success, "exited wrapper cleanup was not idempotent");
    }

    [Test]
    public async Task QueuedReloadsDoNotLaunchAfterApplicationStartsExiting()
    {
        EnsureIsolatedStorage();
        var source = Path.Combine(Path.GetTempPath(), "queued-core-" + Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(source, """{"inbounds":[],"outbounds":[{"type":"direct"}]}""");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launchAttempts = 0;
        var paused = false;
        var manager = new CoreManager(_ => { launchAttempts++; return ("missing-test-core", ""); });
        Field("_updateFunc").SetValue(manager, (Func<bool, string, Task>)(async (_, message) =>
        {
            if (!paused && message.Contains("[主代理重载]"))
            { paused = true; entered.TrySetResult(); await release.Task; }
        }));
        var context = new CoreConfigContext
        {
            Node = new ProfileItem { ConfigType = EConfigType.Custom, CoreType = ECoreType.sing_box, Address = source },
            RunCoreType = ECoreType.sing_box,
        };
        var exiting = typeof(AppManager).GetProperty(nameof(AppManager.IsExiting), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var before = AppManager.Instance.IsExiting;
        Task<CoreLoadResult>? first = null, second = null;
        try
        {
            first = manager.LoadCore(context, null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            second = manager.LoadCore(context, null);
            Check(!second.IsCompleted, "reloads did not serialize");
            exiting.SetValue(AppManager.Instance, true);
            release.TrySetResult();
            Check(await first == CoreLoadResult.Failed && await second == CoreLoadResult.Failed, "exit did not cancel queued startup");
            Check(launchAttempts == 0, "a core was launched after shutdown began");
        }
        finally
        {
            release.TrySetResult();
            if (first != null) await first;
            if (second != null) await second;
            exiting.SetValue(AppManager.Instance, before);
            File.Delete(source);
        }
    }

    [Test]
    public async Task RealSingboxThirtyRestartsReuseTheSameListener()
    {
        var binary = Environment.GetEnvironmentVariable("SSH_TEST_SINGBOX");
        if (string.IsNullOrEmpty(binary)) { Skip.Test("Set SSH_TEST_SINGBOX for real core restart verification"); return; }
        EnsureIsolatedStorage();
        using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        var config = Path.Combine(Path.GetTempPath(), "core-restart-" + Guid.NewGuid() + ".json");
        var json = $$"""{"log":{"disabled":true},"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":{{port}}}],"outbounds":[{"type":"direct"}]}""";
        await File.WriteAllTextAsync(config, json);
        var listeners = CoreListenerCheck.Read(json);
        var originalRunningType = AppManager.Instance.RunningCoreType;
        var formalConfig = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        var previousConfig = File.Exists(formalConfig) ? await File.ReadAllBytesAsync(formalConfig) : null;
        var formalPre = Utils.GetBinConfigPath(Global.CorePreConfigFileName);
        var previousPre = File.Exists(formalPre) ? await File.ReadAllBytesAsync(formalPre) : null;
        var invalidPre = Path.Combine(Path.GetTempPath(), "invalid-pre-" + Guid.NewGuid() + ".json");
        var manager = new CoreManager(_ => (binary, ""));
        var context = new CoreConfigContext
        {
            Node = new ProfileItem { ConfigType = EConfigType.Custom, CoreType = ECoreType.sing_box, Address = config, DisplayLog = true },
            RunCoreType = ECoreType.sing_box, AppConfig = new Config(),
        };
        try
        {
            int? oldPid = null;
            for (var round = 0; round < 30; round++)
            {
                Check(await manager.LoadCore(context, null) == CoreLoadResult.Started, manager.LastError ?? "real core failed to reload");
                var process = (ProcessService)Field("_processService").GetValue(manager)!;
                Check(!process.HasExited && process.IsReady && manager.IsCoreRunning, "new core not ready");
                if (oldPid.HasValue)
                {
                    Check(process.Id != oldPid, "core was not replaced");
                    try { using var old = Process.GetProcessById(oldPid.Value); Check(old.HasExited, "old core remained alive"); }
                    catch (ArgumentException) { }
                }
                oldPid = process.Id;
            }
            using var prePortReservation = new TcpListener(IPAddress.Loopback, 0); prePortReservation.Start();
            var prePort = ((IPEndPoint)prePortReservation.LocalEndpoint).Port; prePortReservation.Stop();
            await File.WriteAllTextAsync(invalidPre, $$"""{"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":{{prePort}}}],"outbounds":[{"type":"not-a-core-outbound"}]}""");
            var preContext = context with { Node = new ProfileItem { ConfigType = EConfigType.Custom, CoreType = ECoreType.sing_box, Address = invalidPre, DisplayLog = true } };
            Check(await manager.LoadCore(context, preContext) == CoreLoadResult.Failed, "invalid pre-core was accepted");
            Check(Field("_processService").GetValue(manager) == null && Field("_processPreService").GetValue(manager) == null,
                "partial startup was not rolled back");
            Check(!string.IsNullOrEmpty(manager.LastError), "startup error was lost during cleanup");
            Check((await manager.CoreStop()).Success, "final core stop failed");
            await CoreListenerCheck.EnsureAvailableAsync(listeners);
        }
        finally
        {
            await manager.CoreStop();
            AppManager.Instance.RunningCoreType = originalRunningType;
            if (previousConfig == null) File.Delete(formalConfig); else await File.WriteAllBytesAsync(formalConfig, previousConfig);
            if (previousPre == null) File.Delete(formalPre); else await File.WriteAllBytesAsync(formalPre, previousPre);
            File.Delete(invalidPre);
            File.Delete(config);
        }
    }

    private static FieldInfo Field(string name) => typeof(CoreManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static void EnsureIsolatedStorage() => Check(Path.GetFullPath(Utils.StartupPath()).TrimEnd(Path.DirectorySeparatorChar)
        == Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), "test requires isolated app storage");
    private static async Task<ProcessService> Sleep()
    {
        var process = new ProcessService("/bin/sleep", "30", Path.GetTempPath(), false, false, null, null);
        await process.StartAsync(); return process;
    }
    private static void KillFixture(int pid)
    {
        try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
    private static void Check(bool condition, string error) { if (!condition) throw new Exception(error); }
}
