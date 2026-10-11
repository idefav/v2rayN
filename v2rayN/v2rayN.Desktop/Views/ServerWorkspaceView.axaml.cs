using Avalonia.Data;
using Avalonia.Layout;
using ReactiveUI.Primitives.Signals;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Manager;

namespace v2rayN.Desktop.Views;

public partial class ServerWorkspaceView : ReactiveUserControl<ProfilesViewModel>
{
    private bool _drawerOpen;
    private ProfilesView? _profilesView;
    private MenuFlyout? _imports;
    private MenuFlyout? _add;
    public event EventHandler? DetailsOpened;
    public bool IsDetailsOpen => _drawerOpen;

    public ServerWorkspaceView()
    {
        InitializeComponent();
        var exitDetails = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var checkExit = new Button { Content = WorkspaceText.T("检测出口", "Check exit") };
        var copyExit = new Button { Content = WorkspaceText.T("复制出口 IP", "Copy exit IP") };
        details.Children.Add(new TextBlock { Text = WorkspaceText.T("出口信息", "Exit information"), Classes = { "sectionTitle" } });
        details.Children.Add(exitDetails);
        details.Children.Add(new WrapPanel { Children = { checkExit, copyExit } });
        checkExit.Click += async (_, _) => { if (ViewModel?.SelectedProfile is { } selected) await ExitPresentation.CheckAsync([selected.IndexId]); };
        copyExit.Click += async (_, _) => { var value = ViewModel?.SelectedProfile?.ExitInfo?.Ip; if (value != null) await AvaUtils.SetClipboardData(this, value); };
        this.WhenActivated(disposables =>
        {
            this.WhenAnyValue(v => v.ViewModel.SelectedProfile.ExitInfo).ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(info => { exitDetails.Text = ExitPresentation.Details(info); copyExit.IsEnabled = info?.Ip != null; }).DisposeWith(disposables);
        });
        btnCloseDetails.Click += (_, _) => CloseDetails();
        btnSshKey.Click += async (_, _) =>
        {
            var selected = ViewModel?.SelectedProfile;
            if (selected?.ConfigType != EConfigType.SSH) return;
            var item = await AppManager.Instance.GetProfileItem(selected.IndexId);
            if (item == null) return;
            var vm = new AddSshServerViewModel(item) { PrivateKeyAuth = true };
            var window = new AddSshServerWindow { ViewModel = vm };
            EventHandler saved = (_, _) => window.Close(true);
            vm.RequestClose += saved;
            window.Opened += async (_, _) => await vm.SetupKeyCmd.Execute().ToTask();
            try
            {
                if (await window.ShowDialog<bool>(WindowDialog.TryGetOwnerWindow())) await ViewModel!.RefreshServers();
            }
            finally { vm.RequestClose -= saved; }
        };
        this.WhenActivated(disposables =>
        {
            if (_profilesView == null)
            {
                _profilesView = (ProfilesView)SimpleViewLocator.Instance.Build(ViewModel!);
                _profilesView.ViewModel = ViewModel;
                _profilesView.DetailsRequested += (_, _) => OpenDetails();
                if (_imports != null && _add != null) _profilesView.SetServerMenus(_imports, _add);
            }
            profiles.Content = _profilesView;
            this.WhenAnyValue(v => v.ViewModel!.SelectedProfile.DelayVal, v => v.ViewModel!.SelectedProfile.SpeedVal, v => v.ViewModel!.SelectedProfile.IpInfo)
                .ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ =>
                {
                    var selected = ViewModel!.SelectedProfile;
                    txtLatency.Text = Metric(selected?.DelayVal, "ms", WorkspaceText.Untested);
                    txtSpeed.Text = Metric(selected?.SpeedVal, "MB/s", "—");
                    txtIp.Text = string.IsNullOrWhiteSpace(selected?.IpInfo) || selected.IpInfo == Global.None ? "—" : selected.IpInfo;
                }).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel!.SelectedProfile)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(selected =>
                {
                    var hasSelection = selected?.IndexId.IsNotEmpty() == true;
                    details.IsVisible = hasSelection;
                    detailActions.IsVisible = hasSelection;
                    txtEmpty.IsVisible = !hasSelection;
                    btnSshKey.IsVisible = selected?.ConfigType == EConfigType.SSH;
                    txtLatency.Text = Metric(selected?.DelayVal, "ms", WorkspaceText.Untested);
                    txtSpeed.Text = Metric(selected?.SpeedVal, "MB/s", "—");
                    txtIp.Text = string.IsNullOrWhiteSpace(selected?.IpInfo) || selected.IpInfo == Global.None ? "—" : selected.IpInfo;
                    txtUsername.Text = "—";
                    txtAuthentication.Text = "—";
                }).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel!.SelectedProfile)
                .Select(selected => Signal.FromAsync(async () => selected?.IndexId.IsNotEmpty() == true
                    ? await AppManager.Instance.GetProfileItem(selected.IndexId) : null))
                .Switch()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(item =>
                {
                    if (item == null || item.IndexId != ViewModel?.SelectedProfile?.IndexId) return;
                    txtUsername.Text = string.IsNullOrWhiteSpace(item.Username) ? "—" : item.Username;
                    if (item.ConfigType == EConfigType.SSH)
                        txtAuthentication.Text = item.GetProtocolExtra().SshPrivateKeyAuth == true ? SshStrings.PrivateKeyAuth : SshStrings.LoginPassword;
                }).DisposeWith(disposables);
        });
    }

    public void SetCompact(bool compact)
    {
        _profilesView?.SetCompact(compact);
        UpdateDrawerLayout();
        inspector.Width = Math.Min(360, Math.Max(240, Bounds.Width));
        inspector.IsVisible = _drawerOpen;
    }

    public void SetServerMenus(MenuFlyout imports, MenuFlyout add)
    {
        _imports = imports; _add = add;
        _profilesView?.SetServerMenus(imports, add);
    }

    public void OpenDetails()
    {
        _drawerOpen = true;
        inspector.IsVisible = true;
        inspector.Width = Math.Min(360, Math.Max(240, Bounds.Width));
        UpdateDrawerLayout();
        DetailsOpened?.Invoke(this, EventArgs.Empty);
        btnCloseDetails.Focus();
    }

    public bool CloseDetails()
    {
        if (!_drawerOpen) return false;
        _drawerOpen = false;
        inspector.IsVisible = false;
        UpdateDrawerLayout();
        _profilesView?.FocusDetailsButton();
        return true;
    }

    private void UpdateDrawerLayout()
    {
        if (_profilesView != null)
            _profilesView.Margin = _drawerOpen && (TopLevel.GetTopLevel(this)?.Bounds.Width ?? Bounds.Width) >= 1200
                ? new Thickness(0, 0, Math.Min(360, Bounds.Width), 0) : new Thickness(0);
    }

    private static string Metric(string? value, string unit, string empty)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "0" or "-1" || value == Global.None) return empty;
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            || double.TryParse(value, out _) ? value + " " + unit : value;
    }
}
