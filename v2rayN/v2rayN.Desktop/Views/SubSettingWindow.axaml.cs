using Avalonia.Collections;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DialogHostAvalonia;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class SubSettingWindow : PageBase<SubSettingViewModel>
{
    private bool _manualClose;
    private bool _updating;
    private DataGridCollectionView? _filtered;
    public bool IsEmbedded { get; set; }
    public Func<string, bool, Task>? UpdateSubscriptionAsync { get; set; }

    public SubSettingWindow()
    {
        InitializeComponent();
        txtGroupHint.Text = WorkspaceText.T("分组用于整理节点；设置订阅地址的分组可更新服务器。", "Groups organize servers. Groups with a subscription address can update their servers.");
        txtSubscriptionFilter.PlaceholderText = WorkspaceText.T("搜索分组名称或订阅地址", "Search groups or subscription addresses");
        cmbSubscriptionKind.ItemsSource = new[] { WorkspaceText.T("全部分组与订阅", "All groups & subscriptions"), WorkspaceText.T("仅订阅", "Subscriptions only"), WorkspaceText.T("仅本地分组", "Local groups only") };
        cmbSubscriptionKind.SelectedIndex = 0;
        TablePresentation.DisplaySelection(cmbSubscriptionKind, "—");
        btnUpdateAll.Content = WorkspaceText.T("更新全部订阅", "Update all subscriptions");
        menuUpdateAllViaProxy.Header = WorkspaceText.T("通过代理更新全部订阅", "Update all subscriptions via proxy");
        menuUpdateSelected.Header = WorkspaceText.T("更新所选订阅", "Update selected subscriptions");
        menuUpdateSelectedViaProxy.Header = WorkspaceText.T("通过代理更新所选订阅", "Update selected subscriptions via proxy");
        menuAddSubscription.Header = WorkspaceText.T("添加订阅", "Add subscription");
        menuAddLocalGroup.Header = WorkspaceText.T("添加本地分组", "Add local group");
        ToolTip.SetTip(btnUpdateOptions, WorkspaceText.T("订阅更新方式", "Subscription update options"));
        foreach (var flyout in new[] { btnUpdateOptions.Flyout, menuSubAdd.Flyout }.OfType<MenuFlyout>()) flyout.Popup.ShouldUseOverlayLayer = true;
        btnUpdateAll.Click += async (_, _) => await UpdateAsync(null, false);
        menuUpdateAllViaProxy.Click += async (_, _) => await UpdateAsync(null, true);
        menuUpdateSelected.Click += async (_, _) => await UpdateSelectedAsync(false);
        menuUpdateSelectedViaProxy.Click += async (_, _) => await UpdateSelectedAsync(true);
        menuAddSubscription.Click += async (_, _) => await AddAsync(true);
        menuAddLocalGroup.Click += async (_, _) => await AddAsync(false);
        txtSubscriptionFilter.TextChanged += (_, _) => RefreshFilter();
        cmbSubscriptionKind.SelectionChanged += (_, _) => RefreshFilter();
        AddColumns();
        lstSubscription.Columns[0].Header = WorkspaceText.T("名称", "Name");
        lstSubscription.Columns[2].Header = WorkspaceText.T("订阅地址", "Subscription address");
        SizeChanged += (_, _) =>
        {
            var narrow = Bounds.Width < 800;
            lstSubscription.Columns[0].Width = narrow ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(180);
            lstSubscription.Columns[1].MinWidth = Math.Max(110, TablePresentation.TextWidth(lstSubscription,
                WorkspaceText.T("本地分组", "Local group"), WorkspaceText.T("订阅", "Subscription")) + 24);
            cmbSubscriptionKind.Width = Math.Max(160, TablePresentation.TextWidth(cmbSubscriptionKind,
                cmbSubscriptionKind.ItemsSource!.Cast<string>().ToArray()) + 56);
            lstSubscription.Columns[2].IsVisible = lstSubscription.Columns[3].IsVisible = !narrow;
        };

        menuClose.Click += (_, _) => { _manualClose = true; Close(ViewModel?.IsModified); };
        Loaded += (_, _) => { menuClose.IsVisible = !IsEmbedded; lstSubscription.Focus(); };
        Closing += (_, args) =>
        {
            if (ViewModel?.IsModified == true && !_manualClose) { args.Cancel = true; _manualClose = true; Close(true); }
        };
        KeyDown += (_, args) => { if (args.Key == Key.Escape && !IsEmbedded) menuClose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        lstSubscription.DoubleTapped += async (_, args) =>
        {
            if (args.Source is Control source && (source is Button || source.FindAncestorOfType<Button>() != null || source is DataGridColumnHeader || source.FindAncestorOfType<DataGridColumnHeader>() != null)) return;
            if (ViewModel != null) await ViewModel.EditSubAsync(false);
        };
        lstSubscription.SelectionChanged += (_, _) =>
        {
            if (ViewModel == null) return;
            ViewModel.SelectedSources = lstSubscription.SelectedItems.Cast<SubItem>().ToList();
            var canUpdate = !_updating && ViewModel.SelectedSources.Any(sub => IsSubscription(sub) && sub.Enabled);
            menuUpdateSelected.IsEnabled = menuUpdateSelectedViaProxy.IsEnabled = canUpdate;
            menuSubShare2.IsVisible = ViewModel.SelectedSources.Count == 1 && IsSubscription(ViewModel.SelectedSources[0]);
        };
        lstSubscription.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (args.GetCurrentPoint(lstSubscription).Properties.IsRightButtonPressed
                && (args.Source as Control)?.FindAncestorOfType<DataGridRow>(true)?.DataContext is SubItem sub
                && !lstSubscription.SelectedItems.Contains(sub)) SelectRow(sub);
        }, RoutingStrategies.Bubble, true);

        this.WhenActivated(disposables =>
        {
            _filtered = new DataGridCollectionView(ViewModel!.SubItems) { Filter = Match };
            lstSubscription.ItemsSource = _filtered;
            System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) => RefreshFilter();
            ViewModel.SubItems.CollectionChanged += changed;
            new ActionDisposable(() => ViewModel.SubItems.CollectionChanged -= changed).DisposeWith(disposables);
            RefreshFilter();
            this.Bind(ViewModel, vm => vm.SelectedSource, v => v.lstSubscription.SelectedItem).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubDeleteCmd, v => v.menuSubDelete2).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubEditCmd, v => v.menuSubEdit2).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubShareCmd, v => v.menuSubShare2).DisposeWith(disposables);
            ViewModel.ShowYesNoInteraction.RegisterHandler(async interaction =>
                interaction.SetOutput(await UI.ShowYesNo(interaction.Input, WorkspaceText.Delete, true) == ButtonResult.Yes)).DisposeWith(disposables);
            ViewModel.ShareSubInteraction.RegisterHandler(async interaction =>
            {
                if (!string.IsNullOrWhiteSpace(interaction.Input)) await DialogHost.Show(new QrcodeView(interaction.Input, "dialogHostSub"), "dialogHostSub");
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);
        });
    }

    private static bool IsSubscription(SubItem sub) => !string.IsNullOrWhiteSpace(sub.Url);
    private bool Match(object value) => value is SubItem sub
        && (cmbSubscriptionKind.SelectedIndex != 1 || IsSubscription(sub))
        && (cmbSubscriptionKind.SelectedIndex != 2 || !IsSubscription(sub))
        && (string.IsNullOrWhiteSpace(txtSubscriptionFilter.Text) || new[] { sub.Remarks, sub.Url }.Any(text => text?.Contains(txtSubscriptionFilter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) == true));

    private void RefreshFilter()
    {
        _filtered?.Refresh();
        txtSubscriptionCount.Text = $"{_filtered?.Count ?? 0} · {WorkspaceText.GroupsAndSubscriptions}";
    }

    private void SelectRow(SubItem item)
    {
        lstSubscription.SelectedItems.Clear();
        lstSubscription.SelectedItem = item;
        if (ViewModel != null) { ViewModel.SelectedSource = item; ViewModel.SelectedSources = [item]; }
    }

    private void AddColumns()
    {
        lstSubscription.Columns.Insert(1, new DataGridTemplateColumn
        {
            Header = WorkspaceText.T("类型", "Type"), Width = new DataGridLength(110), MinWidth = 90,
            CellTemplate = new FuncDataTemplate<SubItem>((sub, _) => new TextBlock { Text = IsSubscription(sub!) ? WorkspaceText.T("订阅", "Subscription") : WorkspaceText.T("本地分组", "Local group"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) })
        });
        lstSubscription.Columns.Add(new DataGridTemplateColumn
        {
            Header = WorkspaceText.T("自动更新", "Auto update"), Width = new DataGridLength(140), MinWidth = 110,
            CellTemplate = new FuncDataTemplate<SubItem>((sub, _) => new TextBlock
            {
                Text = !IsSubscription(sub!) ? "—" : !sub!.Enabled ? WorkspaceText.T("已停用", "Disabled") : sub.AutoUpdateInterval > 0
                    ? WorkspaceText.T($"每 {sub.AutoUpdateInterval} 分钟", $"Every {sub.AutoUpdateInterval} min") : WorkspaceText.T("仅手动更新", "Manual only"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0)
            })
        });
        lstSubscription.Columns.Add(new DataGridTemplateColumn
        {
            Tag = "Actions", Header = WorkspaceText.Actions, Width = new DataGridLength(210), MinWidth = 180, CanUserSort = false,
            CellTemplate = new FuncDataTemplate<SubItem>((sub, _) =>
            {
                var edit = new Button { Name = "btnRowEditGroup", Content = WorkspaceText.Edit, Classes = { "rowAction" }, Padding = new Thickness(6, 4), Margin = new Thickness(0, 0, 4, 0) };
                var update = new Button { Name = "btnRowUpdateSubscription", Content = WorkspaceText.T("更新", "Update"), Classes = { "rowAction" }, Padding = new Thickness(6, 4), Margin = new Thickness(0, 0, 4, 0), IsVisible = IsSubscription(sub!), IsEnabled = sub!.Enabled };
                var more = new Button { Name = "btnRowGroupActions", Content = "⋯", Classes = { "rowAction" }, Padding = new Thickness(8, 4) };
                ToolTip.SetTip(more, WorkspaceText.T("分组操作", "Group actions"));
                ToolTip.SetTip(update, WorkspaceText.T("更新此订阅", "Update this subscription"));
                SubItem Current(Control control) => control.DataContext as SubItem ?? sub!;
                edit.Click += async (_, _) => { SelectRow(Current(edit)); if (ViewModel != null) await ViewModel.EditSubAsync(false); };
                update.Click += async (_, _) => await UpdateAsync([Current(update)], false);
                var flyout = new MenuFlyout(); flyout.Popup.ShouldUseOverlayLayer = true;
                var viaProxy = new MenuItem { Header = WorkspaceText.T("通过代理更新", "Update via proxy"), IsVisible = IsSubscription(sub), IsEnabled = sub.Enabled };
                viaProxy.Click += async (_, _) => await UpdateAsync([Current(more)], true);
                var share = new MenuItem { Header = ResUI.menuSubShare, IsVisible = IsSubscription(sub) };
                share.Click += async (_, _) => { SelectRow(Current(more)); if (ViewModel != null) await ViewModel.SubShareCmd.Execute().ToTask(); };
                var delete = new MenuItem { Header = WorkspaceText.Delete };
                delete.Click += async (_, _) => { SelectRow(Current(more)); if (ViewModel != null) await ViewModel.SubDeleteCmd.Execute().ToTask(); };
                flyout.Items.Add(viaProxy); flyout.Items.Add(share); flyout.Items.Add(delete); more.Flyout = flyout;
                return new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Children = { edit, update, more } };
            })
        });
    }

    private async Task AddAsync(bool subscription)
    {
        var model = new SubEditViewModel(new SubItem());
        var window = new SubEditWindow { ViewModel = model, Title = subscription ? menuAddSubscription.Header.ToString() : menuAddLocalGroup.Header.ToString() };
        EventHandler saved = (_, _) => window.Close(true);
        model.RequestClose += saved;
        window.Opened += (_, _) => window.FindControl<TextBox>(subscription ? "txtUrl" : "txtRemarks")?.Focus();
        try
        {
            if (await window.ShowDialog<bool>(v2rayN.Desktop.Manager.WindowDialog.TryGetOwnerWindow()) != true) return;
            await ViewModel!.RefreshSubItems();
            ViewModel.IsModified = true;
            RefreshFilter();
        }
        finally { model.RequestClose -= saved; }
    }

    private Task UpdateSelectedAsync(bool proxy) => UpdateAsync(lstSubscription.SelectedItems.Cast<SubItem>().ToArray(), proxy);

    private async Task UpdateAsync(IEnumerable<SubItem>? selected, bool proxy)
    {
        if (_updating) return;
        var targets = (selected ?? ViewModel?.SubItems ?? []).Where(sub => IsSubscription(sub) && sub.Enabled).ToArray();
        if (targets.Length == 0) { txtSubscriptionStatus.Text = WorkspaceText.T("没有可更新的订阅。本地分组无需更新。", "No enabled subscriptions to update. Local groups do not need updates."); txtSubscriptionStatus.IsVisible = true; return; }
        var update = UpdateSubscriptionAsync;
        if (update == null && (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is MainWindow { ViewModel: { } main }) update = main.UpdateSubscriptionProcess;
        if (update == null) { txtSubscriptionStatus.Text = WorkspaceText.T("订阅更新不可用，请返回主窗口重试。", "Subscription updates are unavailable. Return to the main window and retry."); txtSubscriptionStatus.IsVisible = true; return; }
        _updating = true;
        txtSubscriptionStatus.Classes.Remove("error");
        btnUpdateAll.IsEnabled = btnUpdateOptions.IsEnabled = menuSubAdd.IsEnabled = lstSubscription.IsEnabled = false;
        txtSubscriptionStatus.Text = WorkspaceText.T("正在更新订阅…", "Updating subscriptions…"); txtSubscriptionStatus.IsVisible = true;
        try
        {
            if (selected == null) await update("", proxy);
            else foreach (var sub in targets) await update(sub.Id, proxy);
            await ViewModel!.RefreshSubItems();
            RefreshFilter();
            txtSubscriptionStatus.Text = WorkspaceText.T("更新任务结束；结果与错误详情请查看日志。", "Update task finished. See logs for results and errors.");
        }
        catch (Exception exception)
        {
            txtSubscriptionStatus.Text = exception.Message;
            txtSubscriptionStatus.Classes.Add("error");
            NoticeManager.Instance.SendMessageEx(exception.Message);
        }
        finally { _updating = false; btnUpdateAll.IsEnabled = btnUpdateOptions.IsEnabled = menuSubAdd.IsEnabled = lstSubscription.IsEnabled = true; }
    }
}
