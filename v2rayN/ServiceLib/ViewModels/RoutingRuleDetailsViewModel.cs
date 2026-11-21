namespace ServiceLib.ViewModels;

public sealed class RoutingProtocolOption(string name, bool isSupported, List<string> selection, Action selectionChanged) : ReactiveObject
{
    public string Name { get; } = name;
    public bool IsSupported { get; } = isSupported;
    public bool IsSelected
    {
        get => selection.Contains(Name);
        set
        {
            if (!IsSupported || value == IsSelected) return;
            if (value) selection.Add(Name);
            else selection.RemoveAll(protocol => protocol == Name);
            this.RaisePropertyChanged(nameof(IsSelected));
            selectionChanged();
        }
    }
}

public partial class RoutingRuleDetailsViewModel : MyReactiveObject, ICloseable
{
    public event EventHandler? RequestClose;

    private readonly List<string> _protocolItems;
    public IReadOnlyList<string> ProtocolItems => _protocolItems;
    public IList<string> InboundTagItems { get; set; }

    public ECoreType ProtocolCoreType { get; }
    public IReadOnlyList<RoutingProtocolOption> ProtocolOptions { get; }
    public string ProtocolSupportText => RoutingProtocolPolicy.Describe(ProtocolCoreType,
        Enabled && RuleType != nameof(ERuleType.DNS) ? ProtocolItems : [],
        _config.Inbound?.FirstOrDefault()?.SniffingEnabled == true);

    [Reactive]
    public partial RulesItem SelectedSource { get; set; }

    [Reactive]
    public partial string Domain { get; set; }

    [Reactive]
    public partial string IP { get; set; }

    [Reactive]
    public partial string Process { get; set; }

    [Reactive]
    public partial string? RuleType { get; set; }

    [Reactive]
    public partial bool AutoSort { get; set; }

    [Reactive]
    public partial string OutboundTag { get; set; }

    [Reactive]
    public partial string Remarks { get; set; }

    [Reactive]
    public partial string Port { get; set; }

    [Reactive]
    public partial string Network { get; set; }

    [Reactive]
    public partial bool Enabled { get; set; }

    private RouteOutboundTarget? _target;
    private string? _targetDisplay;
    public ReactiveCommand<RxVoid, RxVoid> SelectProfileCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> SaveCmd { get; }

    public RoutingRuleDetailsViewModel(RulesItem rulesItem, ECoreType coreType = ECoreType.Xray, string? subscriptionName = null)
    {
        _config = AppManager.Instance.Config;
        ProtocolCoreType = coreType;
        _protocolItems = rulesItem.Protocol?.ToList() ?? [];
        var supported = RoutingProtocolPolicy.Supported(coreType);
        ProtocolOptions = RoutingProtocolPolicy.EditorOptions(_protocolItems)
            .Select(name => new RoutingProtocolOption(name, supported.Contains(name), _protocolItems,
                () => this.RaisePropertyChanged(nameof(ProtocolSupportText))))
            .ToArray();
        InboundTagItems = rulesItem.InboundTag?.ToList() ?? [];
        this.WhenAnyValue(vm => vm.RuleType, vm => vm.Enabled)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ProtocolSupportText)));

        SelectProfileCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SelectProfileAsync();
        });
        SaveCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SaveRulesAsync();
        });

        if (rulesItem.Id.IsNullOrEmpty())
        {
            rulesItem.Id = Utils.GetGuid(false);
            rulesItem.OutboundTag = Global.ProxyTag;
            rulesItem.Enabled = true;
            SelectedSource = rulesItem;
        }
        else
        {
            SelectedSource = rulesItem;
        }

        Domain = Utils.List2String(SelectedSource.Domain, true);
        IP = Utils.List2String(SelectedSource.Ip, true);
        Process = Utils.List2String(SelectedSource.Process, true);
        RuleType = SelectedSource.RuleType?.ToString();
        _target = SelectedSource.OutboundTarget;
        _targetDisplay = _target == null ? null : RouteTargetPolicy.Summary(_target, subscriptionName);
        OutboundTag = _targetDisplay ?? SelectedSource.OutboundTag;
        Remarks = SelectedSource.Remarks;
        Port = SelectedSource.Port;
        Network = SelectedSource.Network;
        Enabled = SelectedSource.Enabled;
    }

    private async Task SaveRulesAsync()
    {
        var ruleType = RuleType.IsNullOrEmpty() ? (ERuleType?)null : Enum.Parse<ERuleType>(RuleType);
        Domain = Utils.Convert2Comma(Domain);
        IP = Utils.Convert2Comma(IP);
        Process = Utils.ParseProcess(Process);

        var domains = AutoSort ? Utils.String2ListSorted(Domain) : Utils.String2List(Domain);
        var ips = AutoSort ? Utils.String2ListSorted(IP) : Utils.String2List(IP);
        var processes = AutoSort ? Utils.String2ListSorted(Process) : Utils.String2List(Process);
        var hasRule = domains?.Count > 0 || ips?.Count > 0 || ProtocolItems.Count > 0
            || processes?.Count > 0 || Port.IsNotEmpty() || Network.IsNotEmpty();
        if (!hasRule)
        {
            NoticeManager.Instance.Enqueue(string.Format(ResUI.RoutingRuleDetailRequiredTips, "Network/Port/Protocol/Domain/IP/Process"));
            return;
        }

        SelectedSource.Domain = domains;
        SelectedSource.Ip = ips;
        SelectedSource.Process = processes;
        SelectedSource.Protocol = ProtocolItems.ToList();
        SelectedSource.InboundTag = InboundTagItems?.ToList();
        SelectedSource.RuleType = ruleType;
        SelectedSource.OutboundTarget = OutboundTag == _targetDisplay ? _target : null;
        SelectedSource.OutboundTag = SelectedSource.OutboundTarget != null ? Global.BlockTag : OutboundTag;
        SelectedSource.Remarks = Remarks;
        SelectedSource.Port = Port;
        SelectedSource.Network = Network;
        SelectedSource.Enabled = Enabled;

        RequestClose?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask;
    }

    private async Task SelectProfileAsync()
    {
        var profileSelectViewModel = new ProfilesSelectViewModel(ProtocolCoreType, OutboundTag == _targetDisplay ? _target : null, OutboundTag);
        var result = await AppManager.Instance.WindowDialog.ShowDialogAsync(profileSelectViewModel);
        if (result != true)
        {
            return;
        }
        _target = profileSelectViewModel.RouteSelection();
        _targetDisplay = RouteTargetPolicy.Summary(_target);
        OutboundTag = _targetDisplay;
    }
}
