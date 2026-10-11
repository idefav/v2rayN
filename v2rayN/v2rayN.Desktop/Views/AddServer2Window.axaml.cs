using Avalonia.LogicalTree;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class AddServer2Window : WindowBase<AddServer2ViewModel>
{
    private JsonEditor ConfigurationPreview { get; } = new() { MinHeight = 320 };
    private TextBlock ReadError { get; } = new() { TextWrapping = TextWrapping.Wrap };
    public AddServer2Window()
    {
        InitializeComponent();
        txtAddress.IsReadOnly = true;
        var preview = ConfigurationPreview;
        var readError = ReadError;
        var heading = new TextBlock { Text = WorkspaceText.T("配置预览 / JSON 编辑", "Configuration preview / JSON editor") };
        heading.Classes.Add("sectionTitle");
        configurationBody.Children.Add(heading);
        configurationBody.Children.Add(readError);
        configurationBody.Children.Add(preview);
        Loaded += (_, _) =>
        {
            preview.IsReadOnly = ViewModel?.SelectedSource.ConfigType != EConfigType.Outbound;
            preview.ShowValidation = !preview.IsReadOnly || ViewModel?.SelectedSource.Address.EndsWith(".json", StringComparison.OrdinalIgnoreCase) == true;
        };

        Loaded += Window_Loaded;
        btnCancel.Click += (s, e) => Close();

        this.WhenActivated(disposables =>
        {
            ViewModel.UseConfigurationDraft = true;
            this.WhenAnyValue(v => v.ViewModel.IsLoadingConfiguration).Subscribe(busy => btnSave.IsEnabled = !busy).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ConfigurationDraft, v => v.ConfigurationPreview.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.ConfigurationLoadError, v => v.ReadError.Text).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.ConfigurationLoadError).Subscribe(error => ReadError.IsVisible = !string.IsNullOrWhiteSpace(error)).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.SelectedSource).SubscribeAsync(async _ => await ViewModel.LoadConfigurationDraftAsync()).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.SelectedSource)
                .KeepNotNull()
                .Subscribe(InitializeData)
                .DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SelectedSource.Remarks, v => v.txtRemarks.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSource.Address, v => v.txtAddress.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.CoreType, v => v.cmbCoreType.SelectedValue).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSource.DisplayLog, v => v.togDisplayLog.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSource.PreSocksPort, v => v.txtPreSocksPort.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.IsSingboxEndpoint, v => v.togSingBoxEndpoint.IsChecked).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.BrowseServerCmd, v => v.btnBrowse).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.EditServerCmd, v => v.btnEdit).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SaveServerCmd, v => v.btnSave).DisposeWith(disposables);

            ViewModel.BrowseConfigFileInteraction.RegisterHandler(async interaction =>
            {
                var fileName = await UI.OpenFileDialog(null);
                interaction.SetOutput(fileName);
            }).DisposeWith(disposables);
        });
    }

    private void InitializeData(ProfileItem profileItem)
    {
        if (profileItem.ConfigType is EConfigType.Custom)
        {
            Title = ResUI.menuAddCustomServer;
            cmbCoreType.ItemsSource = Utils.GetEnumNames<ECoreType>().Where(t => t != nameof(ECoreType.v2rayN)).ToList();
            gridCustomServer.IsVisible = true;
            gridCustomOutbound.IsVisible = false;
        }
        else if (profileItem.ConfigType is EConfigType.Outbound)
        {
            Title = ResUI.menuAddCustomOutboundServer;
            cmbCoreType.ItemsSource = Global.CoreTypes;
            gridCustomServer.IsVisible = false;
            gridCustomOutbound.IsVisible = true;
        }
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        txtRemarks.Focus();
    }
}
