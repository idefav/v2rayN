namespace ServiceLib.ViewModels;

public partial class MainWindowViewModel : MyReactiveObject
{
    public Interaction<RxVoid, string?> ReadTextFromClipboardInteraction { get; } = new();
    public Interaction<RxVoid, byte[]?> ScanScreenInteraction { get; } = new();
    public Interaction<RxVoid, string?> BrowseImageFileInteraction { get; } = new();
    public Interaction<bool?, RxVoid> ShowHideWindowInteraction { get; } = new();

    public bool DesignMode { get; set; }

    public ProfilesViewModel ProfilesViewModel { get; } = new();
    public MsgViewModel MsgViewModel { get; } = new();
    public ClashProxiesViewModel ClashProxiesViewModel { get; } = new();
    public ClashConnectionsViewModel ClashConnectionsViewModel { get; } = new();
    public CheckUpdateViewModel CheckUpdateViewModel { get; } = new();
    public BackupAndRestoreViewModel BackupAndRestoreViewModel { get; } = new();
    public StatusBarViewModel StatusBarViewModel { get; } = StatusBarViewModel.Instance;

    #region Menu

    //servers
    public ReactiveCommand<RxVoid, RxVoid> AddVmessServerCmd { get; }

    public ReactiveCommand<RxVoid, RxVoid> AddVlessServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddShadowsocksServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddSocksServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddHttpServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddTrojanServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddHysteria2ServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddTuicServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddWireguardServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddAnytlsServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddNaiveServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddSshServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddMasqueServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddCustomServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddCustomOutboundServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddPolicyGroupServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddProxyChainServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddServerViaClipboardCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddServerViaScanCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddServerViaImageCmd { get; }

    //Subscription
    public ReactiveCommand<RxVoid, RxVoid> SubSettingCmd { get; }

    public ReactiveCommand<RxVoid, RxVoid> SubUpdateCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> SubUpdateViaProxyCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> SubGroupUpdateCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> SubGroupUpdateViaProxyCmd { get; }

    //Setting
    public ReactiveCommand<RxVoid, RxVoid> OptionSettingCmd { get; }

    public ReactiveCommand<RxVoid, RxVoid> RoutingSettingCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> DNSSettingCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> FullConfigTemplateCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> GlobalHotkeySettingCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> RebootAsAdminCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearServerStatisticsCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenTheFileLocationCmd { get; }

    //Presets
    public ReactiveCommand<RxVoid, RxVoid> RegionalPresetDefaultCmd { get; }

    public ReactiveCommand<RxVoid, RxVoid> RegionalPresetRussiaCmd { get; }

    public ReactiveCommand<RxVoid, RxVoid> RegionalPresetIranCmd { get; }

    public ReactiveCommand<RxVoid, RxVoid> ReloadCmd { get; }

    [Reactive]
    public partial bool BlReloadEnabled { get; set; }

    [Reactive]
    public partial bool ShowClashUI { get; set; }

    [Reactive]
    public partial int TabMainSelectedIndex { get; set; }

    [Reactive] public partial bool BlIsWindows { get; set; }

    [Reactive] public partial bool BlNewUpdate { get; set; }

    [Reactive] public partial EGirdOrientation MainGirdOrientation { get; set; }

    #endregion Menu

    #region Init

    public MainWindowViewModel(bool designMode = false)
    {
        DesignMode = designMode;
        _config = AppManager.Instance.Config;
        BlIsWindows = Utils.IsWindows();
        MainGirdOrientation = _config.UiItem.MainGirdOrientation;

        #region WhenAnyValue && ReactiveCommand

        //servers
        AddVmessServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.VMess);
        });
        AddVlessServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.VLESS);
        });
        AddShadowsocksServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Shadowsocks);
        });
        AddSocksServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.SOCKS);
        });
        AddHttpServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.HTTP);
        });
        AddTrojanServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Trojan);
        });
        AddHysteria2ServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Hysteria2);
        });
        AddTuicServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.TUIC);
        });
        AddWireguardServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.WireGuard);
        });
        AddAnytlsServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Anytls);
        });
        AddNaiveServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Naive);
        });
        AddSshServerCmd = ReactiveCommand.CreateFromTask(() => AddServerAsync(EConfigType.SSH));
        AddMasqueServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.MASQUE);
        });
        AddCustomServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Custom);
        });
        AddCustomOutboundServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Outbound);
        });
        AddPolicyGroupServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.PolicyGroup);
        });
        AddProxyChainServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.ProxyChain);
        });
        AddServerViaClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerViaClipboardAsync(null);
        });
        AddServerViaScanCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerViaScanAsync();
        });
        AddServerViaImageCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerViaImageAsync();
        });

        //Subscription
        SubSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SubSettingAsync();
        });

        SubUpdateCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess("", false);
        });
        SubUpdateViaProxyCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess("", true);
        });
        SubGroupUpdateCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess(_config.SubIndexId, false);
        });
        SubGroupUpdateViaProxyCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess(_config.SubIndexId, true);
        });

        //Setting
        OptionSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await OptionSettingAsync();
        });
        RoutingSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingSettingAsync();
        });
        DNSSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await DNSSettingAsync();
        });
        FullConfigTemplateCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await FullConfigTemplateAsync();
        });
        GlobalHotkeySettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var globalHotkeySettingViewModel = new GlobalHotkeySettingViewModel();
            if (await AppManager.Instance.WindowDialog.ShowDialogAsync(globalHotkeySettingViewModel) == true)
            {
                NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
            }
        });
        RebootAsAdminCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AppManager.Instance.RebootAsAdmin();
        });
        ClearServerStatisticsCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ClearServerStatistics();
        });
        OpenTheFileLocationCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await OpenTheFileLocation();
        });

        ReloadCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await Reload("用户手动重新加载");
        });

        RegionalPresetDefaultCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ApplyRegionalPreset(EPresetType.Default);
        });

        RegionalPresetRussiaCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ApplyRegionalPreset(EPresetType.Russia);
        });

        RegionalPresetIranCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ApplyRegionalPreset(EPresetType.Iran);
        });

        #endregion WhenAnyValue && ReactiveCommand

        #region AppEvents

        AppEvents.AddServerViaClipboardRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .SubscribeAsync(async _ => await AddServerViaClipboardAsync(null));

        AppEvents.HasUpdateNotified
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(bl => BlNewUpdate = bl);

        #endregion AppEvents

        ProfilesViewModel.RefreshServersRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .SubscribeAsync(async _ => await RefreshServersDispatcherAsync());

        var vmReloadRequestedList = new List<IObservable<RxVoid>>
        {
            ProfilesViewModel.ReloadRequested.AsObservable(),
            StatusBarViewModel.ReloadRequested.AsObservable(),
            CheckUpdateViewModel.ReloadRequested.AsObservable(),
        };

        CheckUpdateViewModel.PrepareCoreRestartAsync = PrepareUpdateRestartAsync;

        foreach (var reloadRequested in vmReloadRequestedList)
        {
            reloadRequested
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .SubscribeAsync(async _ => await ReloadSelectedConfigurationAsync());
        }

        StatusBarViewModel.AddServerViaScanRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .SubscribeAsync(async _ => await AddServerViaScanAsync());

        StatusBarViewModel.AddServerViaClipboardRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .SubscribeAsync(async _ => await AddServerViaClipboardAsync(null));

        StatusBarViewModel.ShowHideWindowRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .SubscribeAsync(async blShow =>
            {
                await ShowHideWindowInteraction.HandleSafe(blShow);
            });

        StatusBarViewModel.SetDefaultServerRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .SubscribeAsync(async indexId => await ProfilesViewModel.SetDefaultServer(indexId));

        StatusBarViewModel.SubscriptionsUpdateRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .SubscribeAsync(async blProxy => await UpdateSubscriptionProcess("", blProxy));

        AppManager.Instance.WaitForCoreReloadAsync = _reloadCoordinator.WaitForIdleAsync;
        Initialization = Init();
    }

    internal Task Initialization { get; }

    private async Task Init()
    {
        AppManager.Instance.ShowInTaskbar = true;

        if (DesignMode)
        {
            return;
        }

        //await ConfigHandler.InitBuiltinRouting(_config);
        await ConfigHandler.InitBuiltinDNS(_config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(_config);
        await ProfileExManager.Instance.Init();
        await CoreManager.Instance.Init(_config, UpdateHandler);
        await CertPemManager.Instance.Init(_config);
        TaskManager.Instance.RegUpdateTask(_config, UpdateSubscriptionTaskHandler);

        if (_config.GuiItem.EnableStatistics || _config.GuiItem.DisplayRealTimeSpeed)
        {
            await StatisticsManager.Instance.Init(_config, UpdateStatisticsHandler);
        }
        await RefreshServersDispatcherAsync();

        await Reload("应用启动");
    }

    #endregion Init

    #region Actions

    private async Task UpdateHandler(bool notify, string msg)
    {
        NoticeManager.Instance.SendMessage(msg);
        if (notify)
        {
            NoticeManager.Instance.Enqueue(msg);
        }
        await Task.CompletedTask;
    }

    private Task UpdateSubscriptionTaskHandler(bool success, string msg) => UpdateTaskHandler(success, msg, true);

    private Task UpdateTaskHandler(bool success, string msg) => UpdateTaskHandler(success, msg, false);

    private async Task UpdateTaskHandler(bool success, string msg, bool subscriptionUpdate)
    {
        NoticeManager.Instance.SendMessageEx(msg);
        if (success)
        {
            await RefreshServersDispatcherAsync();
            // Compare the whole effective config, including chains and routing-referenced nodes,
            // rather than using the selected subscription group as a reload heuristic.
            await ReloadSelectedConfigurationAsync(subscriptionUpdate ? "订阅更新完成" : "资源更新完成", onlyIfChanged: subscriptionUpdate);

            if (_config.UiItem.EnableAutoAdjustMainLvColWidth)
            {
                await ProfilesViewModel.AdjustMainLvColWidth();
            }
        }
    }

    private async Task UpdateStatisticsHandler(ServerSpeedItem update)
    {
        if (!AppManager.Instance.ShowInTaskbar)
        {
            return;
        }
        AppEvents.DispatcherStatisticsRequested.Publish(update);
        await Task.CompletedTask;
    }

    public async Task SetRealTimeSpeedAsync(bool enabled)
    {
        if (DesignMode || AppManager.Instance.IsExiting) return;
        var previous = _config.GuiItem.DisplayRealTimeSpeed;
        _config.GuiItem.DisplayRealTimeSpeed = enabled;
        if (await ConfigHandler.SaveConfig(_config) != 0)
        {
            _config.GuiItem.DisplayRealTimeSpeed = previous;
            throw new IOException("实时速度设置保存失败 / Could not save live speed setting.");
        }
        if (enabled) await StatisticsManager.Instance.Init(_config, UpdateStatisticsHandler);
    }

    #endregion Actions

    #region Servers && Groups

    private readonly SemaphoreSlim _refreshServersSemaphore = new(1, 1);

    private async Task RefreshServers()
    {
        await ProfilesViewModel.RefreshServersBiz();
        await StatusBarViewModel.RefreshServersBiz();

        // await Task.Delay(200);
    }

    private async Task RefreshServersDispatcherAsync()
    {
        //await Observable.Start(async () => await RefreshServers(), RxSchedulers.MainThreadScheduler);
        await _refreshServersSemaphore.WaitAsync();
        try
        {
            await Signal.FromAsync(async () =>
            {
                await RefreshServers();
                return RxVoid.Default;
            })
                .SubscribeOn(RxSchedulers.MainThreadScheduler)
                .ToTask();
        }
        finally
        {
            _refreshServersSemaphore.Release();
        }
    }

    private async Task RefreshSubscriptions()
    {
        //await Observable.Start(async () => await ProfilesViewModel.RefreshSubscriptions(), RxSchedulers.MainThreadScheduler);
        await Signal.FromAsync(async () =>
            {
                await ProfilesViewModel.RefreshSubscriptions();
                return RxVoid.Default;
            })
            .SubscribeOn(RxSchedulers.MainThreadScheduler)
            .ToTask();
    }

    #endregion Servers && Groups

    #region Add Servers

    public async Task AddServerAsync(EConfigType eConfigType)
    {
        ProfileItem item = new()
        {
            Subid = _config.SubIndexId,
            ConfigType = eConfigType,
            IsSub = false,
        };

        bool? ret = false;
        if (eConfigType == EConfigType.SSH)
        {
            ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(new AddSshServerViewModel(item));
        }
        else if (eConfigType is EConfigType.Custom or EConfigType.Outbound)
        {
            var addServer2ViewModel = new AddServer2ViewModel(item);
            ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(addServer2ViewModel);
        }
        else if (eConfigType.IsGroupType())
        {
            var addGroupServerViewModel = new AddGroupServerViewModel(item);
            ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(addGroupServerViewModel);
        }
        else
        {
            var addServerViewModel = new AddServerViewModel(item);
            ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(addServerViewModel);
        }
        if (ret == true)
        {
            await RefreshServersDispatcherAsync();
            if (item.IndexId == _config.IndexId)
            {
                await ReloadSelectedConfigurationAsync();
            }
        }
    }

    public async Task AddServerViaClipboardAsync(string? clipboardData)
    {
        var stringData = clipboardData;
        if (clipboardData == null)
        {
            var result = await ReadTextFromClipboardInteraction.HandleSafe(RxVoid.Default);
            if (result.IsNullOrEmpty())
            {
                NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
                return;
            }
            stringData = result;
        }
        var ret = await ConfigHandler.AddBatchServers(_config, stringData, _config.SubIndexId, false);
        if (ret > 0)
        {
            await RefreshSubscriptions();
            await RefreshServersDispatcherAsync();
            NoticeManager.Instance.Enqueue(string.Format(ResUI.SuccessfullyImportedServerViaClipboard, ret));
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }

    public async Task AddServerViaScanAsync()
    {
        var result = await ScanScreenInteraction.HandleSafe(RxVoid.Default);
        await ScanScreenResult(result);
    }

    public async Task ScanScreenResult(byte[]? bytes)
    {
        var result = QRCodeUtils.ParseBarcode(bytes);
        await AddScanResultAsync(result);
    }

    public async Task AddServerViaImageAsync()
    {
        var imageFileName = await BrowseImageFileInteraction.HandleSafe(RxVoid.Default);
        await ScanImageResult(imageFileName);
    }

    public async Task ScanImageResult(string fileName)
    {
        if (fileName.IsNullOrEmpty())
        {
            return;
        }

        var result = QRCodeUtils.ParseBarcode(fileName);
        await AddScanResultAsync(result);
    }

    private async Task AddScanResultAsync(string? result)
    {
        if (result.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.NoValidQRcodeFound);
        }
        else
        {
            var ret = await ConfigHandler.AddBatchServers(_config, result, _config.SubIndexId, false);
            if (ret > 0)
            {
                await RefreshSubscriptions();
                await RefreshServersDispatcherAsync();
                NoticeManager.Instance.Enqueue(ResUI.SuccessfullyImportedServerViaScan);
            }
            else
            {
                NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            }
        }
    }

    #endregion Add Servers

    #region Subscription

    private async Task SubSettingAsync()
    {
        var subSettingViewModel = new SubSettingViewModel();
        if (await AppManager.Instance.WindowDialog.ShowDialogAsync(subSettingViewModel) == true)
        {
            await ApplySettingsChangesAsync("Subscriptions");
        }
    }

    public async Task UpdateSubscriptionProcess(string subId, bool blProxy)
    {
        await Task.Run(async () => await SubscriptionHandler.UpdateProcess(_config, subId, blProxy, UpdateSubscriptionTaskHandler));
    }

    #endregion Subscription

    #region Setting

    private async Task OptionSettingAsync()
    {
        var settingViewModel = new OptionSettingViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(settingViewModel);
        if (ret == true && !settingViewModel.OnlySourcesChanged)
        {
            await ApplySettingsChangesAsync("Options");
        }
    }

    private async Task RoutingSettingAsync()
    {
        var routingSettingViewModel = new RoutingSettingViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(routingSettingViewModel);
        if (ret == true)
        {
            await ApplySettingsChangesAsync("Routing");
        }
    }

    private async Task DNSSettingAsync()
    {
        var dnsSettingViewModel = new DNSSettingViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(dnsSettingViewModel);
        if (ret == true)
        {
            await ApplySettingsChangesAsync("DNS");
        }
    }

    private async Task FullConfigTemplateAsync()
    {
        var fullConfigTemplateViewModel = new FullConfigTemplateViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(fullConfigTemplateViewModel);
        if (ret == true)
        {
            await ApplySettingsChangesAsync("Templates");
        }
    }

    /// <summary>Desktop V3 keeps disconnected choices and saved drafts separate from explicit connection.</summary>
    public bool UseConnectionWorkspace { get; set; }

    private async Task<Func<string, Task<bool>>> PrepareUpdateRestartAsync()
    {
        var revision = Interlocked.Read(ref _connectionRevision);
        var wasRunning = !_connectionPaused && !AppManager.Instance.IsExiting
            && await CoreManager.Instance.IsCoreRunningAsync();
        var session = new CoreUpdateRestartSession(
            () => wasRunning && !_connectionPaused && !AppManager.Instance.IsExiting
                && revision == Interlocked.Read(ref _connectionRevision),
            (reason, isCurrent) => ReloadCoreAsync(null, CancellationToken.None, reason, updateIsCurrent: isCurrent),
            () => RxSchedulers.MainThreadScheduler.Schedule(() => StatusBarViewModel.HasPendingChanges = true));
        return session.RestartAsync;
    }

    internal async Task ReloadSelectedConfigurationAsync(string reason = "用户操作或配置变更", bool onlyIfChanged = false)
    {
        if (UseConnectionWorkspace && (CoreManager.Instance.HasUnconfirmedProcesses
            || _connectionPaused || _switchTask is not { IsCompleted: false } && !await CoreManager.Instance.IsCoreRunningAsync()))
        {
            StatusBarViewModel.HasPendingChanges = true;
            await StatusBarViewModel.RefreshServersBiz();
            return;
        }
        await Reload(reason, onlyIfChanged);
    }

    public async Task ApplySettingsChangesAsync(string section, bool applyConnection = true)
    {
        if (DesignMode) return;
        if (section == "Subscriptions")
        {
            await RefreshSubscriptions();
            return;
        }
        if (section == "Options")
        {
            MainGirdOrientation = _config.UiItem.MainGirdOrientation;
            await StatusBarViewModel.InboundDisplayStatus();
        }
        if (section == "Routing")
        {
            await ConfigHandler.InitBuiltinRouting(_config);
            await StatusBarViewModel.RefreshRoutingsMenu();
        }
        if (section is "Hotkeys" or "Backup") return;
        if (!applyConnection)
        {
            StatusBarViewModel.HasPendingChanges = true;
            return;
        }
        await ReloadSelectedConfigurationAsync($"设置已修改：{section}", onlyIfChanged: section == "DNS");
    }

    private async Task ClearServerStatistics()
    {
        await StatisticsManager.Instance.ClearAllServerStatistics();
        await RefreshServersDispatcherAsync();
    }

    private async Task OpenTheFileLocation()
    {
        var path = Utils.StartupPath();
        if (Utils.IsWindows())
        {
            ProcUtils.ProcessStart(path);
        }
        else if (Utils.IsLinux())
        {
            ProcUtils.ProcessStart("xdg-open", path);
        }
        else if (Utils.IsMacOS())
        {
            ProcUtils.ProcessStart("open", path);
        }
        await Task.CompletedTask;
    }

    #endregion Setting

    #region core job

    private readonly ConnectionReloadCoordinator _reloadCoordinator = new();
    private long _connectionRevision;
    private int _manualReloads;
    private volatile bool _connectionPaused;
    private Task? _disconnectTask;
    private CancellationTokenSource? _activeTransitionCancellation;

    public void CancelConnectionTransition()
    {
        _connectionPaused = true;
        Interlocked.Increment(ref _connectionRevision);
        _switchCancellation?.Cancel();
        try { _activeTransitionCancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* The transaction completed concurrently. */ }
    }

    private void ReportTransition(ConnectionTransition transition)
        => StatusBarViewModel.ReportTransition(transition);

    public Task DisconnectAsync() => _disconnectTask is { IsCompleted: false } ? _disconnectTask : _disconnectTask = DisconnectCoreAsync();

    private async Task DisconnectCoreAsync()
    {
        long operationId = 0;
        CancelConnectionTransition();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(AppManager.Instance.ExitToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _reloadCoordinator.RunAsync(() => !AppManager.Instance.IsExiting, async () =>
            {
                SetReloadEnabled(false);
                operationId = ConnectionTransition.NewOperationId();
                ReportTransition(new(ConnectionStage.Stopping, _config.TunModeItem.EnableTun, Disconnecting: true) { OperationId = operationId });
                var errors = new List<string>();
                try
                {
                    try
                    {
                        if (!await SysProxyHandler.UpdateSysProxy(_config, true, error => errors.Add(error), deadline.Token))
                            errors.Add("系统代理清理失败 / System proxy cleanup failed");
                    }
                    catch (Exception ex) { errors.Add(ex.Message); }
                    var result = await CoreManager.Instance.CoreStop(deadline.Token);
                    if (!result.Success) errors.Add(result.Error ?? ResUI.FailedToRunCore);
                    var error = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
                    StatusBarViewModel.ConnectionError = error;
                    ReportTransition(new(error == null ? ConnectionStage.Completed : ConnectionStage.Failed,
                        _config.TunModeItem.EnableTun, error, Disconnecting: true) { OperationId = operationId });
                    return error == null;
                }
                finally { SetReloadEnabled(true); }
            }, deadline.Token);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Disconnect", ex);
            StatusBarViewModel.ConnectionError = ex.Message;
            ReportTransition(new(ConnectionStage.Failed, _config.TunModeItem.EnableTun, ex.Message, Disconnecting: true)
                { OperationId = operationId == 0 ? ConnectionTransition.NewOperationId() : operationId });
        }
    }

    internal RecoveryContext? GetRecoveryContext()
    {
        if (_connectionPaused || DesignMode || AppManager.Instance.IsExiting || CoreManager.Instance.HandoffNeedsManualRetry || Volatile.Read(ref _manualReloads) > 0
            || StatusBarViewModel.IsChangingConnection || !SysProxyHandler.ChangesEnabled
            || _config.TunModeItem.EnableTun || _config.SystemProxyItem.SysProxyType != ESysProxyType.ForcedChange
            || _config.SystemProxyItem.CustomSystemProxyScriptPath.IsNotEmpty() || _config.IndexId.IsNullOrEmpty()) return null;
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        return port > 0 ? new(_config.IndexId, port, _config.SpeedTestItem.SpeedPingTestUrl, Interlocked.Read(ref _connectionRevision)) : null;
    }

    public async Task Reload(string reason = "用户操作或配置变更", bool onlyIfChanged = false)
        => await ReloadWithResultAsync(reason, onlyIfChanged);

    private CancellationTokenSource? _switchCancellation;
    private Task<bool>? _switchTask;
    private string? _switchKey;

    public Task<bool> ReloadWithResultAsync(string reason, bool onlyIfChanged = true)
    {
        _connectionPaused = false;
        var key = JsonUtils.Serialize(_config);
        if (_switchTask is { IsCompleted: false } && key == _switchKey
            && _switchCancellation?.IsCancellationRequested != true) return _switchTask;
        _switchCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(AppManager.Instance.ExitToken);
        _switchCancellation = cancellation;
        _switchKey = key;
        return _switchTask = ReloadSwitchAsync(reason, onlyIfChanged, cancellation);
    }

    private async Task<bool> ReloadSwitchAsync(string reason, bool onlyIfChanged, CancellationTokenSource cancellation)
    {
        Interlocked.Increment(ref _manualReloads);
        Interlocked.Increment(ref _connectionRevision);
        AppManager.Instance.ConnectionRecovery?.RequestCheck("configuration changed");
        try { return await ReloadCoreAsync(null, cancellation.Token, reason, onlyIfChanged); }
        catch (OperationCanceledException) { return false; }
        finally
        {
            Interlocked.Decrement(ref _manualReloads);
            AppManager.Instance.ConnectionRecovery?.RequestCheck("reload completed");
        }
    }

    internal async Task<bool> ReloadForRecoveryAsync(RecoveryRestartRequest request, CancellationToken token)
    {
        if (!await ReloadCoreAsync(request.Context, token, "自动恢复：" + request.Reason,
            recoveryConfirm: request.Confirm)) return false;
        return await RecoveryProbe.WaitForListenerAsync(request.Context.Port, token);
    }

    private async Task<bool> ReloadCoreAsync(RecoveryContext? expected, CancellationToken token,
        string reason, bool onlyIfChanged = false, Func<CancellationToken, Task<bool>>? recoveryConfirm = null,
        Func<bool>? updateIsCurrent = null)
    {
        return await _reloadCoordinator.RunAsync(
            () => !DesignMode && !AppManager.Instance.IsExiting && (expected == null || GetRecoveryContext() == expected), async () =>
        {
        if (updateIsCurrent?.Invoke() == false) return true;
        using var transitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, AppManager.Instance.ExitToken);
        token = transitionCancellation.Token;
        _activeTransitionCancellation = transitionCancellation;
        var targetTun = _config.TunModeItem.EnableTun;
        var success = false;
        var transitionStarted = false;
        long operationId = 0;
        void ReportOperation(ConnectionTransition transition) => ReportTransition(transition with
            { OperationId = operationId, IsBackground = expected != null });
        try
        {
            SetReloadEnabled(false);
            StatusBarViewModel.ConnectionError = null;
            StatusBarViewModel.HasPendingChanges = true;

            RxSchedulers.MainThreadScheduler.Schedule(() =>
            {
                if (TabMainSelectedIndex < 0)
                {
                    TabMainSelectedIndex = 0;
                }
            });
            var profileItem = await ConfigHandler.GetDefaultServer(_config);
            if (profileItem == null)
            {
                StatusBarViewModel.ConnectionError = ResUI.CheckServerSettings;
                NoticeManager.Instance.Enqueue(ResUI.CheckServerSettings);
                return false;
            }
            var authorizationRevision = Interlocked.Read(ref _connectionRevision);
            if (!await StatusBarViewModel.EnsureTunAuthorizationAsync()) return false;
            token.ThrowIfCancellationRequested();
            if (authorizationRevision != Interlocked.Read(ref _connectionRevision)) return false;
            if (AppManager.Instance.IsExiting) return false;
            transitionStarted = true;
            operationId = ConnectionTransition.NewOperationId();
            ReportOperation(new(ConnectionStage.Preparing, targetTun));
            var allResult = await CoreConfigContextBuilder.BuildAll(_config, profileItem);
            if (NoticeManager.Instance.NotifyValidatorResult(allResult.CombinedValidatorResult) && !allResult.Success)
            {
                StatusBarViewModel.ConnectionError = string.Join(Environment.NewLine, allResult.CombinedValidatorResult.Errors);
                return false;
            }

            if (expected != null)
            {
                token.ThrowIfCancellationRequested();
                if (GetRecoveryContext() != expected) return false;
                if (recoveryConfirm != null && !await recoveryConfirm(token)) return false;
                token.ThrowIfCancellationRequested();
                if (GetRecoveryContext() != expected) return false;
            }

            // Finish an initiated reload transaction before another reload or shutdown can stop it.
            var proxyApplied = false;
            var loadResult = CoreLoadResult.Failed;
            await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (updateIsCurrent?.Invoke() == false) return;
                loadResult = await CoreManager.Instance.LoadCore(allResult.MainResult.Context,
                    allResult.PreSocksResult?.Context, onlyIfChanged, reason, token,
                    stageChanged: (stage, seconds) => ReportOperation(new(stage, targetTun, WaitSeconds: seconds)));
                if (loadResult != CoreLoadResult.Started) return;
                proxyApplied = await SysProxyHandler.UpdateSysProxy(_config, false, error => StatusBarViewModel.ConnectionError = error);
                await Task.Delay(1000);
            });
            if (loadResult == CoreLoadResult.Unchanged)
            {
                StatusBarViewModel.HasPendingChanges = false;
                return success = true;
            }
            if (loadResult == CoreLoadResult.Failed)
            {
                token.ThrowIfCancellationRequested();
                if (updateIsCurrent?.Invoke() == false) return false;
                StatusBarViewModel.ConnectionError = CoreManager.Instance.LastError ?? ResUI.FailedToRunCore;
                return false;
            }
            if (expected == null) RxSchedulers.MainThreadScheduler.Schedule(async () =>
            {
                var result = await StatusBarViewModel.TestServerAvailability();
                if (result == null || profileItem.IndexId.IsNullOrEmpty())
                {
                    return;
                }

                await ProfilesViewModel.SetSpeedTestResult(new()
                {
                    IndexId = profileItem.IndexId,
                    IpInfo = result.Ip,
                    Delay = result.Time > 0 ? result.Time.ToString() : null
                });
            });

            var showClashUI = AppManager.Instance.IsRunningCore(ECoreType.sing_box);
            if (showClashUI)
            {
                //await Observable.Start(async () =>
                //{
                //    await ClashProxiesViewModel.ProxiesReload();
                //}, RxSchedulers.MainThreadScheduler);
                await Signal.FromAsync(async () =>
                    {
                        await ClashProxiesViewModel.ProxiesReload();
                        return RxVoid.Default;
                    }).SubscribeOn(RxSchedulers.MainThreadScheduler)
                    .ToTask();
            }

            ReloadResult(showClashUI);
            success = proxyApplied && await CoreManager.Instance.IsCoreRunningAsync(token);
            if (success) StatusBarViewModel.HasPendingChanges = false;
            return success;
        }
        catch (OperationCanceledException)
        {
            // Superseded recovery checks are not configuration failures.
            throw;
        }
        catch (Exception ex)
        {
            StatusBarViewModel.ConnectionError = ex.Message;
            Logging.SaveLog("Reload", ex);
            NoticeManager.Instance.Enqueue(ex.Message);
            return false;
        }
        finally
        {
            _activeTransitionCancellation = null;
            if (transitionStarted)
                ReportOperation(new(token.IsCancellationRequested ? ConnectionStage.Cancelled
                    : success ? ConnectionStage.Completed : ConnectionStage.Failed,
                    targetTun, token.IsCancellationRequested ? null : StatusBarViewModel.ConnectionError));
            SetReloadEnabled(true);
        }
        }, token);
    }

    private void ReloadResult(bool showClashUI)
    {
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            ShowClashUI = showClashUI;
            if (!showClashUI || TabMainSelectedIndex < 0)
            {
                TabMainSelectedIndex = 0;
            }
        });
    }

    private void SetReloadEnabled(bool enabled)
    {
        RxSchedulers.MainThreadScheduler.Schedule(() => BlReloadEnabled = enabled);
    }

    #endregion core job

    #region Presets

    public async Task ApplyRegionalPreset(EPresetType type)
    {
        await ConfigHandler.ApplyRegionalPreset(_config, type);
        await ConfigHandler.InitRouting(_config);
        RxSchedulers.MainThreadScheduler.Schedule(async () =>
        {
            await StatusBarViewModel.RefreshRoutingsMenu();
        });

        await ConfigHandler.SaveConfig(_config);
        await new UpdateService(_config, UpdateTaskHandler).UpdateGeoFileAll();
        await Reload();
    }

    #endregion Presets
}
