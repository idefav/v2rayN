using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;
using Avalonia.Threading;
using Avalonia.Data;

namespace v2rayN.Desktop.Views;

public partial class AddGroupServerWindow : WindowBase<AddGroupServerViewModel>
{
    public AddGroupServerWindow()
    {
        InitializeComponent();
        var nameLabel = basicFields.Children.OfType<TextBlock>().First();
        var coreLabel = basicFields.Children.OfType<TextBlock>().Last();
        basicFields.Children.Remove(nameLabel);
        basicFields.Children.Remove(coreLabel);
        basicFields.Children.Remove(txtRemarks);
        basicFields.Children.Remove(cmbCoreType);
        foreach (var control in new Control[] { nameLabel, coreLabel, txtRemarks, cmbCoreType })
        {
            Grid.SetRow(control, 0); Grid.SetColumn(control, 0);
            control.Margin = new Thickness(0);
        }
        cmbCoreType.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        var nameField = new StackPanel { Spacing = 8, Children = { nameLabel, txtRemarks } };
        var coreField = new StackPanel { Spacing = 8, Children = { coreLabel, cmbCoreType } };
        basicFields.Children.Add(nameField); basicFields.Children.Add(coreField);
        basicFields.SizeChanged += (_, _) =>
        {
            var wide = basicFields.Bounds.Width >= 760;
            basicFields.ColumnDefinitions = new ColumnDefinitions(wide ? "*,24,*" : "*");
            basicFields.RowDefinitions = new RowDefinitions(wide ? "Auto,Auto" : "Auto,Auto,Auto");
            Grid.SetColumn(coreField, wide ? 2 : 0); Grid.SetRow(coreField, wide ? 0 : 1);
            Grid.SetRow(gridPolicyGroup, wide ? 1 : 2); Grid.SetColumnSpan(gridPolicyGroup, wide ? 3 : 1);
            coreField.Margin = wide ? new Thickness(0) : new Thickness(0, 12, 0, 0);
            gridPolicyGroup.Margin = new Thickness(0, 16, 0, 0);
        };

        Loaded += Window_Loaded;
        btnCancel.Click += (s, e) => Close();
        lstChild.SelectionChanged += LstChild_SelectionChanged;
        tabControl.SelectionChanged += TabControl_SelectionChanged;
        var automaticContent = (Control)automaticTab.Content!;
        automaticTab.Content = null;
        var workspace = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,240") };
        workspace.Children.Add(automaticContent);
        var manualPreview = new StackPanel { Spacing = 12 };
        var title = new TextBlock { Text = WorkspaceText.T("手动选择预览", "Manual selection preview") };
        title.Classes.Add("sectionTitle");
        manualPreview.Children.Add(title);
        var list = new ItemsControl();
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding("ChildItemsObs"));
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ProfileItem>((item, _) =>
            new TextBlock { Text = item?.Remarks, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8) });
        manualPreview.Children.Add(list);
        var manualCount = new TextBlock { TextWrapping = TextWrapping.Wrap };
        manualCount.Classes.Add("muted");
        manualPreview.Children.Insert(1, manualCount);
        manualCount.Bind(TextBlock.TextProperty, new Binding("ChildItemsObs.Count") { StringFormat = WorkspaceText.T("已选择 {0} 个节点", "{0} selected servers") });
        var previewFrame = new Border { Child = manualPreview, Padding = new Thickness(16, 24), BorderThickness = new Thickness(1, 0, 0, 0) };
        previewFrame.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceBorder"));
        Grid.SetColumn(previewFrame, 2);
        workspace.Children.Add(previewFrame);
        automaticTab.Content = workspace;
        workspace.SizeChanged += (_, _) =>
        {
            var narrow = workspace.Bounds.Width < 800;
            workspace.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 240);
            workspace.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 16);
            previewFrame.IsVisible = !narrow;
        };

        cmbCoreType.ItemsSource = Global.CoreTypes;
        cmbPolicyGroupType.ItemsSource = new List<string>
        {
            ResUI.TbLeastPing,
            ResUI.TbFallback,
            ResUI.TbRandom,
            ResUI.TbRoundRobin,
            ResUI.TbLeastLoad,
        };
        cmbFilter.ItemsSource = Global.PolicyGroupDefaultFilterList;

        this.WhenActivated(disposables =>
        {
            this.WhenAnyValue(v => v.ViewModel.SelectedSource)
                .KeepNotNull()
                .Subscribe(InitializeData)
                .DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SelectedSource.Remarks, v => v.txtRemarks.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.CoreType, v => v.cmbCoreType.SelectedValue).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.PolicyGroupType, v => v.cmbPolicyGroupType.SelectedValue).DisposeWith(disposables);
            //this.OneWayBind(ViewModel, vm => vm.SubItems, v => v.cmbSubChildItems.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSubItem, v => v.cmbSubChildItems.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Filter, v => v.cmbFilter.Text).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SelectedChild, v => v.lstChild.SelectedItem).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.AddCmd, v => v.menuAddChildServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveCmd, v => v.menuRemoveChildServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveTopCmd, v => v.menuMoveTop).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveUpCmd, v => v.menuMoveUp).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveDownCmd, v => v.menuMoveDown).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveBottomCmd, v => v.menuMoveBottom).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddCmd, v => v.btnAddChild).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveCmd, v => v.btnRemoveChild).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveUpCmd, v => v.btnMoveUp).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveDownCmd, v => v.btnMoveDown).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.SelectedSubItem, v => v.ViewModel.Filter, v => v.ViewModel.PolicyGroupType)
                .Throttle(TimeSpan.FromMilliseconds(200), RxSchedulers.MainThreadScheduler)
                .SubscribeAsync(async _ => await ViewModel.UpdatePreviewList()).DisposeWith(disposables);
            System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) =>
                Dispatcher.UIThread.Post(async () => { await ViewModel.UpdatePreviewList(); UpdateChainPreview(); });
            System.Collections.Specialized.NotifyCollectionChangedEventHandler previewChanged = (_, _) =>
                txtMatchCount.Text = WorkspaceText.Matched(ViewModel.AllProfilePreviewItemsObs.Count);
            ViewModel.ChildItemsObs.CollectionChanged += changed;
            ViewModel.AllProfilePreviewItemsObs.CollectionChanged += previewChanged;
            new ActionDisposable(() =>
            {
                ViewModel.ChildItemsObs.CollectionChanged -= changed;
                ViewModel.AllProfilePreviewItemsObs.CollectionChanged -= previewChanged;
            }).DisposeWith(disposables);
            txtMatchCount.Text = WorkspaceText.Matched(ViewModel.AllProfilePreviewItemsObs.Count);
            UpdateChainPreview();

            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);
        });

        // Context menu actions that require custom logic (Add, SelectAll)
        menuSelectAllChild.Click += (s, e) => lstChild.SelectAll();

        // Keyboard shortcuts when focus is within grid
        AddHandler(KeyDownEvent, AddGroupServerWindow_KeyDown, RoutingStrategies.Tunnel);
        lstChild.LoadingRow += LstChild_LoadingRow;
    }

    private void InitializeData(ProfileItem profileItem)
    {
        switch (profileItem.ConfigType)
        {
            case EConfigType.PolicyGroup:
                Title = ResUI.TbConfigTypePolicyGroup;
                break;

            case EConfigType.ProxyChain:
                Title = ResUI.TbConfigTypeProxyChain;
                gridPolicyGroup.IsVisible = false;
                if (tabControl.Items.Contains(automaticTab))
                {
                    tabControl.Items.Remove(automaticTab);
                }
                manualTab.Header = WorkspaceText.T("链路节点", "Chain servers");
                chainPreview.IsVisible = true;
                break;
        }
    }

    private void UpdateChainPreview()
    {
        var items = ViewModel?.ChildItemsObs ?? [];
        txtChainOrder.Text = items.Count == 0
            ? WorkspaceText.T("添加节点后，按表格顺序依次连接。", "Add servers to connect in table order.")
            : WorkspaceText.T("本机", "Local") + "  →  " + string.Join("  →  ", items.Select((p, i) => $"{i + 1}. {p.Remarks}")) + "  →  " + WorkspaceText.T("目标网站", "Destination");
    }

    private void LstChild_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.Header = $" {e.Row.Index + 1}";
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        txtRemarks.Focus();
    }

    private void AddGroupServerWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!lstChild.IsKeyboardFocusWithin)
        {
            return;
        }

        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (e.Key == Key.A)
            {
                lstChild.SelectAll();
                e.Handled = true;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.T:
                    ViewModel?.MoveServer(EMove.Top);
                    e.Handled = true;
                    break;

                case Key.U:
                    ViewModel?.MoveServer(EMove.Up);
                    e.Handled = true;
                    break;

                case Key.D:
                    ViewModel?.MoveServer(EMove.Down);
                    e.Handled = true;
                    break;

                case Key.B:
                    ViewModel?.MoveServer(EMove.Bottom);
                    e.Handled = true;
                    break;

                case Key.Delete:
                case Key.Back:
                    ViewModel?.ChildRemoveAsync();
                    e.Handled = true;
                    break;
            }
        }
    }

    private void LstChild_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.SelectedChildren = lstChild.SelectedItems.Cast<ProfileItem>().ToList();
        }
    }

    private async void TabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (e.Source is not TabControl tc)
            {
                return;
            }
            if (!(tc.SelectedIndex == tc.Items.Count - 1 && tc.Items.Count > 0))
            {
                return;
            }
            if (ViewModel == null)
            {
                return;
            }

            await ViewModel.UpdatePreviewList();
        }
        catch
        {
            // ignored
        }
    }
}
