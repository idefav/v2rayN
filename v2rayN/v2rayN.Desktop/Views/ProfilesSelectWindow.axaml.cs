using Avalonia.VisualTree;
using v2rayN.Desktop.Base;

namespace v2rayN.Desktop.Views;

public partial class ProfilesSelectWindow : WindowBase<ProfilesSelectViewModel>
{
    public ProfilesSelectWindow()
    {
        InitializeComponent();
        Common.TablePresentation.AddSelectionColumn(lstProfiles);
        lstProfiles.HeadersVisibility = DataGridHeadersVisibility.Column;
        foreach (var column in lstProfiles.Columns)
        {
            if (column.Tag == null) continue;
            column.IsVisible = column.Tag.ToString() is "Remarks" or "ConfigType" or "Address" or "Delay" or "Speed";
            column.MinWidth = 72;
            column.Width = new DataGridLength(column.Tag.ToString() is "Remarks" or "Address" ? 2 : 1, DataGridLengthUnitType.Star);
        }
        var order = new[] { "Remarks", "ConfigType", "Address", "Delay", "Speed" };
        for (var index = 0; index < order.Length; index++)
            lstProfiles.Columns.Single(column => column.Tag?.ToString() == order[index]).DisplayIndex = index + 1;
        Common.TablePresentation.FitHeaders(lstProfiles);
        var count = new TextBlock { Text = Common.WorkspaceText.Selected(0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        ((StackPanel)btnSave.Parent!).Children.Insert(0, count);
        lstProfiles.SelectionChanged += (_, _) => count.Text = Common.WorkspaceText.Selected(lstProfiles.SelectedItems.Count);
        Common.TablePresentation.TextButton(btnAutofitColumnWidth, Common.WorkspaceText.Columns);
        btnAutofitColumnWidth.Click -= BtnAutofitColumnWidth_Click;
        btnAutofitColumnWidth.Click += (_, _) =>
        {
            var choices = new StackPanel { Spacing = 8 };
            foreach (var column in lstProfiles.Columns.Where(c => c.Tag != null))
            {
                var label = column.Header is TextBlock text ? text.Text : column.Header?.ToString();
                var choice = new CheckBox { Content = label, IsChecked = column.IsVisible };
                choice.IsCheckedChanged += (_, _) => column.IsVisible = choice.IsChecked == true;
                choices.Children.Add(choice);
            }
            new Flyout { Content = choices }.ShowAt(btnAutofitColumnWidth);
        };

        btnCancel.Click += (_, _) => Close();
        txtServerFilter.KeyDown += TxtServerFilter_KeyDown;
        lstProfiles.KeyDown += LstProfiles_KeyDown;
        lstProfiles.SelectionChanged += LstProfiles_SelectionChanged;
        lstProfiles.LoadingRow += LstProfiles_LoadingRow;
        lstProfiles.Sorting += LstProfiles_Sorting;
        lstProfiles.DoubleTapped += LstProfiles_DoubleTapped;

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.ProfileItems, v => v.lstProfiles.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedProfile, v => v.lstProfiles.SelectedItem).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SelectedSub, v => v.lstGroup.SelectedItem).DisposeWith(disposables);
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

    private void LstProfiles_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.SelectedProfiles = lstProfiles.SelectedItems.Cast<ProfileItemModel>().ToList();
        }
    }

    private void LstProfiles_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.Header = $" {e.Row.Index + 1}";
    }

    private void LstProfiles_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // Ignore double-taps on the column header area
        if (e.Source is Control src)
        {
            if (src.FindAncestorOfType<DataGridColumnHeader>() != null)
            {
                e.Handled = true;
                return;
            }

            // Only trigger selection when double-tapped on a data row or its child element
            if (src.FindAncestorOfType<DataGridRow>() != null)
            {
                ViewModel?.SelectFinish();
                e.Handled = true;
            }
        }
    }

    private void LstProfiles_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        // Custom sort to prevent unintended default behavior
        e.Handled = true;
        if (ViewModel != null && e.Column?.Tag?.ToString() != null)
        {
            ViewModel.SortServer(e.Column.Tag.ToString());
        }
    }

    private void LstProfiles_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta)
        {
            if (e.Key == Key.A)
            {
                if (ViewModel?.MultiSelect == true)
                {
                    lstProfiles.SelectAll();
                }
                e.Handled = true;
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

    private void BtnAutofitColumnWidth_Click(object? sender, RoutedEventArgs e)
    {
        AutofitColumnWidth();
    }

    private void AutofitColumnWidth()
    {
        try
        {
            foreach (var col in lstProfiles.Columns)
            {
                col.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
            }
        }
        catch
        {
        }
    }

    private void TxtServerFilter_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            ViewModel?.RefreshServers();
        }
    }
}
