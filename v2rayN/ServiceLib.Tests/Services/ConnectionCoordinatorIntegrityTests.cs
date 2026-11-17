using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class ConnectionCoordinatorIntegrityTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EquivalentImportedIdentityRebindsWithoutRestartingEitherCore(bool withPre)
    {
        await using var fixture = await Fixture.Create();
        var main = await fixture.Context("old-id");
        var pre = withPre ? await fixture.Context("pre") : null;
        await fixture.Start(main, pre);
        var before = fixture.Manager.RuntimeSummary!;
        var mainProcess = Field("_processService").GetValue(fixture.Manager);
        var preProcess = Field("_processPreService").GetValue(fixture.Manager);
        main.Node.IndexId = "new-id";
        main.Node.Remarks = "equivalent imported profile";
        await (await fixture.Manager.LoadCore(main, pre, onlyIfChanged: true)).Should().BeEqualTo(CoreLoadResult.Unchanged);
        await fixture.Starts.Should().BeEqualTo(withPre ? 2 : 1);
        await ReferenceEquals(mainProcess, Field("_processService").GetValue(fixture.Manager)).Should().BeTrue();
        await ReferenceEquals(preProcess, Field("_processPreService").GetValue(fixture.Manager)).Should().BeTrue();
        await fixture.Manager.RuntimeSummary!.ProfileId.Should().BeEqualTo("new-id");
        await fixture.Manager.RuntimeSummary.Generation.Should().BeEqualTo(before.Generation);
        await fixture.Manager.RuntimeSummary.ProxyPort.Should().BeEqualTo(before.ProxyPort);
        var stalePublished = await fixture.Manager.PublishAvailabilityAsync(before, () => throw new Exception("stale result"), CancellationToken.None);
        await stalePublished.Should().BeFalse();
    }

    [Test]
    [Arguments("outbound")]
    [Arguments("metrics")]
    [Arguments("duplicate")]
    [Arguments("cross-core")]
    [Arguments("missing")]
    public async Task CandidateFailurePreservesRunningCoreAndFormalConfiguration(string failure)
    {
        await using var fixture = await Fixture.Create();
        var old = await fixture.Context("old");
        await fixture.Start(old);
        var generation = fixture.Manager.RuntimeSummary!.Generation;
        var original = await File.ReadAllTextAsync(fixture.MainFormal);
        var candidate = await fixture.Context("new");
        CoreConfigContext? pre = null;
        if (failure == "missing") fixture.MissingExecutable = true;
        else
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(candidate.Node.Address))!;
            if (failure == "outbound") root["outbounds"]![0]!["protocol"] = "not-a-core-outbound";
            if (failure == "metrics") root["metrics"] = new JsonObject { ["listen"] = "bad-listener" };
            if (failure == "duplicate") root["inbounds"]!.AsArray().Add(root["inbounds"]![0]!.DeepClone());
            await File.WriteAllTextAsync(candidate.Node.Address, root.ToJsonString());
            if (failure == "cross-core") pre = candidate;
        }
        await (await fixture.Manager.LoadCore(candidate, pre)).Should().BeEqualTo(CoreLoadResult.Failed);
        await fixture.Starts.Should().BeEqualTo(1);
        await fixture.Manager.RuntimeSummary!.Generation.Should().BeEqualTo(generation);
        await (await fixture.Manager.GetRuntimeStateAsync()).Ready.Should().BeTrue();
        await (await File.ReadAllTextAsync(fixture.MainFormal)).Should().BeEqualTo(original);
    }

    [Test]
    [Arguments("stopping", 0)]
    [Arguments("starting", 0)]
    [Arguments("main", 1)]
    [Arguments("pre", 2)]
    [Arguments("verifying", 2)]
    public async Task CancellationStopsOnlyCreatedProcessesAndRestoresBothFiles(string stage, int starts)
    {
        await using var fixture = await Fixture.Create();
        var main = await fixture.Context("main"); var pre = await fixture.Context("pre");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        fixture.Manager.ProcessStarted = process =>
        {
            fixture.Starts++;
            if (stage == "main" && fixture.Starts == 1 || stage == "pre" && fixture.Starts == 2) cancellation.Cancel();
        };
        var outcome = await fixture.Manager.LoadCore(main, pre, token: cancellation.Token, stageChanged: (current, _) =>
        {
            if (current.ToString().Equals(stage, StringComparison.OrdinalIgnoreCase)) cancellation.Cancel();
        });
        await outcome.Should().BeEqualTo(CoreLoadResult.Failed);
        await fixture.Starts.Should().BeEqualTo(starts);
        await fixture.Manager.HandoffState.Should().BeEqualTo(PortHandoffState.Cancelled);
        await fixture.Manager.RemainingProcesses.Should().HaveCount(0);
        await fixture.AssertOriginals();
        // A cancelled operation does not poison the next operation or its files.
        fixture.Manager.ProcessStarted = _ => fixture.Starts++;
        await fixture.Start(main, pre);
        await fixture.Starts.Should().BeEqualTo(starts + 2);
    }

    [Test]
    public async Task SecondPublishFailureRollsBackTheProductionTransactionAndRetryStartsOnce()
    {
        await using var fixture = await Fixture.Create();
        var main = await fixture.Context("main"); var pre = await fixture.Context("pre");
        fixture.Manager.ReplaceConfigFile = (source, target) =>
        {
            if (target == fixture.PreFormal) throw new IOException("injected second publication failure");
            File.Move(source, target, true);
        };
        await (await fixture.Manager.LoadCore(main, pre)).Should().BeEqualTo(CoreLoadResult.Failed);
        await fixture.Starts.Should().BeEqualTo(0);
        await fixture.AssertOriginals();
        fixture.Manager.ReplaceConfigFile = null;
        await fixture.Start(main, pre);
        await fixture.Starts.Should().BeEqualTo(2);
    }

    [Test]
    public async Task CancellationWhileStoppingIsNotReportedAsATimeoutAndRetainsOwnerForRetry()
    {
        await using var fixture = await Fixture.Create();
        var context = await fixture.Context("old"); await fixture.Start(context);
        var formal = await File.ReadAllTextAsync(fixture.MainFormal);
        using var cancel = new CancellationTokenSource();
        await (await fixture.Manager.LoadCore(await fixture.Context("next"), null, token: cancel.Token,
            stageChanged: (stage, _) => { if (stage == ConnectionStage.Stopping) cancel.Cancel(); })).Should().BeEqualTo(CoreLoadResult.Failed);
        await fixture.Manager.HandoffState.Should().BeEqualTo(PortHandoffState.Cancelled);
        await fixture.Manager.HasUnconfirmedProcesses.Should().BeTrue();
        await fixture.Starts.Should().BeEqualTo(1);
        await (await File.ReadAllTextAsync(fixture.MainFormal)).Should().BeEqualTo(formal);
        await fixture.Start(context);
        await fixture.Starts.Should().BeEqualTo(2);
    }

    [Test]
    public async Task InputsStayFrozenAndUnchangedProxyRetryKeepsHealthyCores()
    {
        await using var fixture = await Fixture.Create();
        var context = await fixture.Context("snapshot");
        var port = context.AppConfig.Inbound[0].LocalPort;
        var original = JsonUtils.DeepCopy(context.AppConfig);
        var originalNode = JsonUtils.DeepCopy(context.Node);
        await (await fixture.Manager.LoadCore(context, null, stageChanged: (stage, _) =>
        {
            if (stage != ConnectionStage.Stopping) return;
            context.AppConfig.Inbound[0].LocalPort = port + 20;
            context.Node.CoreType = ECoreType.sing_box;
            context.Node.Remarks = "late selection";
        })).Should().BeEqualTo(CoreLoadResult.Started);
        var runtime = fixture.Manager.RuntimeSummary!;
        await runtime.ProxyPort.Should().BeEqualTo(port);
        await runtime.Core.Should().BeEqualTo(ECoreType.Xray);
        fixture.Manager.SetProxyResult(false, "fixture proxy failure", runtime.Generation);
        var frozen = context with { AppConfig = original, Node = originalNode };
        await (await fixture.Manager.LoadCore(frozen, null, onlyIfChanged: true)).Should().BeEqualTo(CoreLoadResult.Unchanged);
        await (await fixture.Manager.GetRuntimeStateAsync()).ProxyApplied.Should().BeFalse();
        fixture.Manager.SetProxyResult(true, null, runtime.Generation);
        await fixture.Starts.Should().BeEqualTo(1);
        await (await fixture.Manager.GetRuntimeStateAsync()).Ready.Should().BeTrue();
        await fixture.Start(frozen); // Explicit restart always reloads, even with identical effective config.
        await fixture.Starts.Should().BeEqualTo(2);
        await (runtime.Generation != fixture.Manager.RuntimeSummary!.Generation).Should().BeTrue();
        var publications = 0;
        await (await fixture.Manager.PublishAvailabilityAsync(runtime, () => publications++, default)).Should().BeFalse();
        await (await fixture.Manager.PublishAvailabilityAsync(fixture.Manager.RuntimeSummary!, () => publications++, default)).Should().BeTrue();
        await publications.Should().BeEqualTo(1);
    }

    [Test]
    public async Task RequiredCoreExitIsIncompleteInBothDirectionsAndRestartRestoresBoth()
    {
        await using var fixture = await Fixture.Create();
        var main = await fixture.Context("main"); var pre = await fixture.Context("pre");
        await fixture.Start(main, pre);
        var oldGeneration = fixture.Manager.RuntimeSummary!.Generation;
        await StopOwned(fixture.Manager, "_processPreService");
        await (await fixture.Manager.GetRuntimeStateAsync()).Incomplete.Should().BeTrue();
        await (await fixture.Manager.GetRuntimeStateAsync()).Ready.Should().BeFalse();
        await fixture.Start(main, pre);
        await fixture.Starts.Should().BeEqualTo(4);
        await (oldGeneration != fixture.Manager.RuntimeSummary!.Generation).Should().BeTrue();
        await StopOwned(fixture.Manager, "_processService");
        await (await fixture.Manager.GetRuntimeStateAsync()).Incomplete.Should().BeTrue();
    }

    [Test]
    public async Task UnrelatedPortSuccessKeepsHandoffEvidenceThroughCancellationAndRetry()
    {
        if (!OperatingSystem.IsMacOS()) { Skip.Test("macOS handoff metadata fixture"); return; }
        await using var fixture = await Fixture.Create();
        var context = await fixture.Context("reserved");
        var port = context.AppConfig.Inbound[0].LocalPort;
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, port));
        reserved.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        var evidence = (List<CoreListener>)Field("_elevatedHandoff").GetValue(fixture.Manager)!;
        evidence.Add(new(IPAddress.Loopback, port, ProtocolType.Tcp));
        await fixture.Start(await fixture.Context("other"));
        await evidence.Should().HaveCount(1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = fixture.Manager.LoadCore(context, null, token: cancellation.Token,
            stageChanged: (stage, _) => { if (stage == ConnectionStage.Waiting) waiting.TrySetResult(); });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Starts.Should().BeEqualTo(1);
        cancellation.Cancel();
        await (await load).Should().BeEqualTo(CoreLoadResult.Failed);
        await evidence.Should().HaveCount(1);
        reserved.Close();
        await fixture.Start(context);
        await fixture.Starts.Should().BeEqualTo(2);
        await evidence.Should().HaveCount(0);
    }

    private static FieldInfo Field(string name) => typeof(CoreManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PartialLaunchFailureRetainsDiagnosticsAndOnlyRestoresAfterConfirmedCleanup(bool unconfirmed)
    {
        if (OperatingSystem.IsWindows()) { Skip.Test("Unix process fault fixture"); return; }
        await using var fixture = await Fixture.Create();
        var main = await fixture.Context("main"); var pre = await fixture.Context("pre", ECoreType.sing_box);
        var failedCore = Path.Combine(fixture.DirectoryPath, "failed-core.sh");
        await File.WriteAllTextAsync(failedCore, "#!/bin/sh\nif [ \"$1\" = check ]; then exit 0; fi\necho 'Failed to start: injected pre-core stdout failure'\nexit 1\n");
        File.SetUnixFileMode(failedCore, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        fixture.SecondaryExecutable = failedCore;
        ConcurrentDictionary<int, ProcessIdentity>? identities = null;
        ProcessIdentity? original = null;
        fixture.Manager.ProcessStarted = _ =>
        {
            fixture.Starts++;
            if (!unconfirmed || fixture.Starts != 2) return;
            var process = (ProcessService)Field("_processService").GetValue(fixture.Manager)!;
            identities = (ConcurrentDictionary<int, ProcessIdentity>)typeof(ProcessService)
                .GetField("_identities", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(process)!;
            original = identities[process.Id];
            identities[process.Id] = original with { StartTime = "identity-fault" };
        };
        try
        {
            await (await fixture.Manager.LoadCore(main, pre)).Should().BeEqualTo(CoreLoadResult.Failed);
            await fixture.Starts.Should().BeEqualTo(2);
            await fixture.Manager.LastError!.Should().Contain("injected pre-core stdout failure");
            await fixture.Manager.HasUnconfirmedProcesses.Should().BeEqualTo(unconfirmed);
            if (unconfirmed)
            {
                await (await File.ReadAllTextAsync(fixture.MainFormal)).Should().Contain(main.AppConfig.Inbound[0].LocalPort.ToString());
                await (Field("_pendingConfigTransaction").GetValue(fixture.Manager) != null).Should().BeTrue();
                await (await fixture.Manager.GetRuntimeStateAsync()).Ready.Should().BeFalse();
            }
            else await fixture.AssertOriginals();
        }
        finally { if (original != null) identities![original.Pid] = original; }
        var singbox = Environment.GetEnvironmentVariable("TUN_TEST_SINGBOX");
        if (string.IsNullOrEmpty(singbox)) { Skip.Test("Set TUN_TEST_SINGBOX for successful retry"); return; }
        fixture.SecondaryExecutable = singbox;
        fixture.Manager.ProcessStarted = _ => fixture.Starts++;
        await fixture.Start(main, pre);
        await fixture.Starts.Should().BeEqualTo(4);
        await fixture.Manager.HasUnconfirmedProcesses.Should().BeFalse();
    }

    private static async Task StopOwned(CoreManager manager, string field)
    {
        var process = (ProcessService)Field(field).GetValue(manager)!;
        if (!(await process.StopAndWaitAsync()).Success) throw new Exception("Fixture core did not stop");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string DirectoryPath = "";
        internal string MainFormal = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        internal string PreFormal = Utils.GetBinConfigPath(Global.CorePreConfigFileName);
        private byte[]? _mainBefore, _preBefore;
        internal CoreManager Manager = null!;
        internal int Starts;
        internal bool MissingExecutable;
        internal string? SecondaryExecutable;

        internal static async Task<Fixture> Create()
        {
            var binary = Environment.GetEnvironmentVariable("HANDOFF_TEST_XRAY");
            if (string.IsNullOrEmpty(binary)) Skip.Test("Set HANDOFF_TEST_XRAY for isolated coordinator fixtures");
            if (Path.GetFullPath(Utils.StartupPath()).TrimEnd(Path.DirectorySeparatorChar) != Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Requires isolated app storage");
            var f = new Fixture { DirectoryPath = Path.Combine(Path.GetTempPath(), "coordinator-integrity-" + Guid.NewGuid()) };
            Directory.CreateDirectory(f.DirectoryPath);
            f._mainBefore = File.Exists(f.MainFormal) ? await File.ReadAllBytesAsync(f.MainFormal) : null;
            f._preBefore = File.Exists(f.PreFormal) ? await File.ReadAllBytesAsync(f.PreFormal) : null;
            f.Manager = new CoreManager(info => (f.MissingExecutable ? Path.Combine(f.DirectoryPath, "missing-core")
                : info?.CoreType == ECoreType.sing_box ? f.SecondaryExecutable ?? "" : binary!, ""))
                { ProcessStarted = _ => f.Starts++ };
            return f;
        }

        internal async Task<CoreConfigContext> Context(string name, ECoreType core = ECoreType.Xray)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var config = CoreConfigTestFactory.CreateConfig(); config.Inbound[0].LocalPort = port;
            CoreConfigTestFactory.BindAppManagerConfig(config);
            var path = Path.Combine(DirectoryPath, name + ".json");
            await File.WriteAllTextAsync(path, core == ECoreType.Xray
                ? $$$"""{"inbounds":[{"protocol":"mixed","listen":"127.0.0.1","port":{{{port}}},"settings":{"udp":true}}],"outbounds":[{"protocol":"freedom"}]}"""
                : $$"""{"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":{{port}}}],"outbounds":[{"type":"direct"}]}""");
            return new() { Node = new ProfileItem { IndexId = name, Remarks = name, ConfigType = EConfigType.Custom, CoreType = core, Address = path, DisplayLog = true },
                RunCoreType = core, AppConfig = config };
        }
        internal async Task Start(CoreConfigContext main, CoreConfigContext? pre = null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (await Manager.LoadCore(main, pre, token: deadline.Token) != CoreLoadResult.Started) throw new Exception(Manager.LastError);
            await (await Manager.GetRuntimeStateAsync()).Ready.Should().BeTrue();
        }
        internal async Task AssertOriginals()
        {
            foreach (var (path, bytes) in new[] { (MainFormal, _mainBefore), (PreFormal, _preBefore) })
                await (File.Exists(path) ? Convert.ToHexString(await File.ReadAllBytesAsync(path)) : "").Should().BeEqualTo(bytes == null ? "" : Convert.ToHexString(bytes));
        }
        public async ValueTask DisposeAsync()
        {
            if (!(await Manager.CoreStop()).Success) throw new Exception("Fixture cleanup unconfirmed; keeping associated configs");
            foreach (var (path, bytes) in new[] { (MainFormal, _mainBefore), (PreFormal, _preBefore) })
                if (bytes == null) File.Delete(path); else await File.WriteAllBytesAsync(path, bytes);
            Directory.Delete(DirectoryPath, true);
        }
    }
}
