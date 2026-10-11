using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class RoutingRuleSettingWindow : WindowBase<RoutingRuleSettingViewModel>
{
    public RoutingRuleSettingWindow()
    {
        InitializeComponent();
        lstRules.HeadersVisibility = DataGridHeadersVisibility.All;
        lstRules.RowHeaderWidth = 40;
        lstRules.LoadingRow += (_, args) => args.Row.Header = (args.Row.Index + 1).ToString(CultureInfo.CurrentCulture);

        Loaded += Window_Loaded;
        btnCancel.Click += (s, e) => Close();
        KeyDown += RoutingRuleSettingWindow_KeyDown;
        lstRules.SelectionChanged += lstRules_SelectionChanged;
        lstRules.DoubleTapped += LstRules_DoubleTapped;
        menuRuleSelectAll.Click += menuRuleSelectAll_Click;
        //btnBrowseCustomIcon.Click += btnBrowseCustomIcon_Click;
        btnBrowseCustomRulesetPath4Singbox.Click += btnBrowseCustomRulesetPath4Singbox_ClickAsync;
        btnRuleEdit.Click += async (_, _) => { if (ViewModel != null) await ViewModel.RuleEditAsync(false); };
        var dock = (DockPanel)Content!;
        dock.Children.Remove(basicOptions);
        dock.Children.Insert(0, basicOptions);
        for (var index = 0; index < lstRules.Columns.Count; index++)
        {
            var column = lstRules.Columns[index];
            column.MinWidth = index == 0 ? 48 : 80;
            column.IsVisible = index < 4 || index == 8;
            column.Width = index == 0 ? new DataGridLength(48) :
                new DataGridLength(index is 1 or 8 ? 2 : 1, DataGridLengthUnitType.Star);
        }
        btnColumns.Click += (_, _) =>
        {
            var panel = new StackPanel { Spacing = 8 };
            foreach (var column in lstRules.Columns.Skip(1))
            {
                var choice = new CheckBox { Content = column.Header, IsChecked = column.IsVisible };
                choice.IsCheckedChanged += (_, _) => column.IsVisible = choice.IsChecked == true;
                panel.Children.Add(choice);
            }
            new Flyout { Content = panel }.ShowAt(btnColumns);
        };

        cmbdomainStrategy.ItemsSource = Global.DomainStrategies.AppendEmpty();
        cmbdomainStrategy4Singbox.ItemsSource = Global.DomainStrategies4Sbox;

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.RulesItems, v => v.lstRules.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSource, v => v.lstRules.SelectedItem).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SelectedRouting.Remarks, v => v.txtRemarks.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting.DomainStrategy, v => v.cmbdomainStrategy.SelectedValue).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting.DomainStrategy4Singbox, v => v.cmbdomainStrategy4Singbox.SelectedValue).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SelectedRouting.Url, v => v.txtUrl.Text).DisposeWith(disposables);
            //this.Bind(ViewModel, vm => vm.SelectedRouting.CustomIcon, v => v.txtCustomIcon.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting.CustomRulesetPath4Singbox, v => v.txtCustomRulesetPath4Singbox.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting.Sort, v => v.txtSort.Text).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.RuleAddCmd, v => v.menuRuleAdd).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportRulesFromFileCmd, v => v.menuImportRulesFromFile).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportRulesFromClipboardCmd, v => v.menuImportRulesFromClipboard).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportRulesFromUrlCmd, v => v.menuImportRulesFromUrl).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.RuleAddCmd, v => v.menuRuleAdd2).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RuleRemoveCmd, v => v.menuRuleRemove).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RuleExportSelectedCmd, v => v.menuRuleExportSelected).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveTopCmd, v => v.menuMoveTop).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveUpCmd, v => v.menuMoveUp).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveDownCmd, v => v.menuMoveDown).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveBottomCmd, v => v.menuMoveBottom).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RuleRemoveCmd, v => v.btnRuleRemove).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveUpCmd, v => v.btnMoveUp).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveDownCmd, v => v.btnMoveDown).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);

            ViewModel.ShowYesNoInteraction.RegisterHandler(async interaction =>
            {
                var message = interaction.Input;
                var result = await UI.ShowYesNo(message, message == ResUI.RemoveServer ? WorkspaceText.Delete : null, message == ResUI.RemoveServer);
                interaction.SetOutput(result == ButtonResult.Yes);
            }).DisposeWith(disposables);

            ViewModel.SetClipboardDataInteraction.RegisterHandler(async interaction =>
            {
                var strData = interaction.Input;
                await AvaUtils.SetClipboardData(this, strData);
                interaction.SetOutput(RxVoid.Default);
            }).DisposeWith(disposables);

            ViewModel.ReadTextFromClipboardInteraction.RegisterHandler(async interaction =>
            {
                var result = await AvaUtils.GetClipboardData(this);
                interaction.SetOutput(result);
            }).DisposeWith(disposables);

            ViewModel.BrowseRulesFileInteraction.RegisterHandler(async interaction =>
            {
                var fileName = await UI.OpenFileDialog(null);
                interaction.SetOutput(fileName);
            }).DisposeWith(disposables);
        });
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        txtRemarks.Focus();
    }

    private void RoutingRuleSettingWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!lstRules.IsKeyboardFocusWithin) return;
        if (e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta)
        {
            if (e.Key == Key.A)
            {
                lstRules.SelectAll();
            }
            else if (e.Key == Key.C)
            {
                ViewModel?.RuleExportSelectedAsync();
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.T:
                    ViewModel?.MoveRule(EMove.Top);
                    break;

                case Key.U:
                    ViewModel?.MoveRule(EMove.Up);
                    break;

                case Key.D:
                    ViewModel?.MoveRule(EMove.Down);
                    break;

                case Key.B:
                    ViewModel?.MoveRule(EMove.Bottom);
                    break;

                case Key.Delete:
                case Key.Back:
                    ViewModel?.RuleRemoveAsync();
                    break;
            }
        }
    }

    private void lstRules_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.SelectedSources = lstRules.SelectedItems.Cast<RulesItemModel>().ToList();
        }
    }

    private void LstRules_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        ViewModel?.RuleEditAsync(false);
    }

    private void menuRuleSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        lstRules.SelectAll();
    }

    //private async void btnBrowseCustomIcon_Click(object? sender, RoutedEventArgs e)
    //{
    //    var fileName = await UI.OpenFileDialog(this, FilePickerFileTypes.ImagePng);
    //    if (fileName.IsNullOrEmpty())
    //    {
    //        return;
    //    }

    //    txtCustomIcon.Text = fileName;
    //}

    private async void btnBrowseCustomRulesetPath4Singbox_ClickAsync(object? sender, RoutedEventArgs e)
    {
        var fileName = await UI.OpenFileDialog(null);
        if (fileName.IsNullOrEmpty())
        {
            return;
        }

        txtCustomRulesetPath4Singbox.Text = fileName;
    }

    private void linkCustomRulesetPath4Singbox(object? sender, RoutedEventArgs e)
    {
        ProcUtils.ProcessStart("https://github.com/2dust/v2rayCustomRoutingList/blob/master/singbox_custom_ruleset_example.json");
    }
}
