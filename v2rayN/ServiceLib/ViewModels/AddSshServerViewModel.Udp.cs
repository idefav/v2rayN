namespace ServiceLib.ViewModels;

public sealed partial class SshUdpModeChoice(string label) : MyReactiveObject
{
    public string Label { get; } = label;
    [Reactive] public partial bool Enabled { get; set; } = true;
    public override string ToString() => Label;
}

public partial class AddSshServerViewModel
{
    public SshUdpModeChoice[] UdpModes { get; } = [new(SshStrings.UdpCustom), new(SshStrings.UdpTun2Proxy)];
    [Reactive] public partial string UdpCapabilityStatus { get; set; } = SshStrings.UdpUnknown;
    [Reactive] public partial bool IsDetectingUdp { get; set; }
    [Reactive] public partial string DeploymentStatus { get; set; } = "";
    [Reactive] public partial string SudoPassword { get; set; } = "";
    public ReactiveCommand<RxVoid, RxVoid> DetectUdpCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> DeployGatewayCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> StopOperationCmd { get; private set; } = null!;
    public Interaction<string, bool> ConfirmDeploymentInteraction { get; } = new();
    private ISshUdpCapabilityService _udpCapabilityService = null!;
    private ISshGatewayDeploymentService _gatewayDeploymentService = null!;
    private CancellationTokenSource? _udpDetection;
    private long _udpRevision;
    private bool _udpActivated, _newNode;
    private int _lastUdpModeIndex;
    private SshUdpCapabilitySnapshot? _udpCapability;
    private SshUdpDeploymentRecord? _udpDeployment;

    private void InitializeUdp(ISshUdpCapabilityService? capabilityService, ISshGatewayDeploymentService? deploymentService)
    {
        _udpCapabilityService = capabilityService ?? new SshUdpCapabilityService();
        _gatewayDeploymentService = deploymentService ?? new SshGatewayDeploymentService();
        _newNode = SelectedSource.IndexId.IsNullOrEmpty();
        _lastUdpModeIndex = UdpModeIndex;
        _udpDeployment = SelectedSource.GetProtocolExtra().SshUdpDeployment;
        DetectUdpCmd = ReactiveCommand.CreateFromTask(() => DetectUdpAsync(false));
        DeployGatewayCmd = ReactiveCommand.CreateFromTask(() => RunAsync(DeployGatewayAsync));
        StopOperationCmd = ReactiveCommand.Create(() => { _operation?.Cancel(); _udpDetection?.Cancel(); });
        this.WhenAnyValue(x => x.Address, x => x.Port, x => x.Username).Subscribe(_ => InvalidateUdpCapability());
        this.WhenAnyValue(x => x.Password, x => x.PrivateKeyAuth, x => x.PrivateKeyPath).Subscribe(_ => InvalidateUdpCapability());
        this.WhenAnyValue(x => x.Passphrase, x => x.HostKey, x => x.UdpGateway).Subscribe(_ => InvalidateUdpCapability());
        this.WhenAnyValue(x => x.UdpForwarding).Subscribe(enabled => { if (enabled) ScheduleUdpDetection(); });
        this.WhenAnyValue(x => x.UdpModeIndex).Subscribe(index =>
        {
            if (index == 0 && !UdpModes[0].Enabled && _lastUdpModeIndex != 0) { UdpModeIndex = _lastUdpModeIndex; return; }
            _lastUdpModeIndex = index;
        });
    }

    public void ActivateUdpCapabilities()
    {
        if (_udpActivated) return;
        _udpActivated = true;
        ScheduleUdpDetection();
    }

    private void InvalidateUdpCapability()
    {
        _udpRevision++;
        _udpDetection?.Cancel();
        _udpCapability = null;
        UdpCapabilityStatus = SshStrings.UdpUnknown;
        UdpModes[0].Enabled = true;
        ScheduleUdpDetection();
    }

    private void ScheduleUdpDetection()
    {
        if (_udpActivated && !_closed && UdpForwarding && _trustedKey != null)
            _ = DetectUdpAsync(true);
    }

    private async Task DetectUdpAsync(bool automatic)
    {
        if (_closed) return;
        _udpDetection?.Cancel();
        using var detection = new CancellationTokenSource();
        _udpDetection = detection;
        var revision = _udpRevision;
        try
        {
            if (automatic) await Task.Delay(700, detection.Token);
            var node = CreateDraftSnapshot();
            if (_trustedKey == null || SshProfileService.Validate(node) != null)
            { if (!automatic) UdpCapabilityStatus = SshStrings.UdpCredentialsRequired; return; }
            IsDetectingUdp = true;
            var result = await _udpCapabilityService.DetectAsync(node, detection.Token);
            if (_closed || detection.IsCancellationRequested || revision != _udpRevision) return;
            _udpCapability = result;
            UdpCapabilityStatus = SshStrings.UdpCapabilityResult(result);
            UdpModes[0].Enabled = result.CustomSelectable;
            // Historical protocols are never changed by probing. Only a new
            // profile with an explicit unsupported response gets this default.
            if (_newNode && result.Custom == "unsupported") UdpModeIndex = 1;
        }
        catch (OperationCanceledException) { }
        catch { if (revision == _udpRevision) UdpCapabilityStatus = SshStrings.UdpUnknown; }
        finally
        {
            if (_udpDetection == detection) { _udpDetection = null; IsDetectingUdp = false; }
        }
    }

    private async Task DeployGatewayAsync()
    {
        try
        {
            if (_trustedKey == null) await ConfirmAsync(true);
            if (_trustedKey == null || _closed) return;
            var node = CreateDraftSnapshot();
            var revision = _udpRevision;
            var token = _operation!.Token;
            DeploymentStatus = SshStrings.DeployPreflight;
            await using var deployment = await _gatewayDeploymentService.PrepareAsync(node, SudoPassword, token);
            if (_closed || revision != _udpRevision) return;
            var plan = deployment.Plan;
            var preview = SshStrings.DeployPreview(node, _trustedKey.Fingerprint, plan);
            if (!await ConfirmDeploymentInteraction.HandleSafe(preview) || _closed || revision != _udpRevision) return;
            token.ThrowIfCancellationRequested();
            DeploymentStatus = SshStrings.DeployInstalling;
            var record = await deployment.InstallAsync(token);
            if (_closed || revision != _udpRevision) return;
            _udpDeployment = record;
            UdpGateway = $"127.0.0.1:{record.Port}";
            UdpModeIndex = 1;
            UdpForwarding = true;
            DeploymentStatus = SshStrings.DeployCompleted;
            ScheduleUdpDetection();
        }
        catch (OperationCanceledException) { DeploymentStatus = SshStrings.DeployCancelled; }
        catch (SshSetupException error) { DeploymentStatus = SshStrings.DeployError(error.Code); }
        catch { DeploymentStatus = SshStrings.DeployError("E_DEPLOY_FAILED"); }
        finally { SudoPassword = ""; }
    }
}
