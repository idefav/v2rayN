namespace v2rayN.Desktop.Views;

public partial class ClashConnectionsView : ReactiveUserControl<ClashConnectionsViewModel>
{
    private static Config _config;
    private static readonly string _tag = "ClashConnectionsView";

    public ClashConnectionsView()
    {
        InitializeComponent();

        Common.TablePresentation.TextButton(btnConnectionCloseAll, ResUI.menuConnectionCloseAll);
        btnConnectionCloseAll.Classes.Add("danger");
        Common.TablePresentation.TextButton(btnAutofitColumnWidth, ResUI.menuProfileAutofitColumnWidth);
        var closeSelected = new Button { Content = Common.WorkspaceText.T("关闭此连接", "Close connection") };
        closeSelected.Classes.Add("danger");
        Common.TablePresentation.AddDetails(this, lstConnections, Common.WorkspaceText.T("连接详情", "Connection details"),
            [(ResUI.TbSortingHost, "Host"), (Common.WorkspaceText.T("路由链", "Chain"), "Chain"), (ResUI.TbSortingNetwork, "Network"),
             (Common.WorkspaceText.T("类型", "Type"), "Type"), (Common.WorkspaceText.T("进程", "Process"), "ProcessPath"),
             (Common.WorkspaceText.T("持续时间", "Duration"), "Elapsed")], closeSelected);

        _config = AppManager.Instance.Config;

        btnAutofitColumnWidth.Click += BtnAutofitColumnWidth_Click;

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.ConnectionItems, v => v.lstConnections.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSource, v => v.lstConnections.SelectedItem).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.ConnectionCloseCmd, v => v.menuConnectionClose).DisposeWith(disposables);
            closeSelected.Command = ViewModel.ConnectionCloseCmd;
            this.BindCommand(ViewModel, vm => vm.ConnectionCloseAllCmd, v => v.menuConnectionCloseAll).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.HostFilter, v => v.txtHostFilter.Text).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ConnectionCloseAllCmd, v => v.btnConnectionCloseAll).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.AutoRefresh, v => v.togAutoRefresh.IsChecked).DisposeWith(disposables);

            AppEvents.AppExitRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(_ => StorageUI())
              .DisposeWith(disposables);
        });

        RestoreUI();
    }

    private void BtnAutofitColumnWidth_Click(object? sender, RoutedEventArgs e)
    {
        AutofitColumnWidth();
    }

    private void AutofitColumnWidth()
    {
        try
        {
            foreach (var it in lstConnections.Columns)
            {
                it.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ClashConnectionsView", ex);
        }
    }

    private void btnClose_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ClashConnectionClose(false);
    }

    #region UI

    private void RestoreUI()
    {
        try
        {
            var lvColumnItem = _config.ClashUIItem?.ConnectionsColumnItem?.OrderBy(t => t.Index).ToList();
            if (lvColumnItem == null)
            {
                return;
            }

            var displayIndex = 0;
            foreach (var item in lvColumnItem)
            {
                foreach (var item2 in lstConnections.Columns)
                {
                    if (item2.Tag == null)
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
                            item2.Width = new DataGridLength(item.Width, DataGridLengthUnitType.Pixel);
                            item2.DisplayIndex = displayIndex++;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private void StorageUI()
    {
        try
        {
            List<ColumnItem> lvColumnItem = [];
            foreach (var item2 in lstConnections.Columns)
            {
                if (item2.Tag == null)
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
            _config.ClashUIItem.ConnectionsColumnItem = lvColumnItem;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    #endregion UI
}
