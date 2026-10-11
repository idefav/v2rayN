using Avalonia.VisualTree;
using Avalonia.Collections;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using DialogHostAvalonia;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class ProfilesView : ReactiveUserControl<ProfilesViewModel>
{
    public static readonly StyledProperty<bool> InlineActiveBadgeProperty = AvaloniaProperty.Register<ProfilesView, bool>(nameof(InlineActiveBadge), true);
    public bool InlineActiveBadge { get => GetValue(InlineActiveBadgeProperty); set => SetValue(InlineActiveBadgeProperty, value); }
    private static Config _config;
    private static readonly string _tag = "ProfilesView";
    private DataGridCollectionView? _filteredProfiles;
    private bool _refreshing;
    private bool _syncingGroup;
    private Button? _detailsEntry;
    private RightPinnedColumn _pinnedActions = null!;
    private Dictionary<DataGridColumn, (DataGridLength Width, double Minimum, bool Visible, int Index)>? _wideColumns;
    public event EventHandler? DetailsRequested;

    public void SetServerMenus(MenuFlyout imports, MenuFlyout add)
    {
        imports.Popup.ShouldUseOverlayLayer = true;
        add.Popup.ShouldUseOverlayLayer = true;
        imports.Closed += (_, _) => btnImportServers.Focus();
        add.Closed += (_, _) => btnAddServers.Focus();
        btnImportServers.Flyout = imports;
        btnAddServers.Flyout = add;
        btnEmptyImport.Click += (_, _) => imports.ShowAt(btnEmptyImport);
        btnEmptyAdd.Click += (_, _) => add.ShowAt(btnEmptyAdd);
    }
    public void FocusDetailsButton()
    {
        if (_detailsEntry?.IsAttachedToVisualTree() == true) _detailsEntry.Focus();
        else lstProfiles.Focus();
    }
    public void SetCompact(bool compact)
    {
        cmbGroupFilter.IsVisible = true;
        txtServerFilter.Width = Bounds.Width < 600 ? 180 : 220;
        cmbProtocolFilter.Width = Math.Max(120, TablePresentation.TextWidth(cmbProtocolFilter, WorkspaceText.AllProtocols) + 48);
        cmbGroupFilter.Width = Math.Max(160, FontSize * 9);
        _pinnedActions.Width = Math.Max(128, TablePresentation.RowActionWidth(lstProfiles, WorkspaceText.T("详情", "Details"), WorkspaceText.Edit));
        var size = Math.Max(14, AppManager.Instance.Config.UiItem.CurrentFontSize);
        var narrow = Bounds.Width > 0 && Bounds.Width * 14 / size < 800;
        if (narrow && _wideColumns == null)
        {
            _wideColumns = lstProfiles.Columns.ToDictionary(c => c, c => (c.Width, c.MinWidth, c.IsVisible, c.DisplayIndex));
            foreach (var column in lstProfiles.Columns)
            {
                var tag = column.Tag?.ToString();
                column.IsVisible = tag == null || tag is "Remarks" or "Actions"
                    || tag == "DelayVal" && Bounds.Width * 14 / size >= 360
                    || tag == "ConfigType" && Bounds.Width * 14 / size >= 600 && size <= 18;
                column.MinWidth = tag == null ? 52 : tag == "Remarks" ? 140 : tag == "Actions" ? Math.Max(128, TablePresentation.RowActionWidth(lstProfiles, WorkspaceText.T("详情", "Details"), WorkspaceText.Edit)) : tag == "ConfigType" ? 80 : 72;
                column.Width = tag == "Remarks" ? new DataGridLength(1, DataGridLengthUnitType.Star) :
                    new DataGridLength(tag == null ? 52 : tag == "Actions" ? column.MinWidth : tag == "ConfigType" ? 80 : Math.Max(88, TablePresentation.TextWidth(lstProfiles, WorkspaceText.Untested) + 24));
            }
            lstProfiles.Columns.First(c => c.Tag == null).DisplayIndex = 0;
            lstProfiles.Columns.First(c => c.Tag?.ToString() == "Remarks").DisplayIndex = 1;
        }
        else if (!narrow && _wideColumns != null)
        {
            foreach (var (column, state) in _wideColumns) { column.Width = state.Width; column.MinWidth = state.Minimum; column.IsVisible = state.Visible; }
            foreach (var (column, state) in _wideColumns.OrderBy(pair => pair.Value.Index)) column.DisplayIndex = state.Index;
            _wideColumns = null;
        }
    }

    public ProfilesView()
    {
        InitializeComponent();
        lstProfiles.Columns.Add(ExitPresentation.RegionColumn());
        lstProfiles.Columns.Add(new DataGridTextColumn { Header = WorkspaceText.T("出口 IP", "Exit IP"), Tag = "ExitIp", Binding = new Binding("ExitInfo.Ip"), Width = new DataGridLength(160), IsVisible = false, CanUserSort = false });
        var exitMenu = (MenuFlyout)btnTestScope.Flyout!;
        var checkSelected = new MenuItem { Header = WorkspaceText.T("检测选中节点出口", "Check selected exits") };
        var checkGroup = new MenuItem { Header = WorkspaceText.T("检测当前分组出口", "Check group exits") };
        var checkAll = new MenuItem { Header = WorkspaceText.T("检测全部服务器出口", "Check all exits") };
        var cancelExit = new MenuItem { Header = WorkspaceText.T("取消出口检测", "Cancel exit checks") };
        var cancelTests = new MenuItem { Header = WorkspaceText.T("取消测速", "Cancel speed tests") };
        checkSelected.Click += async (_, _) => await ExitPresentation.CheckAsync(ViewModel?.SelectedProfiles?.Select(p => p.IndexId) ?? []);
        checkGroup.Click += async (_, _) => { var group = ViewModel?.SelectedSub?.Id; var nodes = await ServiceLib.Helper.SQLiteHelper.Instance.TableAsync<ProfileItem>().ToListAsync(); await ExitPresentation.CheckAsync(nodes.Where(n => string.IsNullOrEmpty(group) || n.Subid == group).Select(n => n.IndexId)); };
        checkAll.Click += async (_, _) => await ExitPresentation.CheckAsync((await ServiceLib.Helper.SQLiteHelper.Instance.TableAsync<ProfileItem>().ToListAsync()).Select(n => n.IndexId));
        cancelExit.Click += (_, _) => ServiceLib.Services.ExitInfoService.Instance.Cancel();
        cancelTests.Click += (_, _) => ViewModel?.ServerSpeedtestStop();
        exitMenu.Items.Add(new Separator()); exitMenu.Items.Add(checkSelected); exitMenu.Items.Add(checkGroup); exitMenu.Items.Add(checkAll); exitMenu.Items.Add(cancelExit); exitMenu.Items.Add(cancelTests);
        var contextExit = new MenuItem { Header = WorkspaceText.T("检测选中节点出口", "Check selected exits") };
        contextExit.Click += async (_, _) => await ExitPresentation.CheckAsync(ViewModel?.SelectedProfiles?.Select(p => p.IndexId) ?? []);
        lstProfiles.ContextMenu!.Items.Add(contextExit);
        TablePresentation.TextIconButton(btnImportServers, WorkspaceText.Import, "M3,3 L13,3 13,7 M3,3 L3,21 17,21 17,15 M9,11 L21,11 M17,7 L21,11 17,15");
        TablePresentation.TextIconButton(btnAddServers, WorkspaceText.T("添加服务器", "Add server"), "M12,3 L12,21 M3,12 L21,12");
        TablePresentation.TextIconButton(menuMixedTestServer, WorkspaceText.TestSelected, "M3,17 A9,9 0 1 1 21,17 M5,18 L19,18 M12,14 L17,8");
        lstProfiles.HeadersVisibility = DataGridHeadersVisibility.Column;
        TablePresentation.AddSelectionColumn(lstProfiles);
        AddRowActions();
        var statusColumn = lstProfiles.Columns.Single(column => column.Tag?.ToString() == "Status");
        statusColumn.PropertyChanged += (_, change) => { if (change.Property.Name == "IsVisible") InlineActiveBadge = !statusColumn.IsVisible; };
        InlineActiveBadge = !statusColumn.IsVisible;
        TablePresentation.DisplaySelection(cmbGroupFilter, WorkspaceText.AllServers, value => value is SubItem sub
            ? string.IsNullOrEmpty(sub.Id) ? WorkspaceText.AllServers : WorkspaceText.T("分组：", "Group: ") + sub.Remarks : null);
        TablePresentation.DisplaySelection(cmbProtocolFilter, WorkspaceText.AllProtocols);
        btnClearSelection.Click += (_, _) => { lstProfiles.SelectedItems.Clear(); lstProfiles.Focus(); };
        btnClearFilter.Click += (_, _) => { txtServerFilter.Text = ""; cmbProtocolFilter.SelectedIndex = 0; txtServerFilter.Focus(); };
        btnAllServers.Click += (_, _) =>
        {
            txtServerFilter.Text = "";
            cmbProtocolFilter.SelectedIndex = 0;
            if (ViewModel != null) ViewModel.SelectedSub = ViewModel.SubItems.FirstOrDefault(sub => string.IsNullOrEmpty(sub.Id));
            lstProfiles.Focus();
        };

        ((MenuFlyout)btnMore.Flyout!).Popup.ShouldUseOverlayLayer = true;
        ((MenuFlyout)btnTestScope.Flyout!).Popup.ShouldUseOverlayLayer = true;

        _config = AppManager.Instance.Config;
        cmbProtocolFilter.Items.Add(WorkspaceText.AllProtocols);
        foreach (var protocol in Enum.GetValues<EConfigType>()) cmbProtocolFilter.Items.Add(protocol.ToString());
        cmbProtocolFilter.SelectedIndex = 0;
        cmbGroupFilter.SelectionChanged += (_, _) =>
        {
            if (!_syncingGroup && ViewModel != null && cmbGroupFilter.SelectedItem is SubItem group
                && group.Id != ViewModel.SelectedSub?.Id) ViewModel.SelectedSub = group;
        };
        cmbProtocolFilter.SelectionChanged += (_, _) => RefreshFilter();
        txtServerFilter.TextChanged += (_, _) => RefreshFilter();
        btnColumns.Click += (_, _) => ShowColumns();
        menuNodeDetails.Click += (_, _) => { _detailsEntry = null; DetailsRequested?.Invoke(this, EventArgs.Empty); };
        SizeChanged += (_, _) => SetCompact(TopLevel.GetTopLevel(this)?.Bounds.Width < 1000);
        btnMinimalColumns.Click += (_, _) =>
        {
            foreach (var column in lstProfiles.Columns) column.IsVisible = column.Tag == null || column.Tag.ToString() is "Remarks" or "ConfigType" or "DelayVal" or "Status" or "Actions" or "ExitLocation";
            lstProfiles.Columns.First(column => column.Tag?.ToString() == "Remarks").Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            StorageUI();
        };

        menuSelectAll.Click += menuSelectAll_Click;
        btnAutofitColumnWidth.Click += BtnAutofitColumnWidth_Click;
        txtServerFilter.KeyDown += TxtServerFilter_KeyDown;
        lstProfiles.KeyDown += LstProfiles_KeyDown;
        lstProfiles.SelectionChanged += lstProfiles_SelectionChanged;
        lstProfiles.DoubleTapped += LstProfiles_DoubleTapped;
        lstProfiles.LoadingRow += LstProfiles_LoadingRow;
        lstProfiles.Sorting += LstProfiles_Sorting;
        lstProfiles.AddHandler(PointerPressedEvent, LstProfiles_PointerPressed, RoutingStrategies.Bubble, true);
        if (_config.UiItem.EnableDragDropSort)
        {
            lstProfiles.SetValue(DragDrop.AllowDropProperty, true);

            lstProfiles.AddHandler(PointerMovedEvent, LstProfiles_PointerMoved, RoutingStrategies.Bubble, true);
            lstProfiles.AddHandler(PointerReleasedEvent, LstProfiles_PointerReleased, RoutingStrategies.Bubble, true);
            lstProfiles.AddHandler(DragDrop.DragOverEvent, LstProfiles_DragOver, RoutingStrategies.Bubble);
            lstProfiles.AddHandler(DragDrop.DropEvent, LstProfiles_Drop, RoutingStrategies.Bubble);
        }

        this.WhenActivated(disposables =>
        {
            void UpdateExit(string id) => Dispatcher.UIThread.Post(() => { foreach (var item in ViewModel?.ProfileItems.Where(p => p.IndexId == id) ?? []) item.ExitInfo = ServiceLib.Services.ExitInfoService.Instance.Get(id); });
            ServiceLib.Services.ExitInfoService.Instance.Changed += UpdateExit;
            new ActionDisposable(() => ServiceLib.Services.ExitInfoService.Instance.Changed -= UpdateExit).DisposeWith(disposables);
            _filteredProfiles ??= new DataGridCollectionView(ViewModel!.ProfileItems) { Filter = MatchProfile };
            lstProfiles.ItemsSource = _filteredProfiles;
            RefreshFilter();
            this.Bind(ViewModel, vm => vm.SelectedProfile, v => v.lstProfiles.SelectedItem).DisposeWith(disposables);

            this.WhenAnyValue(v => v.ViewModel!.SelectedSub).ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(group =>
            {
                _syncingGroup = true;
                try { cmbGroupFilter.SelectedItem = group; }
                finally { _syncingGroup = false; }
                RefreshFilter();
            }).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveServerCmd, v => v.btnBatchRemove).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.Export2ShareUrlCmd, v => v.btnBatchExport).DisposeWith(disposables);

            //servers delete
            this.BindCommand(ViewModel, vm => vm.EditServerCmd, v => v.menuEditServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveServerCmd, v => v.menuRemoveServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveDuplicateServerCmd, v => v.menuRemoveDuplicateServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CopyServerCmd, v => v.menuCopyServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SetDefaultServerCmd, v => v.menuSetDefaultServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ShareServerCmd, v => v.menuShareServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.GenGroupAllServerCmd, v => v.menuGenGroupAllServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.GenGroupRegionServerCmd, v => v.menuGenGroupRegionServer).DisposeWith(disposables);

            //servers move
            //this.OneWayBind(ViewModel, vm => vm.SubItems, v => v.cmbMoveToGroup.ItemsSource).DisposeWith(disposables);
            //this.Bind(ViewModel, vm => vm.SelectedMoveToGroup, v => v.cmbMoveToGroup.SelectedItem).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.MoveTopCmd, v => v.menuMoveTop).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveUpCmd, v => v.menuMoveUp).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveDownCmd, v => v.menuMoveDown).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveBottomCmd, v => v.menuMoveBottom).DisposeWith(disposables);

            //servers ping
            menuMixedTestServer.Command = ViewModel!.RealPingServerCmd;
            this.BindCommand(ViewModel, vm => vm.TcpingServerCmd, v => v.menuTcpingServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RealPingServerCmd, v => v.menuRealPingServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.UdpTestServerCmd, v => v.menuUdpTestServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SpeedServerCmd, v => v.menuSpeedServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SortServerResultCmd, v => v.menuSortServerResult).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveInvalidServerResultCmd, v => v.menuRemoveInvalidServerResult).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.FastRealPingCmd, v => v.btnFastRealPing).DisposeWith(disposables);

            //servers export
            this.BindCommand(ViewModel, vm => vm.Export2ClientConfigCmd, v => v.menuExport2ClientConfig).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.Export2ClientConfigClipboardCmd, v => v.menuExport2ClientConfigClipboard).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.Export2ShareUrlCmd, v => v.menuExport2ShareUrl).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.Export2ShareUrlBase64Cmd, v => v.menuExport2ShareUrlBase64).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.Export2InnerUriCmd, v => v.menuExport2InnerUri).DisposeWith(disposables);

            ViewModel.ShowYesNoInteraction.RegisterHandler(async interaction =>
            {
                var message = interaction.Input;
                var result = await UI.ShowYesNo(message, WorkspaceText.Delete, true);
                interaction.SetOutput(result == ButtonResult.Yes);
            }).DisposeWith(disposables);

            ViewModel.SaveFileDialogInteraction.RegisterHandler(async interaction =>
            {
                var viewModel = ViewModel;
                if (viewModel is null)
                {
                    interaction.SetOutput(false);
                    return;
                }
                var profileItem = interaction.Input;
                var fileName = await UI.SaveFileDialog("");
                if (fileName.IsNullOrEmpty())
                {
                    interaction.SetOutput(false);
                    return;
                }
                await viewModel.Export2ClientConfigResult(fileName, profileItem);
                interaction.SetOutput(true);
            }).DisposeWith(disposables);

            ViewModel.SetClipboardDataInteraction.RegisterHandler(async interaction =>
            {
                var strData = interaction.Input;
                await AvaUtils.SetClipboardData(this, strData);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.ProfilesFocusInteraction.RegisterHandler(interaction =>
            {
                lstProfiles.Focus();
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.ShareServerInteraction.RegisterHandler(async interaction =>
            {
                var url = interaction.Input;
                if (url.IsNullOrEmpty())
                {
                    interaction.SetOutput(RxVoid.Default);
                    return;
                }
                await ShareServer(url);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.DispatcherRefreshServersBizInteraction.RegisterHandler(interaction =>
            {
                Dispatcher.UIThread.Post(RefreshServersBiz, DispatcherPriority.Default);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.AdjustMainLvColWidthInteraction.RegisterHandler(interaction =>
            {
                //AutofitColumnWidth();
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            AppEvents.AppExitRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(_ => StorageUI())
              .DisposeWith(disposables);
    });

        RestoreUI();
        UpdateColumnSizes();
        PropertyChanged += (_, args) => { if (args.Property == FontSizeProperty) UpdateColumnSizes(); };
    }

    private void UpdateColumnSizes()
    {
        _pinnedActions.Width = Math.Max(128, TablePresentation.RowActionWidth(lstProfiles, WorkspaceText.T("详情", "Details"), WorkspaceText.Edit));
        if (_wideColumns != null) return;
        cmbProtocolFilter.Width = Math.Max(120, TablePresentation.TextWidth(cmbProtocolFilter, WorkspaceText.AllProtocols) + 48);
        foreach (var column in lstProfiles.Columns.Where(column => column.Tag != null))
            column.MinWidth = column.Tag?.ToString() == "Actions" ? Math.Max(128, TablePresentation.RowActionWidth(lstProfiles, WorkspaceText.T("详情", "Details"), WorkspaceText.Edit))
                : FontSize * (column.Tag?.ToString() switch
                { "Remarks" => 16, "DelayVal" => 9, "SpeedVal" => 11, "ConfigType" or "Status" => 7, _ => 6 });
    }

    private bool MatchProfile(object value)
    {
        if (value is not ProfileItemModel profile) return false;
        var protocol = cmbProtocolFilter.SelectedIndex > 0 ? cmbProtocolFilter.SelectedItem?.ToString() : null;
        if (protocol != null && profile.ConfigType.ToString() != protocol) return false;
        var query = txtServerFilter.Text?.Trim();
        return string.IsNullOrEmpty(query) || new[] { profile.Remarks, profile.Address, profile.ConfigType.ToString(), profile.SubRemarks }
            .Any(text => text?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
    }

    private void RefreshFilter()
    {
        var selectedIds = ViewModel?.SelectedProfiles?.Select(profile => profile.IndexId).ToHashSet() ?? [];
        var current = ViewModel?.SelectedProfile;
        _refreshing = true;
        try
        {
            _filteredProfiles?.Refresh();
            var visible = _filteredProfiles?.Cast<ProfileItemModel>().ToArray() ?? [];
            lstProfiles.SelectedItems.Clear();
            lstProfiles.SelectedItem = visible.FirstOrDefault(profile => profile.IndexId == current?.IndexId);
            foreach (var item in visible.Where(profile => selectedIds.Contains(profile.IndexId)))
                if (!lstProfiles.SelectedItems.Contains(item)) lstProfiles.SelectedItems.Add(item);
        }
        finally { _refreshing = false; }
        if (ViewModel != null) ViewModel.SelectedProfiles = lstProfiles.SelectedItems.Cast<ProfileItemModel>().ToList();
        UpdateSelectionTools();
        var group = ViewModel?.SelectedSub;
        txtProfileCount.Text = $"{(string.IsNullOrEmpty(group?.Id) ? WorkspaceText.AllServers : group.Remarks)} · {_filteredProfiles?.Count ?? 0} {WorkspaceText.T("个节点", "servers")}";
        btnAllServers.IsVisible = !string.IsNullOrEmpty(group?.Id) || !string.IsNullOrWhiteSpace(txtServerFilter.Text) || cmbProtocolFilter.SelectedIndex > 0;
        btnFastRealPing.Header = (string.IsNullOrEmpty(group?.Id) ? WorkspaceText.T("测试全部服务器", "Test all servers") : WorkspaceText.TestGroup + " · " + group.Remarks) + $" ({ViewModel?.ProfileItems.Count ?? 0})";
        emptyState.IsVisible = _filteredProfiles?.Count == 0;
        var noMatches = ViewModel?.ProfileItems.Count > 0;
        txtEmptyTitle.Text = noMatches ? WorkspaceText.T("没有匹配的节点", "No matching servers") : WorkspaceText.T("尚未添加服务器", "No servers yet");
        txtEmptyHint.Text = noMatches ? WorkspaceText.T("试试其他关键词或清除筛选条件。", "Try another query or clear the filters.")
            : WorkspaceText.T("导入订阅、分享链接或添加一个服务器。", "Import a subscription or share link, or add a server.");
        btnEmptyAdd.IsVisible = btnEmptyImport.IsVisible = !noMatches;
        btnClearFilter.IsVisible = noMatches;
    }

    private void AddRowActions()
    {
        _pinnedActions = new RightPinnedColumn(lstProfiles, WorkspaceText.Actions,
            new FuncDataTemplate<ProfileItemModel>((item, _) =>
            {
                var details = new Button { Name = "btnRowDetails", Content = WorkspaceText.T("详情", "Details"), Classes = { "rowAction" }, Padding = new Thickness(6, 4), Margin = new Thickness(0, 0, 4, 0) };
                var edit = new Button { Name = "btnRowEdit", Content = WorkspaceText.Edit, Classes = { "rowAction" }, Padding = new Thickness(6, 4) };
                ToolTip.SetTip(details, WorkspaceText.Details);
                ToolTip.SetTip(edit, ResUI.menuEditServer);
                details.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(ProfileItemModel.Remarks)) { StringFormat = WorkspaceText.Details + " · {0}" });
                edit.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(ProfileItemModel.Remarks)) { StringFormat = WorkspaceText.Edit + " · {0}" });
                details.Click += (_, _) =>
                {
                    SelectRow(details.DataContext as ProfileItemModel ?? item);
                    _detailsEntry = details;
                    DetailsRequested?.Invoke(this, EventArgs.Empty);
                };
                edit.Click += async (_, _) =>
                {
                    SelectRow(edit.DataContext as ProfileItemModel ?? item);
                    if (ViewModel != null) await ViewModel.EditServerAsync();
                };
                return new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0), Children = { details, edit } };
            }));
    }

    private void SelectRow(ProfileItemModel? item)
    {
        if (item == null) return;
        lstProfiles.SelectedItems.Clear();
        lstProfiles.SelectedItem = item;
        if (ViewModel != null) { ViewModel.SelectedProfile = item; ViewModel.SelectedProfiles = [item]; }
    }

    private void ShowColumns()
    {
        var choices = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        foreach (var column in lstProfiles.Columns)
        {
            if (column.Tag == null || column.Tag?.ToString() == "Actions") continue;
            var title = column.Header is TextBlock text ? text.Text : column.Header?.ToString();
            var choice = new CheckBox { Content = title, IsChecked = column.IsVisible };
            choice.IsCheckedChanged += (_, _) => { column.IsVisible = choice.IsChecked == true; StorageUI(); };
            choices.Children.Add(choice);
        }
        var flyout = new Flyout { Content = choices };
        btnMore.Flyout?.Hide();
        flyout.ShowAt(btnMore);
    }

    private async void LstProfiles_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;

        if (ViewModel != null && e.Column?.Tag?.ToString() != null)
        {
            await ViewModel.SortServer(e.Column.Tag.ToString());
        }

        e.Handled = false;
    }

    #region Event

    public async Task ShareServer(string url)
    {
        if (url.IsNullOrEmpty())
        {
            return;
        }

        var dialog = new QrcodeView(url);
        await DialogHost.Show(dialog);
    }

    public void RefreshServersBiz()
    {
        RefreshFilter();
        if (lstProfiles.SelectedIndex >= 0)
        {
            lstProfiles.ScrollIntoView(lstProfiles.SelectedItem, null);
        }
    }

    private void lstProfiles_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && ViewModel != null)
        {
            ViewModel.SelectedProfiles = lstProfiles.SelectedItems.Cast<ProfileItemModel>().ToList();
            UpdateSelectionTools();
        }
    }

    private void UpdateSelectionTools()
    {
            var count = lstProfiles.SelectedItems.Count;
            var multiple = count > 1;
            txtSelectedCount.Text = WorkspaceText.Selected(count);
            txtSelectedCount.IsVisible = multiple;
            btnBatchRemove.IsVisible = btnBatchExport.IsVisible = multiple;
            btnClearSelection.IsVisible = multiple;
            btnImportServers.IsVisible = btnAddServers.IsVisible = !multiple;
            txtServerFilter.IsVisible = cmbProtocolFilter.IsVisible = !multiple;
            cmbGroupFilter.IsEnabled = !multiple;
            menuMixedTestServer.IsEnabled = count > 0;
            menuNodeDetails.IsEnabled = count == 1;
    }

    private void LstProfiles_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        var source = e.Source as Control;
        if (source is Button || source?.FindAncestorOfType<Button>() != null
            || source is CheckBox || source?.FindAncestorOfType<CheckBox>() != null
            || source is DataGridColumnHeader
            || source?.FindAncestorOfType<DataGridColumnHeader>() != null)
        {
            return;
        }

        if (_config.UiItem.DoubleClick2Activate)
        {
            ViewModel?.SetDefaultServer();
        }
        else
        {
            ViewModel?.EditServerAsync();
        }
    }

    private void LstProfiles_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.Header = $" {e.Row.Index + 1}";
    }

    //private void LstProfiles_ColumnHeader_Click(object? sender, RoutedEventArgs e)
    //{
    //    var colHeader = sender as DataGridColumnHeader;
    //    if (colHeader == null || colHeader.TabIndex < 0 || colHeader.Column == null)
    //    {
    //        return;
    //    }

    //    var colName = ((MyDGTextColumn)colHeader.Column).ExName;
    //    ViewModel?.SortServer(colName);
    //}

    private void menuSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        lstProfiles.SelectAll();
    }

    private void LstProfiles_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta)
        {
            switch (e.Key)
            {
                case Key.A:
                    menuSelectAll_Click(null, null);
                    break;

                case Key.C:
                    ViewModel?.Export2ShareUrlAsync(false);
                    break;

                case Key.D:
                    ViewModel?.EditServerAsync();
                    break;

                case Key.F:
                    ViewModel?.ShareServerAsync();
                    break;

                case Key.O:
                    ViewModel?.ServerSpeedtest(ESpeedActionType.Tcping);
                    break;

                case Key.R:
                    ViewModel?.ServerSpeedtest(ESpeedActionType.Realping);
                    break;

                case Key.T:
                    ViewModel?.ServerSpeedtest(ESpeedActionType.Speedtest);
                    break;

                case Key.E:
                    ViewModel?.ServerSpeedtest(ESpeedActionType.Mixedtest);
                    break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Enter:
                    //case Key.Return:
                    ViewModel?.SetDefaultServer();
                    break;

                case Key.Delete:
                case Key.Back:
                    ViewModel?.RemoveServerAsync();
                    break;

                case Key.T:
                    ViewModel?.MoveServer(EMove.Top);
                    break;

                case Key.U:
                    ViewModel?.MoveServer(EMove.Up);
                    break;

                case Key.D:
                    ViewModel?.MoveServer(EMove.Down);
                    break;

                case Key.B:
                    ViewModel?.MoveServer(EMove.Bottom);
                    break;

                case Key.Escape:
                    ViewModel?.ServerSpeedtestStop();
                    break;
            }
        }
    }

    private void BtnAutofitColumnWidth_Click(object? sender, RoutedEventArgs e)
    {
        AutofitColumnWidth();
    }

    private void AutofitColumnWidth()
    {
        try
        {
            //First scroll horizontally to the initial position to avoid the control crash bug
            if (lstProfiles.SelectedIndex >= 0)
            {
                lstProfiles.ScrollIntoView(lstProfiles.SelectedItem, lstProfiles.Columns[0]);
            }
            else
            {
                var model = lstProfiles.ItemsSource.Cast<ProfileItemModel>();
                if (model.Any())
                {
                    lstProfiles.ScrollIntoView(model.First(), lstProfiles.Columns[0]);
                }
                else
                {
                    return;
                }
            }

            foreach (var it in lstProfiles.Columns)
            {
                it.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private void TxtServerFilter_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            ViewModel?.RefreshServers();
        }
    }

    #endregion Event

    #region UI

    private void RestoreUI()
    {
        try
        {
            if (_config.UiItem.MainColumnItem.Count == 0)
            {
                foreach (var column in lstProfiles.Columns)
                    column.IsVisible = column.Tag == null || column.Tag.ToString() is "Remarks" or "ConfigType" or "DelayVal" or "Status" or "Actions" or "ExitLocation";
                var nameColumn = lstProfiles.Columns.First(c => c.Tag?.ToString() == "Remarks");
                nameColumn.DisplayIndex = 1;
                nameColumn.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                lstProfiles.Columns.First(c => c.Tag == null).DisplayIndex = 0;
                lstProfiles.Columns.First(c => c.Tag?.ToString() == "DelayVal").Width = new DataGridLength(140);
                lstProfiles.Columns.First(c => c.Tag?.ToString() == "ConfigType").Width = new DataGridLength(140);
                lstProfiles.Columns.First(c => c.Tag?.ToString() == "Status").Width = new DataGridLength(140);
            }
            var lvColumnItem = _config.UiItem.MainColumnItem.OrderBy(t => t.Index).ToList();
            var displayIndex = 1;
            foreach (var item in lvColumnItem)
            {
                foreach (var item2 in lstProfiles.Columns)
                {
                    if (item2.Tag == null || item2.Tag?.ToString() == "Actions")
                    {
                        continue;
                    }
                    if (item2.Tag.Equals(item.Name))
                    {
                        if (item.Width < 0)
                        {
                            item2.IsVisible = false;
                        }
                        else
                        {
                            item2.IsVisible = true;
                            item2.Width = new DataGridLength(item.Width, DataGridLengthUnitType.Pixel);
                            item2.DisplayIndex = displayIndex++;
                        }
                        if (item.Name.StartsWith("to", StringComparison.CurrentCultureIgnoreCase))
                        {
                            item2.IsVisible = _config.GuiItem.EnableStatistics;
                        }
                        if (item.Name.Equals("IpInfo", StringComparison.CurrentCultureIgnoreCase))
                        {
                            item2.IsVisible = _config.SpeedTestItem.IPAPIUrl.IsNotEmpty() && !_config.UiItem.HideColumnIpInfo;
                        }
                    }
                }
            }
            if (!lvColumnItem.Any(c => c.Name == "ExitLocation"))
                lstProfiles.Columns.First(c => c.Tag?.ToString() == "ExitLocation").DisplayIndex = lstProfiles.Columns.First(c => c.Tag?.ToString() == "Remarks").DisplayIndex + 1;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private void StorageUI()
    {
        if (_wideColumns != null) return;
        try
        {
            List<ColumnItem> lvColumnItem = [];
            foreach (var item2 in lstProfiles.Columns)
            {
                if (item2.Tag == null || item2.Tag?.ToString() == "Actions")
                {
                    continue;
                }
                lvColumnItem.Add(new()
                {
                    Name = (string)item2.Tag,
                    Width = (int)(item2.IsVisible == true ? item2.ActualWidth : -1),
                    Index = item2.DisplayIndex
                });
            }
            _config.UiItem.MainColumnItem = lvColumnItem;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    #endregion UI

    #region Drag and Drop

    private static readonly DataFormat<ProfileItemModel> LstProfilesRowFormat =
        DataFormat.CreateInProcessFormat<ProfileItemModel>("LstProfilesRow");

    private (Point, PointerPressedEventArgs)? _dragStartPoint;

    private void LstProfiles_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        var source = e.Source as Control;
        if (properties.IsRightButtonPressed && source?.FindAncestorOfType<DataGridRow>(true)?.DataContext is ProfileItemModel node
            && !lstProfiles.SelectedItems.Contains(node))
        {
            lstProfiles.SelectedItems.Clear();
            lstProfiles.SelectedItem = node;
        }
        if (source is Button || source?.FindAncestorOfType<Button>() != null || source is CheckBox || source?.FindAncestorOfType<CheckBox>() != null)
        {
            _dragStartPoint = null;
            return;
        }
        if (_config.UiItem.EnableDragDropSort && properties.IsLeftButtonPressed)
        {
            _dragStartPoint = (e.GetPosition(this), e);
        }
    }

    private async void LstProfiles_PointerMoved(object? sender, PointerEventArgs e)
    {
        try
        {
            if (_dragStartPoint == null)
            {
                return;
            }

            var properties = e.GetCurrentPoint(this).Properties;
            if (!properties.IsLeftButtonPressed)
            {
                _dragStartPoint = null;
                return;
            }

            var currentPoint = e.GetPosition(this);
            var startPoint = _dragStartPoint.Value.Item1;
            var delta = startPoint - currentPoint;

            var threshold = new Vector(4, 4);

            if (!(Math.Abs(delta.X) >= threshold.X) && !(Math.Abs(delta.Y) >= threshold.Y))
            {
                return;
            }
            var dragStartEventArgs = _dragStartPoint.Value.Item2;
            _dragStartPoint = null;

            if (e.Source is not Visual visualSource)
            {
                return;
            }
            var row = visualSource.FindAncestorOfType<DataGridRow>(true);
            if (row?.DataContext == null)
            {
                return;
            }

            e.Handled = true;

            var dragData = new DataTransfer();
            var item = DataTransferItem.Create(LstProfilesRowFormat, row.DataContext as ProfileItemModel);

            dragData.Add(item);

            await DragDrop.DoDragDropAsync(dragStartEventArgs, dragData, DragDropEffects.Move);
        }
        catch
        {
            // Ignore
        }
    }

    private void LstProfiles_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStartPoint = null;
    }

    private void LstProfiles_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(LstProfilesRowFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;
    }

    private void LstProfiles_Drop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(LstProfilesRowFormat))
        {
            return;
        }
        ProfileItemModel? sourceItem = null;
        foreach (var item in e.DataTransfer.Items)
        {
            if (!item.Formats.Contains(LstProfilesRowFormat))
            {
                continue;
            }
            if (item.TryGetRaw(LstProfilesRowFormat) is not ProfileItemModel model)
            {
                continue;
            }
            sourceItem = model;
            break;
        }
        if (sourceItem == null)
        {
            return;
        }
        if (e.Source is not Visual visualTarget)
        {
            return;
        }

        var targetRow = visualTarget.FindAncestorOfType<DataGridRow>(true);
        if (targetRow is not { DataContext: ProfileItemModel targetItem })
        {
            return;
        }
        if (ReferenceEquals(sourceItem, targetItem))
        {
            return;
        }
        if (ViewModel?.ProfileItems is not { } items)
        {
            return;
        }
        var oldIndex = items.IndexOf(sourceItem);
        var newIndex = items.IndexOf(targetItem);
        if (oldIndex >= 0 && newIndex >= 0)
        {
            ViewModel?.MoveServerTo(oldIndex, targetItem);
        }
    }

    #endregion Drag and Drop
}
