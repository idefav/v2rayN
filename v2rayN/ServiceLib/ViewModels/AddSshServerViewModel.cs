namespace ServiceLib.ViewModels;

public partial class AddSshServerViewModel : MyReactiveObject, ICloseable
{
    public event EventHandler? RequestClose;
    public Interaction<RxVoid, string?> BrowseKeyInteraction { get; } = new();
    public Interaction<string, bool> ConfirmHostKeyInteraction { get; } = new();
    public ProfileItem SelectedSource { get; }
    [Reactive] public partial string Remarks { get; set; }
    [Reactive] public partial string Address { get; set; }
    [Reactive] public partial string Port { get; set; }
    [Reactive] public partial string Username { get; set; }
    [Reactive] public partial string Password { get; set; }
    [Reactive] public partial bool PrivateKeyAuth { get; set; }
    [Reactive] public partial string PrivateKeyPath { get; set; }
    [Reactive] public partial string Passphrase { get; set; }
    [Reactive] public partial string HostKey { get; set; }
    [Reactive] public partial string Status { get; set; }
    [Reactive] public partial bool IsBusy { get; set; }
    public ReactiveCommand<RxVoid, RxVoid> SaveCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> FetchCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> TrustCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> BrowseCmd { get; }
    private CancellationTokenSource? _operation;
    private bool _closed;
    private string? _trustedAddress;
    private int? _trustedPort;
    private SshHostKey? _trustedKey;

    public AddSshServerViewModel(ProfileItem item)
    {
        _config = AppManager.Instance.Config;
        SelectedSource = item.IndexId.IsNullOrEmpty() ? item : JsonUtils.DeepCopy(item);
        var extra = item.GetProtocolExtra();
        Remarks = item.Remarks;
        Address = item.Address;
        Port = (item.Port == 0 ? 22 : item.Port).ToString();
        Username = item.Username;
        Password = item.Password;
        PrivateKeyAuth = extra.SshPrivateKeyAuth ?? item.IndexId.IsNullOrEmpty();
        PrivateKeyPath = extra.SshPrivateKeyPath ?? "";
        Passphrase = extra.SshPrivateKeyPassphrase ?? "";
        HostKey = extra.SshHostKey ?? "";
        Status = SshStrings.Unconfirmed;
        try
        {
            if (extra.SshTrustedAddress == Address && extra.SshTrustedPort == item.Port)
            {
                _trustedKey = SshProfileService.ParseHostKey(HostKey, extra.SshHostKeyAlgorithm);
                _trustedAddress = Address;
                _trustedPort = item.Port;
            }
        }
        catch { }
        this.WhenAnyValue(x => x.Address, x => x.Port, x => x.HostKey)
            .Subscribe(_ => RefreshTrust());
        SaveCmd = ReactiveCommand.CreateFromTask(() => RunAsync(SaveAsync));
        FetchCmd = ReactiveCommand.CreateFromTask(() => RunAsync(() => ConfirmAsync(true)));
        TrustCmd = ReactiveCommand.CreateFromTask(() => RunAsync(() => ConfirmAsync(false)));
        BrowseCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            if (IsBusy) return;
            var path = await BrowseKeyInteraction.HandleSafe(RxVoid.Default);
            if (!_closed && !string.IsNullOrEmpty(path)) PrivateKeyPath = path;
        });
    }

    public void CancelOperation()
    {
        _closed = true;
        _operation?.Cancel();
    }

    private void RefreshTrust()
    {
        if (_trustedAddress != Address.Trim() || _trustedPort?.ToString() != Port
            || _trustedKey?.PublicKey != HostKey)
        {
            _trustedKey = null;
        }
        Status = _trustedKey == null ? SshStrings.Unconfirmed
            : $"{SshStrings.Pinned}: {_trustedKey.Algorithm}\n{_trustedKey.Fingerprint}";
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy || _closed) return;
        IsBusy = true;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        try { await action(); }
        catch (OperationCanceledException) { Status = SshStrings.Cancelled; }
        catch { Status = SshStrings.FetchFailed; } // Never show exception text containing credentials.
        finally { _operation = null; IsBusy = false; }
    }

    private async Task ConfirmAsync(bool fetch)
    {
        var address = Address.Trim();
        if (address.Length == 0 || !int.TryParse(Port, out var port) || port is < 1 or > 65535)
        {
            Status = SshStrings.InvalidEndpoint;
            return;
        }
        var input = HostKey;
        SshHostKey key;
        if (fetch)
        {
            Status = SshStrings.Fetching;
            key = await SshProfileService.FetchHostKeyAsync(address, port, _operation!.Token);
        }
        else
        {
            try { key = SshProfileService.ParseHostKey(input); }
            catch { Status = SshStrings.InvalidHostKey; return; }
        }
        if (_closed || Address.Trim() != address || Port != port.ToString() || HostKey != input) return;
        var confirmed = await ConfirmHostKeyInteraction.HandleSafe(
            $"{SshStrings.ConfirmKey}\n\n{address}:{port}\n{key.Algorithm}\n{key.Fingerprint}");
        if (!confirmed || _closed || Address.Trim() != address || Port != port.ToString() || HostKey != input)
        {
            RefreshTrust();
            return;
        }
        HostKey = key.PublicKey;
        _trustedKey = key;
        _trustedAddress = address;
        _trustedPort = port;
        RefreshTrust();
    }

    private void UpdateSource()
    {
        SelectedSource.ConfigType = EConfigType.SSH;
        SelectedSource.Remarks = Remarks.Trim();
        SelectedSource.Address = Address.Trim();
        SelectedSource.Port = int.TryParse(Port, out var port) ? port : 0;
        SelectedSource.Username = Username.Trim();
        SelectedSource.Password = PrivateKeyAuth ? "" : Password;
        SelectedSource.SetProtocolExtra(SelectedSource.GetProtocolExtra() with
        {
            SshPrivateKeyAuth = PrivateKeyAuth,
            SshPrivateKeyPath = PrivateKeyAuth ? PrivateKeyPath : null,
            SshPrivateKeyPassphrase = PrivateKeyAuth ? Passphrase : null,
            SshHostKey = _trustedKey?.PublicKey,
            SshHostKeyAlgorithm = _trustedKey?.Algorithm,
            SshTrustedAddress = _trustedKey != null ? _trustedAddress : null,
            SshTrustedPort = _trustedKey != null ? _trustedPort : null,
        });
    }

    private async Task SaveAsync()
    {
        UpdateSource();
        if (SshProfileService.Validate(SelectedSource, requireTrust: false) is { } error)
        {
            Status = error;
            return;
        }
        if (_trustedKey == null)
        {
            await ConfirmAsync(true);
            if (_trustedKey == null || _closed) return;
            UpdateSource();
        }
        if (SelectedSource.Remarks.Length == 0)
            SelectedSource.Remarks = $"{Username}@{Address}:{Port}";
        if (await ConfigHandler.AddServer(_config, SelectedSource) == 0 && !_closed)
            RequestClose?.Invoke(this, EventArgs.Empty);
        else Status = ResUI.OperationFailed;
    }
}
