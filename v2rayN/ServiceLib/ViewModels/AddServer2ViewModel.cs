namespace ServiceLib.ViewModels;

public partial class AddServer2ViewModel : MyReactiveObject, ICloseable
{
    public event EventHandler? RequestClose;

    public Interaction<RxVoid, string?> BrowseConfigFileInteraction { get; } = new();

    [Reactive]
    public partial ProfileItem SelectedSource { get; set; }

    [Reactive]
    public partial string? CoreType { get; set; }

    [Reactive]
    public partial bool IsSingboxEndpoint { get; set; }

    [Reactive] public partial string? ConfigurationDraft { get; set; }
    [Reactive] public partial string? ConfigurationLoadError { get; set; }
    [Reactive] public partial bool IsLoadingConfiguration { get; set; }
    public bool UseConfigurationDraft { get; set; }
    private string? _loadedDraft;
    private int _readRevision;

    public async Task LoadConfigurationDraftAsync()
    {
        var revision = ++_readRevision;
        IsLoadingConfiguration = true;
        var address = SelectedSource.Address;
        string text = "";
        string? error = null;
        try
        {
            if (address.IsNotEmpty())
            {
                var path = Path.IsPathRooted(address) ? address : Utils.GetConfigPath(address);
                text = await File.ReadAllTextAsync(path);
            }
        }
        catch (Exception) { error = ResUI.FailedReadConfiguration; }
        if (revision != _readRevision) return;
        _loadedDraft = text;
        ConfigurationDraft = text;
        ConfigurationLoadError = error;
        IsLoadingConfiguration = false;
    }

    public ReactiveCommand<RxVoid, RxVoid> BrowseServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> EditServerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> SaveServerCmd { get; }
    public bool IsModified { get; set; }

    public AddServer2ViewModel(ProfileItem profileItem)
    {
        _config = AppManager.Instance.Config;

        BrowseServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var fileName = await BrowseConfigFileInteraction.HandleSafe(RxVoid.Default);
            if (fileName.IsNullOrEmpty())
            {
                return;
            }
            await BrowseServer(fileName);
        });
        EditServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await EditServer();
        });
        SaveServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SaveServerAsync();
        });

        SelectedSource = profileItem.IndexId.IsNullOrEmpty() ? profileItem : JsonUtils.DeepCopy(profileItem);
        var coreStr = SelectedSource?.CoreType?.ToString();
        coreStr = coreStr.IsNullOrEmpty() ? Global.CoreTypes.FirstOrDefault() : coreStr;
        CoreType = coreStr;
        IsSingboxEndpoint = SelectedSource?.GetProtocolExtra()?.IsSingboxEndpoint ?? false;
    }

    private async Task SaveServerAsync()
    {
        if (IsLoadingConfiguration) return;
        var remarks = SelectedSource.Remarks;
        if (remarks.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseFillRemarks);
            return;
        }

        var inlineDraft = SelectedSource.ConfigType == EConfigType.Outbound && ConfigurationDraft != null
            && (ConfigurationDraft != _loadedDraft || SelectedSource.Address.IsNullOrEmpty());
        if (inlineDraft && JsonUtils.ParseJson(ConfigurationDraft) == null)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            return;
        }
        if (!inlineDraft && SelectedSource.Address.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.FillServerAddressCustom);
            return;
        }
        SelectedSource.CoreType = CoreType.IsNullOrEmpty() ? null : Enum.Parse<ECoreType>(CoreType);
        SelectedSource.SetProtocolExtra(SelectedSource?.GetProtocolExtra() with
        {
            IsSingboxEndpoint = IsSingboxEndpoint ? true : null,
        });

        var originalAddress = SelectedSource.Address;
        string? draftFile = null;
        int result;
        try
        {
            if (inlineDraft)
            {
                draftFile = Utils.GetConfigPath(Utils.GetGuid() + ".json");
                await File.WriteAllTextAsync(draftFile, ConfigurationDraft);
                SelectedSource.Address = Path.GetFileName(draftFile);
            }
            else if (UseConfigurationDraft && Path.IsPathRooted(originalAddress))
            {
                draftFile = Utils.GetConfigPath(Utils.GetGuid() + Path.GetExtension(originalAddress));
                File.Copy(originalAddress, draftFile);
                SelectedSource.Address = Path.GetFileName(draftFile);
            }
            result = (inlineDraft || UseConfigurationDraft) && SelectedSource.IndexId.IsNullOrEmpty()
                ? await ConfigHandler.AddServerCommon(_config, SelectedSource)
                : await ConfigHandler.EditCustomServer(_config, SelectedSource);
        }
        catch (Exception) { result = -1; }
        if (result == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            SelectedSource.Address = originalAddress;
            if (draftFile != null && File.Exists(draftFile))
            {
                try { File.Delete(draftFile); } catch (IOException) { }
            }
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }

    public async Task BrowseServer(string fileName)
    {
        if (fileName.IsNullOrEmpty())
        {
            return;
        }

        if (UseConfigurationDraft)
        {
            var draft = JsonUtils.DeepCopy(SelectedSource);
            draft.Address = fileName;
            SelectedSource = draft;
            await LoadConfigurationDraftAsync();
            return;
        }
        var item = await AppManager.Instance.GetProfileItem(SelectedSource.IndexId);
        item ??= SelectedSource;
        item.Address = fileName;
        var result = item.ConfigType == EConfigType.Outbound ? await ConfigHandler.AddCustomOutboundServer(_config, item, false) : await ConfigHandler.AddCustomServer(_config, item, false);
        if (result == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.SuccessfullyImportedCustomServer);
            if (item.IndexId.IsNotEmpty())
            {
                SelectedSource = JsonUtils.DeepCopy(item);
            }
            IsModified = true;
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.FailedImportedCustomServer);
        }
    }

    private async Task EditServer()
    {
        var address = SelectedSource.Address;
        if (address.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.FillServerAddressCustom);
            return;
        }

        address = Utils.GetConfigPath(address);
        if (File.Exists(address))
        {
            ProcUtils.ProcessStart(address);
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.FailedReadConfiguration);
        }
        await Task.CompletedTask;
    }
}
