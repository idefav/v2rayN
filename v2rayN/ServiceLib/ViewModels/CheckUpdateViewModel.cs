namespace ServiceLib.ViewModels;

public partial class CheckUpdateViewModel : MyReactiveObject
{
    private const string _geo = "GeoFiles";
    private readonly ECoreType _v2rayN = ECoreType.v2rayN;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private static readonly string _tag = "CheckUpdateViewModel";

    public EventChannel<RxVoid> ReloadRequested { get; } = new();
    public EventChannel<RxVoid> SourceSettingsRequested { get; } = new();
    public Func<string, Task<bool>>? RestartCoreAsync { get; set; }

    public BulkObservableCollection<CheckUpdateModel> CheckUpdateModels { get; } = [];
    public ReactiveCommand<RxVoid, RxVoid> CheckUpdateCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> CheckOnlyCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> SourceSettingsCmd { get; }

    [Reactive] public partial bool EnableUpdateViaProxy { get; set; }

    public CheckUpdateViewModel()
    {
        _config = AppManager.Instance.Config;
        SourceSettingsCmd = ReactiveCommand.Create(() => SourceSettingsRequested.Publish());

        CheckUpdateCmd = ReactiveCommand.CreateFromTask(CheckUpdate);
        CheckUpdateCmd.ThrownExceptions.Subscribe(ex =>
        {
            Logging.SaveLog(_tag, ex);
            _ = UpdateView(_v2rayN, ex.Message);
        });

        CheckOnlyCmd = ReactiveCommand.CreateFromTask(CheckOnly);
        CheckOnlyCmd.ThrownExceptions.Subscribe(ex =>
        {
            Logging.SaveLog(_tag, ex);
            _ = UpdateView(_v2rayN, ex.Message);
        });

        EnableUpdateViaProxy = _config.CheckUpdateItem.UpdateViaProxy;

        this.WhenAnyValue(x => x.EnableUpdateViaProxy)
            .Subscribe(c => _ = OnUpdateViaProxyChanged());

        AppEvents.HasUpdateNotified
         .AsObservable()
         .ObserveOn(RxSchedulers.MainThreadScheduler)
         .Subscribe(bl => RefreshCheckUpdateItems(bl));

        AppEvents.UpdateSourcesChanged.AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshCheckUpdateItems(true));

        RefreshCheckUpdateItems(true);
    }

    private void RefreshCheckUpdateItems(bool hasUpdate)
    {
        if (!hasUpdate)
        {
            return;
        }

        var models = CoreInfoManager.Instance.GetCheckUpdateCoreTypes()
                        .Select(t => GetCheckUpdateModel(t))
                        .ToList();

        models.Add(GetGeoFileCheckUpdateModel());

        CheckUpdateModels.ReplaceRange(models);
        foreach (var model in models.Where(m => m.CoreType != null)) _ = RefreshVersionAsync(model);
    }

    private CheckUpdateModel GetCheckUpdateModel(ECoreType coreType)
    {
        if (coreType == _v2rayN && Utils.IsPackagedInstall())
        {
            return new()
            {
                IsSelected = false,
                IsCheckPreRelease = false,
                ShowCheckPreRelease = false,
                CoreType = coreType,
                IsGeoFile = false,
                Remarks = ResUI.menuCheckUpdate + $" ({ResUI.MsgNotSupport})",
            };
        }

        AppManager.Instance.LastCheckUpdateResults.TryGetValue(coreType, out var lastResult);
        return new()
        {
            IsSelected = _config.CheckUpdateItem.SelectedCoreTypes?.Contains(coreType.ToString()) ?? true,
            IsCheckPreRelease = _config.CheckUpdateItem.CheckPreReleaseCoreTypes?.Contains(coreType.ToString()) ?? false,
            ShowCheckPreRelease = CoreInfoManager.Instance.GetCheckPreRelease(coreType, true),
            CoreType = coreType,
            IsGeoFile = false,
            Remarks = lastResult ?? ResUI.menuCheckUpdate,
            Source = GetSource(coreType),
            InstalledSource = InstalledUpdateStore.Repository(coreType) ?? UpdateSourceText.Unknown,
        };
    }

    private CheckUpdateModel GetGeoFileCheckUpdateModel()
    {
        return new()
        {
            IsSelected = _config.CheckUpdateItem.SelectedCoreTypes?.Contains(_geo) ?? true,
            IsCheckPreRelease = false,
            ShowCheckPreRelease = false,
            CoreType = null,
            IsGeoFile = true,
            Remarks = ResUI.menuCheckUpdate,
        };
    }

    private async Task OnUpdateViaProxyChanged()
    {
        if (_config.CheckUpdateItem.UpdateViaProxy == EnableUpdateViaProxy)
        {
            return;
        }
        _config.CheckUpdateItem.UpdateViaProxy = EnableUpdateViaProxy;
        await SaveSelectedCoreTypes();
    }

    private async Task SaveSelectedCoreTypes()
    {
        _config.CheckUpdateItem.SelectedCoreTypes =
            CheckUpdateModels.Where(t => t.IsSelected == true)
                            .Select(t => t.CoreTypeForStorage)
                            .ToList();

        _config.CheckUpdateItem.CheckPreReleaseCoreTypes =
           CheckUpdateModels.Where(t => t.IsCheckPreRelease == true)
                           .Select(t => t.CoreTypeForStorage)
                           .ToList();

        await ConfigHandler.SaveConfig(_config);
    }

    private async Task CheckOnly()
    {
        if (!await _operation.WaitAsync(0)) return;
        try { await Task.Run(CheckOnlyTask); }
        finally { _operation.Release(); }
    }

    private async Task CheckUpdate()
    {
        if (!await _operation.WaitAsync(0)) return;
        try { await Task.Run(CheckUpdateTask); }
        finally { _operation.Release(); }
    }

    private async Task CheckOnlyTask()
    {
        await SaveSelectedCoreTypes();
        var snapshot = JsonUtils.Deserialize<Config>(JsonSerializer.Serialize(_config))!;
        foreach (var item in CheckUpdateModels.Reverse().ToArray())
        {
            if (item.IsSelected != true)
            {
                continue;
            }

            await UpdateView(item.CoreType, "...");

            if (item.IsGeoFile || item.CoreType == null)
            {
                await UpdateView(item.CoreType, ResUI.menuCheckOnly + $" ({ResUI.MsgNotSupport})");
                continue;
            }

            var updateService = new UpdateService(snapshot, (_, _) => Task.CompletedTask);
            var result = await updateService.CheckHasUpdateOnly(item.CoreType.Value, item.IsCheckPreRelease, snapshot.CheckUpdateItem.UpdateViaProxy);
            if (result.Success && result.Version != null)
            {
                await UpdateView(item.CoreType, result.Msg ?? string.Format(ResUI.MsgCheckUpdateHasNewVersion, item.CoreType, result.Version));
            }
            else
            {
                await UpdateView(item.CoreType, result.Msg);
            }
        }
    }

    private async Task CheckUpdateTask()
    {
        await SaveSelectedCoreTypes();
        // Take a snapshot before settings changes or UI refreshes can replace the collection.
        var selected = CheckUpdateModels.Where(x => x.IsSelected == true).Reverse().ToArray();
        var snapshot = JsonUtils.Deserialize<Config>(JsonSerializer.Serialize(_config))!;
        foreach (var item in selected)
        {
            string? archive = null;
            try
            {
                await UpdateView(item.CoreType, "...");
                var service = new UpdateService(snapshot, (_, message) => UpdateView(item.CoreType, message));
                if (item.IsGeoFile)
                {
                    await service.UpdateGeoFileAll(snapshot.CheckUpdateItem.UpdateViaProxy);
                    if (CoreManager.Instance.IsCoreRunning && RestartCoreAsync != null
                        && !await RestartCoreAsync("Geo／SRS 规则更新完成"))
                        throw new InvalidOperationException("Rules were downloaded, but the core could not reload");
                    continue;
                }
                if (item.CoreType is not { } type) continue;
                var result = await service.DownloadUpdateAsync(type, item.IsCheckPreRelease, snapshot.CheckUpdateItem.UpdateViaProxy);
                if (!result.Success) { await UpdateView(type, result.Msg ?? ResUI.OperationFailed); continue; }
                archive = result.FileName!;
                if (type == ECoreType.v2rayN)
                {
                    if (!Utils.UpgradeAppExists(out var helper)) throw new FileNotFoundException(ResUI.UpgradeAppNotExistTip);
                    AppUpdateProvenance.Stage(result, archive);
                    if (ProcUtils.ProcessStart(helper, Utils.UrlEncode(archive), Utils.StartupPath()) <= 0)
                    {
                        AppUpdateProvenance.ClearPending();
                        throw new IOException("Could not start the application updater");
                    }
                    archive = null; // The upgrade assistant owns the archive until the application exits.
                    await AppManager.Instance.AppExitAsync(true);
                    return;
                }
                if (RestartCoreAsync == null) throw new InvalidOperationException("Core restart is unavailable");
                await new CoreUpdateInstaller().InstallAsync(type, result, archive, _config, RestartCoreAsync);
                await UpdateView(type, $"{ResUI.MsgUpdateV2rayCoreSuccessfully} ({result.Repository} {result.Tag})");
            }
            catch (Exception ex)
            {
                if (item.CoreType == ECoreType.v2rayN) AppUpdateProvenance.ClearPending();
                Logging.SaveLog(_tag, ex);
                await UpdateView(item.CoreType, $"{UpdateSourceText.Failed}: {ex.Message}");
            }
            finally { if (archive != null) File.Delete(archive); }
        }
    }

    private string GetSource(ECoreType type)
    {
        try { return UpdateSources.Repository(_config, type); }
        catch (ArgumentException ex) { return ex.Message; }
    }

    private async Task UpdateView(ECoreType? coreType, string msg)
    {
        var item = new CheckUpdateModel()
        {
            CoreType = coreType,
            IsGeoFile = coreType == null,
            Remarks = msg,
        };

        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            _ = UpdateViewResult(item);
        });
        await Task.CompletedTask;
    }

    public async Task UpdateViewResult(CheckUpdateModel model)
    {
        var found = CheckUpdateModels.FirstOrDefault(t => t.CoreType == model.CoreType && t.IsGeoFile == model.IsGeoFile);
        if (found == null)
        {
            return;
        }
        found.Remarks = model.Remarks;
        if (found.CoreType is { } type)
        {
            found.Source = GetSource(type);
            found.InstalledSource = InstalledUpdateStore.Repository(type) ?? UpdateSourceText.Unknown;
            await RefreshVersionAsync(found);
        }
        await Task.CompletedTask;
    }

    private async Task RefreshVersionAsync(CheckUpdateModel model)
    {
        if (model.CoreType is not { } type) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var version = type == ECoreType.v2rayN ? AppUpdateProvenance.CurrentVersion()
            : (await new UpdateService(_config, (_, _) => Task.CompletedTask).GetCoreVersion(type, timeout.Token)).ToString();
        RxSchedulers.MainThreadScheduler.Schedule(() => model.CurrentVersion = version == "0.0.0" ? "—" : version);
    }
}
