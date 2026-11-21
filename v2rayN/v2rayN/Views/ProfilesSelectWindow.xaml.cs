using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using v2rayN.Base;

namespace v2rayN.Views;

public partial class ProfilesSelectWindow
{
    public ProfilesSelectWindow()
    {
        InitializeComponent();
        lstGroup.MaxHeight = Math.Floor(SystemParameters.WorkArea.Height * 0.20 / 40) * 40;

        routeMode.SelectionChanged += (_, _) => { if (ViewModel?.IsRoutePicker == true) ViewModel.FollowSubscription = routeMode.SelectedIndex == 1; };
        clearRoute.Click += (_, _) => ViewModel?.ClearRouteChoices();
        routeAll.Click += (_, _) => ViewModel?.SelectVisible(routeAll.IsChecked == true);
        btnAutofitColumnWidth.Click += BtnAutofitColumnWidth_Click;
        txtServerFilter.PreviewKeyDown += TxtServerFilter_PreviewKeyDown;
        lstProfiles.PreviewKeyDown += LstProfiles_PreviewKeyDown;
        lstProfiles.SelectionChanged += LstProfiles_SelectionChanged;
        lstProfiles.LoadingRow += LstProfiles_LoadingRow;

        this.WhenActivated(disposables =>
        {
            if (ViewModel.IsRoutePicker)
            {
                routeControls.Visibility = Visibility.Visible;
                routeMode.ItemsSource = new[] { ViewModel.ManualLabel, ViewModel.FollowLabel };
                routeMode.SelectedIndex = ViewModel.FollowSubscription ? 1 : 0;
                routeColumn.DisplayIndex = 0;
                this.WhenAnyValue(v => v.ViewModel.FollowSubscription).Subscribe(follow =>
                {
                    routeColumn.Visibility = clearRoute.Visibility = follow ? Visibility.Collapsed : Visibility.Visible;
                }).DisposeWith(disposables);
                this.OneWayBind(ViewModel, vm => vm.AllVisibleChecked, v => v.routeAll.IsChecked).DisposeWith(disposables);
            }
            this.OneWayBind(ViewModel, vm => vm.ProfileItems, v => v.lstProfiles.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedProfile, v => v.lstProfiles.SelectedItem).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.ServerFilter, v => v.txtServerFilter.Text).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);

            this.WhenAnyValue(x => x.ViewModel.MultiSelect)
                .Subscribe(AllowMultiSelect)
                .DisposeWith(disposables);

            ViewModel.ProfilesFocusInteraction.RegisterHandler(interaction =>
            {
                lstProfiles.Focus();
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);
        });

        WindowsUtils.SetDarkBorder(this, AppManager.Instance.Config.UiItem.CurrentTheme);
    }

    private void AllowMultiSelect(bool allow)
    {
        if (allow)
        {
            lstProfiles.SelectionMode = DataGridSelectionMode.Extended;
            lstProfiles.SelectedItems.Clear();
        }
        else
        {
            lstProfiles.SelectionMode = DataGridSelectionMode.Single;
        }
    }

    // Expose ConfigType filter controls to callers
    public void SetConfigTypeFilter(IEnumerable<EConfigType> types, bool exclude = false)
        => ViewModel?.SetConfigTypeFilter(types, exclude);

    #region Event

    private void LstProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.SelectedProfiles = lstProfiles.SelectedItems.Cast<ProfileItemModel>().ToList();
        }
    }

    private void LstProfiles_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.Header = $" {e.Row.GetIndex() + 1}";
    }

    private void LstProfiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.IsRoutePicker != true) ViewModel?.SelectFinish();
    }

    private void LstProfiles_ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGridColumnHeader colHeader || colHeader.TabIndex < 0 || colHeader.Column == null)
        {
            return;
        }

        if (colHeader.Column is MyDGTextColumn textColumn) ViewModel?.SortServer(textColumn.ExName);
    }

    private void menuSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { IsRoutePicker: true, FollowSubscription: false }) ViewModel.SelectVisible(true);
        if (ViewModel?.MultiSelect != true)
        {
            return;
        }
        lstProfiles.SelectAll();
    }

    private void LstProfiles_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            switch (e.Key)
            {
                case Key.A:
                    menuSelectAll_Click(null, null);
                    e.Handled = true;
                    break;
            }
        }
        else
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                ViewModel?.SelectFinish();
                e.Handled = true;
            }
        }
    }

    private void BtnAutofitColumnWidth_Click(object sender, RoutedEventArgs e)
    {
        AutofitColumnWidth();
    }

    private void AutofitColumnWidth()
    {
        try
        {
            foreach (var it in lstProfiles.Columns)
            {
                it.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ProfilesView", ex);
        }
    }

    private void TxtServerFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            ViewModel?.RefreshServers();
            e.Handled = true;
        }
    }

    #endregion Event
}
