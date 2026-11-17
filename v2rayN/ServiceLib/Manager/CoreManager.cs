namespace ServiceLib.Manager;

internal sealed record CoreProcessRequirements(bool PreRequired);

/// <summary>
/// Core process processing class
/// </summary>
public class CoreManager
{
    private static readonly Lazy<CoreManager> _instance = new(() => new());
    public static CoreManager Instance => _instance.Value;
    private Config _config;
    private readonly Func<CoreInfo?, (string FileName, string Message)> _resolveExecutable;
    private readonly Func<string, ECoreType, string, IDictionary<string, string?>, CancellationToken, Task<string?>> _validateConfig;
    internal Action<ProcessService>? ProcessStarted { get; set; }
    internal Action<string, string>? ReplaceConfigFile { get; set; }
    private CoreConfigTransaction? _pendingConfigTransaction;
    private readonly HashSet<string> _pendingValidationConfigs = [];
    private bool? _proxyApplied;
    private string? _proxyDetail;
    private readonly HashSet<string> _releasedTunInterfaces = [];

    public CoreManager() : this(info =>
    {
        var file = CoreInfoManager.Instance.GetCoreExecFile(info, out var message);
        return (file, message);
    }) { }

    internal CoreManager(Func<CoreInfo?, (string FileName, string Message)> resolveExecutable,
        Func<string, ECoreType, string, IDictionary<string, string?>, CancellationToken, Task<string?>>? validateConfig = null)
    {
        _resolveExecutable = resolveExecutable;
        _validateConfig = validateConfig ?? RoutingCoreCompatibility.CheckExecutableAsync;
    }

    [SupportedOSPlatform("windows")]
    private WindowsJobService? _processJob;

    private ProcessService? _processService;
    private ProcessService? _processPreService;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    public string? LastError { get; private set; }
    private readonly List<CoreListener> _elevatedHandoff = [];
    internal PortHandoffState HandoffState { get; private set; }
    public bool? RunningTunEnabled { get; private set; }
    public CoreRuntimeSummary? RuntimeSummary { get; private set; }
    public bool HasUnconfirmedProcesses => _pendingValidationConfigs.Count > 0
        || _runningConfig == null && (_processService != null || _processPreService != null);
    internal bool HandoffNeedsManualRetry => _elevatedHandoff.Count > 0 && HandoffState == PortHandoffState.Failed;
    private EffectiveCoreConfig? _runningConfig;
    // Startup requirements survive a partial launch; effective config is recorded only
    // after every required process starts successfully.
    private CoreProcessRequirements? _processRequirements;
    internal async Task<(LocalHealth Main, LocalHealth Pre)> GetRecoveryProcessHealthAsync(CancellationToken token = default)
    {
        static async Task<LocalHealth> ReadAsync(ProcessService? process, CancellationToken token)
        {
            try { return process != null && !await process.HasExitedAsync(token).ConfigureAwait(false) ? LocalHealth.Healthy : LocalHealth.Failed; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
            { return LocalHealth.Unknown; }
        }
        token.ThrowIfCancellationRequested();
        var requirements = _processRequirements;
        var main = _processService;
        var pre = _processPreService;
        return (await ReadAsync(main, token).ConfigureAwait(false), requirements == null ? LocalHealth.Unknown
            : requirements.PreRequired ? await ReadAsync(pre, token).ConfigureAwait(false) : LocalHealth.Healthy);
    }
    public Task<bool> IsCoreRunningAsync(CancellationToken token = default) => IsRunningAsync(_processService, token);

    internal async Task<ConnectionRuntimeState> GetRuntimeStateAsync(CancellationToken token = default)
    {
        var main = await IsRunningAsync(_processService, token);
        var required = _processRequirements?.PreRequired == true;
        var pre = await IsRunningAsync(_processPreService, token);
        var tun = RunningTunEnabled == true && main && (!required || pre) && !HasUnconfirmedProcesses;
        return new(main, required, pre, HasUnconfirmedProcesses, tun, RuntimeSummary, _proxyApplied, _proxyDetail);
    }

    internal void SetProxyResult(bool? applied, string? detail, string? generation)
    {
        if (RuntimeSummary?.Generation != generation) return;
        _proxyApplied = applied; _proxyDetail = detail;
    }

    internal async Task<bool> PublishAvailabilityAsync(CoreRuntimeSummary expected, Action publish, CancellationToken token)
    {
        await _lifecycleGate.WaitAsync(token);
        try
        {
            var state = await GetRuntimeStateAsync(token);
            if (!state.Ready || state.Summary?.Generation != expected.Generation
                || state.Summary.ProfileId != expected.ProfileId || state.Summary.ProxyPort != expected.ProxyPort) return false;
            token.ThrowIfCancellationRequested();
            publish();
            return true;
        }
        finally { _lifecycleGate.Release(); }
    }

    private static async Task<bool> IsRunningAsync(ProcessService? process, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try { return process is { IsReady: true } && !await process.HasExitedAsync(token).ConfigureAwait(false); }
        catch (InvalidOperationException) { return false; }
    }
    private Func<bool, string, Task>? _updateFunc;
    private const string _tag = "CoreHandler";

    public async Task Init(Config config, Func<bool, string, Task> updateFunc)
    {
        _config = config;
        _updateFunc = updateFunc;

        //Copy the bin folder to the storage location (for init)
        if (Environment.GetEnvironmentVariable(Global.LocalAppData) == "1")
        {
            var fromPath = Utils.GetBaseDirectory("bin");
            var toPath = Utils.GetBinPath("");
            if (fromPath != toPath)
            {
                FileUtils.CopyDirectory(fromPath, toPath, true, false);
            }
        }

        if (Utils.IsNonWindows())
        {
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo();
            foreach (var it in coreInfo)
            {
                if (it.CoreType == ECoreType.v2rayN)
                {
                    if (Utils.UpgradeAppExists(out var upgradeFileName))
                    {
                        await Utils.SetLinuxChmod(upgradeFileName);
                    }
                    continue;
                }

                foreach (var name in it.CoreExes)
                {
                    var exe = Utils.GetBinPath(Utils.GetExeName(name), it.CoreType.ToString());
                    if (File.Exists(exe))
                    {
                        await Utils.SetLinuxChmod(exe);
                    }
                }
            }
        }
    }

    /// <param name="mainContext">Resolved main context (with pre-socks ports already merged if applicable).</param>
    /// <param name="preContext">Optional pre-socks context passed to <see cref="CoreStartPreService"/>.</param>
    public async Task<CoreLoadResult> LoadCore(CoreConfigContext? mainContext, CoreConfigContext? preContext,
        bool onlyIfChanged = false, string reason = "用户操作或配置变更", CancellationToken token = default,
        Action<string>? progress = null, Action<ConnectionStage, int>? stageChanged = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, AppManager.Instance.ExitToken);
        await _lifecycleGate.WaitAsync(lifetime.Token);
        try { return await LoadCoreUnderLock(mainContext, preContext, onlyIfChanged, reason, lifetime.Token, progress, stageChanged); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<CoreLoadResult> LoadCoreUnderLock(CoreConfigContext? mainContext, CoreConfigContext? preContext,
        bool onlyIfChanged, string reason, CancellationToken token, Action<string>? progress, Action<ConnectionStage, int>? stageChanged)
    {
        if (AppManager.Instance.IsExiting) return CoreLoadResult.Failed;
        LastError = null;
        if (mainContext == null)
        {
            LastError = ResUI.CheckServerSettings;
            await UpdateFunc(false, ResUI.CheckServerSettings);
            return CoreLoadResult.Failed;
        }

        mainContext = Snapshot(mainContext);
        preContext = preContext == null ? null : Snapshot(preContext);
        var operationId = Guid.NewGuid().ToString("N");
        var node = mainContext.Node;
        // Generate candidates away from the files in use. A skipped reload must not touch them.
        var fileName = Utils.GetBinConfigPath($"candidate-{Utils.GetGuid(false)}.json");
        var preFileName = Utils.GetBinConfigPath($"candidate-pre-{Utils.GetGuid(false)}.json");
        var startingNewProcesses = false;
        try
        {
            await CleanupValidationProcessesAsync(token);
            if (_pendingConfigTransaction != null)
            {
                var cleanup = await CoreStopUnderLock(token: token);
                if (!cleanup.Success) throw new IOException(cleanup.Error);
                RestorePendingConfiguration();
            }
            var result = await CoreConfigHandler.GenerateClientConfig(mainContext, fileName);
            if (result.Success != true)
            {
                LastError = result.Msg;
                await UpdateFunc(true, result.Msg);
                return CoreLoadResult.Failed;
            }

            if (preContext != null)
            {
                var preResult = await CoreConfigHandler.GenerateClientConfig(preContext, preFileName);
                if (!preResult.Success)
                {
                    LastError = preResult.Msg;
                    await UpdateFunc(true, preResult.Msg);
                    return CoreLoadResult.Failed;
                }
            }
            // Parse both candidates before stopping any running process.
            var mainPlan = CoreListenerPlan.Read(await File.ReadAllTextAsync(fileName, token), mainContext.RunCoreType);
            var prePlan = preContext == null ? null : CoreListenerPlan.Read(await File.ReadAllTextAsync(preFileName, token), preContext.RunCoreType);
            CoreListenerPlan.ValidateTogether(mainPlan, prePlan);
            var mainListeners = mainPlan.Ports;
            var preListeners = prePlan?.Ports ?? [];
            var candidate = new EffectiveCoreConfig(
                EffectiveCoreConfig.Fingerprint(await File.ReadAllTextAsync(fileName), node.ConfigType != EConfigType.Custom), mainContext.RunCoreType,
                preContext == null ? null : EffectiveCoreConfig.Fingerprint(await File.ReadAllTextAsync(preFileName), preContext.Node.ConfigType != EConfigType.Custom),
                preContext?.RunCoreType);
            var changeReason = candidate.ReloadReason(_runningConfig, await IsCoreRunningAsync(), await IsRunningAsync(_processPreService));
            var nodeName = node.Remarks?.ReplaceLineEndings(" ").Replace('\t', ' ');
            var reloadLog = onlyIfChanged && changeReason == null
                ? $"[主代理][节点：{nodeName}] {reason}：有效配置未变化，跳过重载，保留现有连接。"
                : $"[主代理重载][节点：{nodeName}] 原因：{reason}；{changeReason ?? "主动重载"}。";
            Logging.SaveLog(reloadLog);
            await UpdateFunc(false, reloadLog);
            if (onlyIfChanged && changeReason == null)
            {
                // Subscription imports may replace IDs without changing either live core.
                // Keep process identity / generation but rebind equivalent presentation metadata.
                if (RuntimeSummary is { } runtime)
                    RuntimeSummary = runtime with { ProfileId = mainContext.SourceNodeId ?? node.IndexId,
                        Server = node.GetSummary(), Routing = mainContext.RoutingItem?.Remarks };
                return CoreLoadResult.Unchanged;
            }

            var mainLaunch = await PrepareLaunchAsync(operationId, mainContext, mainPlan, fileName, token);
            var preLaunch = preContext == null ? null : await PrepareLaunchAsync(operationId, preContext, prePlan!, preFileName, token);

            await UpdateFunc(false, $"{node.GetSummary()}");
            await UpdateFunc(false, $"{Utils.GetRuntimeInfo()}");
            await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
            await RememberElevatedAsync(token);
            HandoffState = PortHandoffState.Stopping;
            stageChanged?.Invoke(ConnectionStage.Stopping, 0);
            var stopped = await CoreStopUnderLock(token: token);
            if (!stopped.Success) throw new IOException(stopped.Error);
            token.ThrowIfCancellationRequested();
            var handoff = new List<CoreListener>();
            if (OperatingSystem.IsMacOS() && _elevatedHandoff.Count > 0)
            {
                handoff = SelectHandoffTargets(mainContext, preContext, mainListeners, preListeners, _elevatedHandoff).ToList();
            }
            if (handoff.Count > 0)
            {
                HandoffState = PortHandoffState.Waiting;
                await PortHandoff.WaitAsync(handoff, token, elapsed =>
                {
                    stageChanged?.Invoke(ConnectionStage.Waiting, (int)elapsed.TotalSeconds);
                    progress?.Invoke($"正在等待目标核心端口可用（{elapsed.TotalSeconds:F0}/90 秒） / Waiting for target core ports");
                });
                progress?.Invoke(string.Empty);
            }
            token.ThrowIfCancellationRequested();
            HandoffState = PortHandoffState.Starting;
            stageChanged?.Invoke(ConnectionStage.Starting, 0);
            if (CoreListenerCheck.Supports(mainContext.RunCoreType))
                await CoreListenerCheck.EnsureAvailableAsync(mainListeners,
                    GetElevatedListenerProbe(mainContext.IsTunEnabled, mainContext.RunCoreType), token);
            if (preContext != null && CoreListenerCheck.Supports(preContext.RunCoreType))
                await CoreListenerCheck.EnsureAvailableAsync(preListeners,
                    GetElevatedListenerProbe(preContext.IsTunEnabled, preContext.RunCoreType), token);
            token.ThrowIfCancellationRequested();
            if (AppManager.Instance.IsExiting) throw new OperationCanceledException("应用正在退出，已取消启动 / Application is exiting.");
            var files = new List<(string, string)> { (fileName, Utils.GetBinConfigPath(Global.CoreConfigFileName)) };
            if (preContext != null) files.Add((preFileName, Utils.GetBinConfigPath(Global.CorePreConfigFileName)));
            _pendingConfigTransaction = new CoreConfigTransaction(files, ReplaceConfigFile);
            _pendingConfigTransaction.Publish();
            _processRequirements = new(preContext != null);
            startingNewProcesses = true;

            if (Utils.IsWindows() && (mainContext.IsTunEnabled || preContext?.IsTunEnabled == true))
            {
                await Task.Delay(100, token);
                await WindowsUtils.RemoveTunDevice();
            }

            token.ThrowIfCancellationRequested();
            await CoreStart(mainLaunch, token);
            if (!await IsCoreRunningAsync())
                throw new IOException(LastError ?? ResUI.FailedToRunCore);
            await WaitForProxyPort(preContext, token);
            token.ThrowIfCancellationRequested();
            if (AppManager.Instance.IsExiting) throw new OperationCanceledException("应用正在退出，已取消启动 / Application is exiting.");
            await CoreStartPreService(preLaunch, token);
            stageChanged?.Invoke(ConnectionStage.Verifying, 0);

            if (_processService != null)
            {
                await UpdateFunc(true, $"{node.GetSummary()}");
            }
            if (!await IsCoreRunningAsync() || preContext != null && !await IsRunningAsync(_processPreService))
                throw new IOException(LastError ?? ResUI.FailedToRunCore);
            token.ThrowIfCancellationRequested();
            _runningConfig = candidate;
            AppManager.Instance.RunningCoreType = preContext?.RunCoreType ?? mainContext.RunCoreType;
            var ordinary = new[] { mainLaunch, preLaunch }.OfType<CoreLaunchSnapshot>()
                .Where(l => !l.Elevated)
                .SelectMany(l => l.Listeners.Ports).ToArray();
            _elevatedHandoff.RemoveAll(old => ordinary.Any(p => p.Address.Equals(old.Address) && p.Port == old.Port && p.Protocol == old.Protocol));
            _pendingConfigTransaction.Commit();
            _pendingConfigTransaction = null;
            HandoffState = PortHandoffState.Completed;
            RunningTunEnabled = mainContext.IsTunEnabled || preContext?.IsTunEnabled == true;
            RuntimeSummary = new(mainContext.SourceNodeId ?? node.IndexId, node.GetSummary(),
                mainContext.RoutingItem?.Remarks, RunningTunEnabled == true) { Core = mainContext.RunCoreType,
                    ProxyPort = mainLaunch.ProxyPort, Generation = operationId };
            _proxyApplied = null; _proxyDetail = null;
            return CoreLoadResult.Started;
        }
        catch (Exception ex)
        {
            var cancelled = token.IsCancellationRequested || ex is OperationCanceledException;
            HandoffState = cancelled ? PortHandoffState.Cancelled : PortHandoffState.Failed;
            LastError = cancelled ? "操作已取消 / Operation cancelled. " + ex.Message : ex.Message;
            if (startingNewProcesses)
            {
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var cleanup = await CoreStopUnderLock(clearRequirements: false, token: cleanupDeadline.Token);
                if (!cleanup.Success) LastError += Environment.NewLine + cleanup.Error;
            }
            if (_processService == null && _processPreService == null)
                try { RestorePendingConfiguration(); }
                catch (Exception restore) { LastError += Environment.NewLine + restore.Message; }
            Logging.SaveLog(_tag + ": " + LastError);
            if (ex is not OperationCanceledException) await UpdateFunc(true, LastError);
            return CoreLoadResult.Failed;
        }
        finally
        {
            if (!ProcessService.HasUnstoppedConfig(fileName)) File.Delete(fileName);
            if (!ProcessService.HasUnstoppedConfig(preFileName)) File.Delete(preFileName);
        }
    }

    private static CoreConfigContext Snapshot(CoreConfigContext context) => context with
    {
        AppConfig = JsonUtils.DeepCopy(context.AppConfig), Node = JsonUtils.DeepCopy(context.Node),
        RoutingItem = context.RoutingItem == null ? null : JsonUtils.DeepCopy(context.RoutingItem),
        SimpleDnsItem = JsonUtils.DeepCopy(context.SimpleDnsItem),
        RawDnsItem = context.RawDnsItem == null ? null : JsonUtils.DeepCopy(context.RawDnsItem),
        FullConfigTemplate = context.FullConfigTemplate == null ? null : JsonUtils.DeepCopy(context.FullConfigTemplate),
        AllProxiesMap = context.AllProxiesMap.ToDictionary(p => p.Key, p => JsonUtils.DeepCopy(p.Value)),
        CustomOutboundContent = new(context.CustomOutboundContent), ProtectDomainList = [.. context.ProtectDomainList],
        ProtectCoreTypeList = [.. context.ProtectCoreTypeList], CustomOutboundMap = new(context.CustomOutboundMap),
        ServerTestItemMap = new(context.ServerTestItemMap)
    };

    private async Task<CoreLaunchSnapshot> PrepareLaunchAsync(string operationId, CoreConfigContext context, CoreListenerPlan listeners, string path, CancellationToken token)
    {
        var info = CoreInfoManager.Instance.GetCoreInfo(context.RunCoreType) ?? throw new IOException("Unknown core: " + context.RunCoreType);
        var (executable, error) = _resolveExecutable(info);
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) throw new IOException(error.NullIfEmpty() ?? "Core executable is missing: " + executable);
        if (CoreListenerCheck.Supports(context.RunCoreType))
        {
            string? failure;
            try { failure = await _validateConfig(executable, context.RunCoreType, path, info.Environment, token); }
            finally { if (ProcessService.HasUnstoppedConfig(path)) _pendingValidationConfigs.Add(path); }
            if (failure != null) throw new IOException(failure);
        }
        token.ThrowIfCancellationRequested();
        return new(operationId, context, JsonUtils.DeepCopy(info), executable, listeners,
            ShouldRunAsSudo(context.IsTunEnabled, context.RunCoreType, Utils.IsNonWindows()), AppManager.GetLocalPort(context.AppConfig, EInboundProtocol.socks));
    }

    private void RestorePendingConfiguration()
    {
        _pendingConfigTransaction?.Rollback();
        _pendingConfigTransaction = null;
    }

    private async Task CleanupValidationProcessesAsync(CancellationToken token)
    {
        if (_pendingValidationConfigs.Count == 0) return;
        await ProcessService.RetryFailedConfigStopsAsync(_pendingValidationConfigs, token);
        foreach (var path in _pendingValidationConfigs.ToArray())
            if (!ProcessService.HasUnstoppedConfig(path)) { File.Delete(path); _pendingValidationConfigs.Remove(path); }
        if (_pendingValidationConfigs.Count > 0) throw new IOException("配置检查进程退出未确认 / Configuration checker exit unconfirmed.");
    }

    private async Task RememberElevatedAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) return;
        foreach (var process in new[] { _processService, _processPreService })
            // The sudo wrapper may have created the core before its identity marker is read.
            // Keep the declared ports once any owned process started, even on cancellation.
            if (process?.IsElevated == true && process.KnownProcesses.Count > 0)
            {
                var ports = process.ListenerPlan?.Ports;
                if (ports == null && process.ConfigPath != null)
                    ports = CoreListenerCheck.Read(await File.ReadAllTextAsync(process.ConfigPath, token));
                foreach (var p in ports ?? [])
                    if (!_elevatedHandoff.Any(old => old.Address.Equals(p.Address) && old.Port == p.Port && old.Protocol == p.Protocol)) _elevatedHandoff.Add(p);
            }
    }

    public async Task<ProcessService?> LoadCoreConfigSpeedtest(List<ServerTestItem> selecteds)
    {
        var coreType = selecteds.FirstOrDefault()?.CoreType == ECoreType.sing_box ? ECoreType.sing_box : ECoreType.Xray;
        var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
        var configPath = Utils.GetBinConfigPath(fileName);
        var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(_config, configPath, selecteds, coreType);
        await UpdateFunc(false, result.Msg);
        if (result.Success != true)
        {
            return null;
        }

        await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
        await UpdateFunc(false, configPath);

        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        return await RunProcess(coreInfo, fileName, true, false);
    }

    public async Task<ProcessService?> LoadCoreConfigSpeedtest(ServerTestItem testItem, string? logPrefix = null)
    {
        var node = await AppManager.Instance.GetProfileItem(testItem.IndexId);
        if (node is null)
        {
            return null;
        }

        var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
        var configPath = Utils.GetBinConfigPath(fileName);
        var (context, _) = await CoreConfigContextBuilder.Build(_config, node);
        var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(_config, context, testItem, configPath);
        if (result.Success != true)
        {
            return null;
        }

        var coreType = context.RunCoreType;
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        var process = await RunProcess(coreInfo, fileName, true, false, logPrefix: logPrefix);
        if (process != null) process.TemporaryConfigPath = configPath;
        else if (!ProcessService.HasUnstoppedConfig(configPath)) File.Delete(configPath);
        return process;
    }

    internal IReadOnlyList<ProcessIdentity> RemainingProcesses => new[] { _processPreService, _processService }
        .Where(p => p != null).SelectMany(p => p!.KnownProcesses).ToArray();

    public async Task<ProcessStopResult> CoreStop(CancellationToken token = default)
    {
        await _lifecycleGate.WaitAsync(token);
        try
        {
            var result = await CoreStopUnderLock(token: token);
            try { await CleanupValidationProcessesAsync(token); }
            catch (Exception ex) { return new(ProcessStopStatus.Failed, RemainingProcesses, ex.Message); }
            if (result.Success) RestorePendingConfiguration();
            return result;
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<ProcessStopResult> CoreStopUnderLock(bool clearRequirements = true, CancellationToken token = default)
    {
        try { await RememberElevatedAsync(token); }
        catch (Exception ex) { Logging.SaveLog("Remember stopped core listeners", ex); }
        _runningConfig = null;
        var results = new List<ProcessStopResult>();
        foreach (var process in new[] { _processPreService, _processService })
        {
            if (process == null) continue;
            try
            {
                var tunInterfaces = process.ReadyTunInterfaces;
                var result = await process.StopAndWaitAsync(token);
                results.Add(result);
                if (!result.Success) continue;
                if (tunInterfaces != null) _releasedTunInterfaces.UnionWith(tunInterfaces);
                process.Dispose();
                if (ReferenceEquals(process, _processPreService)) _processPreService = null;
                if (ReferenceEquals(process, _processService)) _processService = null;
            }
            catch (Exception ex)
            {
                results.Add(new(ProcessStopStatus.Failed, process.KnownProcesses, ex.Message));
            }
        }
        var combined = ProcessStopResult.Combine(results);
        if (combined.Success) { RunningTunEnabled = null; RuntimeSummary = null; _proxyApplied = null; _proxyDetail = null; }
        if (combined.Success && clearRequirements) _processRequirements = null;
        if (!combined.Success) Logging.SaveLog(_tag + ": " + combined.Error);
        return combined;
    }

    #region Private

    private async Task CoreStart(CoreLaunchSnapshot launch, CancellationToken token)
    {
        var context = launch.Context;
        var node = context.Node;
        var coreInfo = launch.Info;

        var displayLog = node.ConfigType != EConfigType.Custom || node.DisplayLog;
        var proc = await RunProcess(coreInfo, Global.CoreConfigFileName, displayLog, true, context.IsTunEnabled,
            onCreated: process => _processService = process, requireSingboxReady: node.ConfigType != EConfigType.Custom,
            token: token, launch: launch);
        if (proc is null)
        {
            return;
        }
        _processService = proc;
    }

    private async Task CoreStartPreService(CoreLaunchSnapshot? launch, CancellationToken token)
    {
        if (launch != null && await IsCoreRunningAsync(token))
        {
            var proc = await RunProcess(launch.Info, Global.CorePreConfigFileName, true, true, launch.Context.IsTunEnabled,
                onCreated: process => _processPreService = process, token: token, launch: launch);
            if (proc is null)
            {
                return;
            }
            _processPreService = proc;
        }
    }

    private async Task UpdateFunc(bool notify, string msg)
    {
        if (_updateFunc != null) await _updateFunc(notify, msg);
    }

    internal static IReadOnlyList<CoreListener> SelectHandoffTargets(CoreConfigContext main, CoreConfigContext? pre,
        IReadOnlyList<CoreListener> mainListeners, IReadOnlyList<CoreListener> preListeners, IReadOnlyList<CoreListener> previous)
    {
        var targets = new List<CoreListener>();
        if (!ShouldRunAsSudo(main.IsTunEnabled, main.RunCoreType, true)) targets.AddRange(mainListeners);
        if (pre != null && !ShouldRunAsSudo(pre.IsTunEnabled, pre.RunCoreType, true)) targets.AddRange(preListeners);
        return targets.Where(p => previous.Any(old => CoreListenerCheck.Overlaps(p, old))).Distinct().ToArray();
    }

    private static async Task WaitForProxyPort(CoreConfigContext? preContext, CancellationToken token)
    {
        if (preContext is null)
        {
            return;
        }
        if (!preContext.IsTunEnabled)
        {
            return;
        }

        using var rootCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        rootCts.CancelAfter(Global.LocalFetch);
        var rootToken = rootCts.Token;

        var port = preContext.Node.Port;
        // SOCKS5 client greeting: VER=5, NMETHODS=1, METHOD=0x00 (no auth)
        ReadOnlyMemory<byte> greeting = new byte[] { 0x05, 0x01, 0x00 };
        var buf = new byte[2];

        while (!rootToken.IsCancellationRequested)
        {
            using var tcp = new TcpClient();
            using var attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(rootToken, attemptCts.Token);
            var linkedToken = linkedCts.Token;
            try
            {
                await tcp.ConnectAsync(Global.Loopback, port, linkedToken);
                var stream = tcp.GetStream();

                await stream.WriteAsync(greeting, linkedToken);

                await stream.ReadExactlyAsync(buf, linkedToken);

                // Server selection: VER=5, METHOD=0x00 — proxy is fully ready
                if (buf[0] == 0x05 && buf[1] == 0)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                if (!rootToken.IsCancellationRequested)
                {
                    continue;
                }
                throw new TimeoutException($"主内核代理端口未就绪 / Proxy port {port} did not become ready.");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // Connection refused, proxy not ready yet, wait 50ms before retrying
                try
                {
                    await Task.Delay(50, rootToken);
                }
                catch (OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException($"主内核代理端口未就绪 / Proxy port {port} did not become ready.");
                }
            }
            catch
            {
                // Ignore other exceptions and continue
            }
        }
        token.ThrowIfCancellationRequested();
        throw new TimeoutException($"主内核代理端口未就绪 / Proxy port {port} did not become ready.");
    }

    #endregion Private

    #region Process

    /// <summary>
    ///     Decides whether a core launch must be elevated on non-Windows platforms.
    ///     The TUN state comes from the immutable <see cref="CoreConfigContext" /> snapshot that
    ///     generated the config, never from the live mutable config: the generated config and the
    ///     launch mode must always agree, even if TUN is toggled while a reload is in flight.
    /// </summary>
    public static bool ShouldRunAsSudo(bool isTunLaunch, ECoreType? coreType, bool isNonWindows)
    {
        return isTunLaunch
            && coreType is ECoreType.sing_box or ECoreType.mihomo or ECoreType.Xray
            && isNonWindows;
    }

    private static Func<CoreListener, CancellationToken, Task<SocketError>>? GetElevatedListenerProbe(bool isTunLaunch, ECoreType? coreType)
        => ShouldRunAsSudo(isTunLaunch, coreType, Utils.IsNonWindows())
            ? (endpoint, token) => CoreAdminManager.CheckListenerAsSudoAsync(endpoint, token) : null;

    private async Task<ProcessService?> RunProcess(CoreInfo? coreInfo, string configPath, bool displayLog, bool mayNeedSudo, bool isTunLaunch = false, string? logPrefix = null,
        Action<ProcessService>? onCreated = null, bool requireSingboxReady = true, CancellationToken token = default, CoreLaunchSnapshot? launch = null)
    {
        // Keep the context on this process, since independent probes can run concurrently.
        Func<bool, string, Task>? updateFunc = _updateFunc;
        if (!string.IsNullOrEmpty(logPrefix))
        {
            var sink = updateFunc;
            updateFunc = (notify, message) => sink?.Invoke(notify,
                string.Join(Environment.NewLine, message.ReplaceLineEndings("\n").Split('\n')
                    .Select(line => line.Length == 0 ? line : logPrefix + line))) ?? Task.CompletedTask;
        }

        var (fileName, msg) = launch == null ? _resolveExecutable(coreInfo) : (launch.Executable, "");
        if (fileName.IsNullOrEmpty())
        {
            if (mayNeedSudo) LastError = msg;
            if (updateFunc != null) await updateFunc(false, msg);
            return null;
        }

        ProcessService? process = null;
        void Track(ProcessService value)
        {
            process = value;
            value.CoreType = coreInfo.CoreType;
            value.ConnectionOperationId = launch?.OperationId;
            value.ListenerPlan = launch?.Listeners;
            value.Started += p => ProcessStarted?.Invoke(p);
            if (!mayNeedSudo) value.TemporaryConfigPath = Utils.GetBinConfigPath(configPath);
            onCreated?.Invoke(value);
        }
        try
        {
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var plan = launch?.Listeners ?? CoreListenerPlan.Read(await File.ReadAllTextAsync(Utils.GetBinConfigPath(configPath), token), coreInfo.CoreType);
            var listeners = plan.Ports;
            var interfacesBefore = plan.Tuns.Count == 0 ? new HashSet<string>() : CoreListenerPlan.ExistingInterfaces().Except(_releasedTunInterfaces).ToHashSet();
            if (CoreListenerCheck.Supports(coreInfo.CoreType))
                await CoreListenerCheck.EnsureAvailableAsync(listeners,
                    mayNeedSudo ? GetElevatedListenerProbe(isTunLaunch, coreInfo.CoreType) : null, startupTimeout.Token);
            startupTimeout.Token.ThrowIfCancellationRequested();
            if (AppManager.Instance.IsExiting) throw new OperationCanceledException("Application is exiting.");
            if (mayNeedSudo
                && (launch?.Elevated ?? ShouldRunAsSudo(isTunLaunch, coreInfo.CoreType, Utils.IsNonWindows())))
            {
                await CoreAdminManager.Instance.Init(_config, _updateFunc);
                process = await CoreAdminManager.Instance.RunProcessAsLinuxSudo(fileName, coreInfo, configPath, Track, startupTimeout.Token);
            }
            else process = await RunProcessNormal(fileName, coreInfo, configPath, displayLog || CoreListenerCheck.Supports(coreInfo.CoreType),
                displayLog ? updateFunc : null, Track, startupTimeout.Token);
            process.ListenerPlan = plan;
            await process.WaitForIdentityAsync(startupTimeout.Token);
            if (CoreListenerCheck.Supports(coreInfo.CoreType))
            {
                await CoreListenerCheck.WaitForReadyAsync(process, listeners, startupTimeout.Token,
                    _ => Task.FromResult(plan.TunReady(interfacesBefore)));
                if (plan.Tuns.Count > 0)
                    process.ReadyTunInterfaces = plan.MatchingInterfaces().Except(interfacesBefore).ToHashSet();
            }
            else
            {
                await Task.Delay(100, startupTimeout.Token);
                if (await process.HasExitedAsync(startupTimeout.Token)) throw new IOException(ResUI.FailedToRunCore + " " + process.RecentError);
                process.IsReady = true;
            }
            Logging.SaveLog($"CoreLifecycle launch={process.LaunchId} ready core={process.CoreIdentity?.Pid} born={process.CoreIdentity?.StartTime} executable={process.ExpectedExecutable}");
            return process;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            var error = token.IsCancellationRequested ? "操作已取消 / Operation cancelled."
                : ex is OperationCanceledException
                ? "内核启动超时 / Core startup timed out. " + process?.RecentError : ex.Message;
            if (mayNeedSudo) LastError = error;
            if (onCreated == null && process != null)
            {
                var stopped = await process.StopAndWaitAsync();
                if (stopped.Success) process.Dispose();
            }
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (updateFunc != null) await updateFunc(mayNeedSudo, error);
            return null;
        }
    }

    private async Task<ProcessService> RunProcessNormal(string fileName, CoreInfo? coreInfo, string configPath, bool displayLog, Func<bool, string, Task>? updateFunc,
        Action<ProcessService> onCreated, CancellationToken token)
    {
        var environmentVars = new Dictionary<string, string>();
        foreach (var kv in coreInfo.Environment)
        {
            environmentVars[kv.Key] = string.Format(kv.Value, coreInfo.AbsolutePath ? Utils.GetBinConfigPath(configPath).AppendQuotes() : configPath);
        }

        var procService = new ProcessService(
            fileName: fileName,
            arguments: string.Format(coreInfo.Arguments, coreInfo.AbsolutePath ? Utils.GetBinConfigPath(configPath).AppendQuotes() : configPath),
            workingDirectory: Utils.GetBinConfigPath(),
            displayLog: displayLog,
            redirectInput: false,
            environmentVars: environmentVars,
            updateFunc: updateFunc
        ) { ConfigPath = Utils.GetBinConfigPath(configPath) };

        onCreated(procService);
        await procService.StartAsync(token: token);
        AddProcessJob(procService.Handle);

        return procService;
    }

    private void AddProcessJob(nint processHandle)
    {
        if (Utils.IsWindows())
        {
            _processJob ??= new();
            try
            {
                _processJob?.AddProcess(processHandle);
            }
            catch { }
        }
    }

    #endregion Process
}
