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

    public CoreManager() : this(info =>
    {
        var file = CoreInfoManager.Instance.GetCoreExecFile(info, out var message);
        return (file, message);
    }) { }

    internal CoreManager(Func<CoreInfo?, (string FileName, string Message)> resolveExecutable)
        => _resolveExecutable = resolveExecutable;

    [SupportedOSPlatform("windows")]
    private WindowsJobService? _processJob;

    private ProcessService? _processService;
    private ProcessService? _processPreService;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    public string? LastError { get; private set; }
    private readonly List<CoreListener> _elevatedHandoff = [];
    internal PortHandoffState HandoffState { get; private set; }
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
        Action<string>? progress = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, AppManager.Instance.ExitToken);
        await _lifecycleGate.WaitAsync(lifetime.Token);
        try { return await LoadCoreUnderLock(mainContext, preContext, onlyIfChanged, reason, lifetime.Token, progress); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<CoreLoadResult> LoadCoreUnderLock(CoreConfigContext? mainContext, CoreConfigContext? preContext,
        bool onlyIfChanged, string reason, CancellationToken token, Action<string>? progress)
    {
        if (AppManager.Instance.IsExiting) return CoreLoadResult.Failed;
        LastError = null;
        if (mainContext == null)
        {
            LastError = ResUI.CheckServerSettings;
            await UpdateFunc(false, ResUI.CheckServerSettings);
            return CoreLoadResult.Failed;
        }

        var node = mainContext.Node;
        // Generate candidates away from the files in use. A skipped reload must not touch them.
        var fileName = Utils.GetBinConfigPath($"candidate-{Utils.GetGuid(false)}.json");
        var preFileName = Utils.GetBinConfigPath($"candidate-pre-{Utils.GetGuid(false)}.json");
        var startingNewProcesses = false;
        try
        {
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
            if (onlyIfChanged && changeReason == null) return CoreLoadResult.Unchanged;

            await UpdateFunc(false, $"{node.GetSummary()}");
            await UpdateFunc(false, $"{Utils.GetRuntimeInfo()}");
            await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
            if (OperatingSystem.IsMacOS())
            {
                foreach (var old in new[] { _processService, _processPreService })
                    if (old?.IsElevated == true && old.CoreIdentity != null && old.ConfigPath != null)
                        _elevatedHandoff.AddRange(CoreListenerCheck.Read(await File.ReadAllTextAsync(old.ConfigPath, token)));
            }
            HandoffState = PortHandoffState.Stopping;
            var stopped = await CoreStopUnderLock();
            if (!stopped.Success) throw new IOException(stopped.Error);
            token.ThrowIfCancellationRequested();
            var handoff = new List<CoreListener>();
            if (OperatingSystem.IsMacOS() && _elevatedHandoff.Count > 0)
            {
                if (mainContext.RunCoreType == ECoreType.sing_box && !mainContext.IsTunEnabled)
                    handoff.AddRange(CoreListenerCheck.Read(await File.ReadAllTextAsync(fileName, token)));
                if (preContext?.RunCoreType == ECoreType.sing_box && !preContext.IsTunEnabled)
                    handoff.AddRange(CoreListenerCheck.Read(await File.ReadAllTextAsync(preFileName, token)));
                handoff = handoff.Where(p => _elevatedHandoff.Any(old => CoreListenerCheck.Overlaps(p, old))).Distinct().ToList();
            }
            if (handoff.Count > 0)
            {
                HandoffState = PortHandoffState.Waiting;
                await PortHandoff.WaitAsync(handoff, token, elapsed =>
                    progress?.Invoke($"已停止 TUN，正在等待普通模式端口可用（{elapsed.TotalSeconds:F0}/90 秒） / Waiting for unprivileged port handoff"));
            }
            token.ThrowIfCancellationRequested();
            HandoffState = PortHandoffState.Starting;
            if (mainContext.RunCoreType == ECoreType.sing_box)
                await CoreListenerCheck.EnsureAvailableAsync(CoreListenerCheck.Read(await File.ReadAllTextAsync(fileName)),
                    GetElevatedListenerProbe(mainContext.IsTunEnabled, mainContext.RunCoreType), token);
            if (preContext?.RunCoreType == ECoreType.sing_box)
                await CoreListenerCheck.EnsureAvailableAsync(CoreListenerCheck.Read(await File.ReadAllTextAsync(preFileName)),
                    GetElevatedListenerProbe(preContext.IsTunEnabled, preContext.RunCoreType), token);
            token.ThrowIfCancellationRequested();
            if (AppManager.Instance.IsExiting) throw new OperationCanceledException("应用正在退出，已取消启动 / Application is exiting.");
            File.Move(fileName, Utils.GetBinConfigPath(Global.CoreConfigFileName), true);
            if (preContext != null) File.Move(preFileName, Utils.GetBinConfigPath(Global.CorePreConfigFileName), true);
            _processRequirements = new(preContext != null);
            startingNewProcesses = true;

            if (Utils.IsWindows() && (mainContext.IsTunEnabled || preContext?.IsTunEnabled == true))
            {
                await Task.Delay(100);
                await WindowsUtils.RemoveTunDevice();
            }

            token.ThrowIfCancellationRequested();
            await CoreStart(mainContext);
            if (!await IsCoreRunningAsync())
                throw new IOException(LastError ?? ResUI.FailedToRunCore);
            await WaitForProxyPort(preContext);
            token.ThrowIfCancellationRequested();
            if (AppManager.Instance.IsExiting) throw new OperationCanceledException("应用正在退出，已取消启动 / Application is exiting.");
            await CoreStartPreService(preContext);

            AppManager.Instance.RunningCoreType = preContext?.RunCoreType ?? mainContext.RunCoreType;

            if (_processService != null)
            {
                await UpdateFunc(true, $"{node.GetSummary()}");
            }
            if (!await IsCoreRunningAsync() || preContext != null && !await IsRunningAsync(_processPreService))
                throw new IOException(LastError ?? ResUI.FailedToRunCore);
            token.ThrowIfCancellationRequested();
            _runningConfig = candidate;
            _elevatedHandoff.Clear();
            HandoffState = PortHandoffState.Completed;
            return CoreLoadResult.Started;
        }
        catch (Exception ex)
        {
            HandoffState = ex is OperationCanceledException ? PortHandoffState.Cancelled : PortHandoffState.Failed;
            LastError = ex.Message;
            if (startingNewProcesses)
            {
                var cleanup = await CoreStopUnderLock(clearRequirements: false);
                if (!cleanup.Success) LastError += Environment.NewLine + cleanup.Error;
            }
            Logging.SaveLog(_tag + ": " + LastError);
            if (ex is not OperationCanceledException) await UpdateFunc(true, LastError);
            return CoreLoadResult.Failed;
        }
        finally
        {
            File.Delete(fileName);
            File.Delete(preFileName);
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
        try { return await CoreStopUnderLock(token: token); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<ProcessStopResult> CoreStopUnderLock(bool clearRequirements = true, CancellationToken token = default)
    {
        _runningConfig = null;
        var results = new List<ProcessStopResult>();
        foreach (var process in new[] { _processPreService, _processService })
        {
            if (process == null) continue;
            try
            {
                var result = await process.StopAndWaitAsync(token);
                results.Add(result);
                if (!result.Success) continue;
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
        if (combined.Success && clearRequirements) _processRequirements = null;
        if (!combined.Success) Logging.SaveLog(_tag + ": " + combined.Error);
        return combined;
    }

    #region Private

    private async Task CoreStart(CoreConfigContext context)
    {
        var node = context.Node;
        var coreType = AppManager.Instance.GetCoreType(node, node.ConfigType);
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);

        var displayLog = node.ConfigType != EConfigType.Custom || node.DisplayLog;
        var proc = await RunProcess(coreInfo, Global.CoreConfigFileName, displayLog, true, context.IsTunEnabled,
            onCreated: process => _processService = process, requireSingboxReady: node.ConfigType != EConfigType.Custom);
        if (proc is null)
        {
            return;
        }
        _processService = proc;
    }

    private async Task CoreStartPreService(CoreConfigContext? preContext)
    {
        if (preContext != null && await IsCoreRunningAsync())
        {
            var preCoreType = preContext?.Node?.CoreType ?? ECoreType.sing_box;
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(preCoreType);
            var proc = await RunProcess(coreInfo, Global.CorePreConfigFileName, true, true, preContext.IsTunEnabled,
                onCreated: process => _processPreService = process);
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

    private static async Task WaitForProxyPort(CoreConfigContext? preContext)
    {
        if (preContext is null)
        {
            return;
        }
        if (!preContext.IsTunEnabled)
        {
            return;
        }

        using var rootCts = new CancellationTokenSource(Global.LocalFetch);
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

                var read = await stream.ReadAsync(buf.AsMemory(0, 2), linkedToken);

                // Server selection: VER=5, METHOD=0x00 — proxy is fully ready
                if (read == 2 && buf[0] == 0x05 && buf[1] == 0)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
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
                    throw new TimeoutException($"主内核代理端口未就绪 / Proxy port {port} did not become ready.");
                }
            }
            catch
            {
                // Ignore other exceptions and continue
            }
        }
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
        Action<ProcessService>? onCreated = null, bool requireSingboxReady = true)
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

        var (fileName, msg) = _resolveExecutable(coreInfo);
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
            if (!mayNeedSudo) value.TemporaryConfigPath = Utils.GetBinConfigPath(configPath);
            onCreated?.Invoke(value);
        }
        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var listeners = coreInfo.CoreType == ECoreType.sing_box
                ? CoreListenerCheck.Read(await File.ReadAllTextAsync(Utils.GetBinConfigPath(configPath))) : [];
            if (coreInfo.CoreType == ECoreType.sing_box)
                await CoreListenerCheck.EnsureAvailableAsync(listeners,
                    mayNeedSudo ? GetElevatedListenerProbe(isTunLaunch, coreInfo.CoreType) : null, startupTimeout.Token);
            startupTimeout.Token.ThrowIfCancellationRequested();
            if (AppManager.Instance.IsExiting) throw new OperationCanceledException("Application is exiting.");
            if (mayNeedSudo
                && ShouldRunAsSudo(isTunLaunch, coreInfo.CoreType, Utils.IsNonWindows()))
            {
                await CoreAdminManager.Instance.Init(_config, _updateFunc);
                process = await CoreAdminManager.Instance.RunProcessAsLinuxSudo(fileName, coreInfo, configPath, Track);
            }
            else process = await RunProcessNormal(fileName, coreInfo, configPath, displayLog || coreInfo.CoreType == ECoreType.sing_box,
                displayLog ? updateFunc : null, Track);
            await process.WaitForIdentityAsync(startupTimeout.Token);
            if (coreInfo.CoreType == ECoreType.sing_box && (listeners.Count > 0 || requireSingboxReady))
                await CoreListenerCheck.WaitForReadyAsync(process, listeners, startupTimeout.Token);
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
            var error = ex is OperationCanceledException
                ? "内核启动超时 / Core startup timed out. " + process?.RecentError : ex.Message;
            if (mayNeedSudo) LastError = error;
            if (onCreated == null && process != null)
            {
                var stopped = await process.StopAndWaitAsync();
                if (stopped.Success) process.Dispose();
            }
            if (updateFunc != null) await updateFunc(mayNeedSudo, error);
            return null;
        }
    }

    private async Task<ProcessService> RunProcessNormal(string fileName, CoreInfo? coreInfo, string configPath, bool displayLog, Func<bool, string, Task>? updateFunc,
        Action<ProcessService> onCreated)
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
        await procService.StartAsync();
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
