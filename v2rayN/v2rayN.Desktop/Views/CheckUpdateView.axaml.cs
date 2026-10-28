using Avalonia.Data;
using Avalonia.Data.Converters;
namespace v2rayN.Desktop.Views;

public partial class CheckUpdateView : ReactiveUserControl<CheckUpdateViewModel>
{
    public CheckUpdateView()
    {
        InitializeComponent();
        ((DataGridTextColumn)lstCheckUpdates.Columns[3]).Binding = new Binding(nameof(CheckUpdateModel.Remarks))
        {
            Converter = new FuncValueConverter<string?, string>(text => string.IsNullOrWhiteSpace(text) || text == ResUI.menuCheckUpdate
                ? Common.WorkspaceText.T("未检查", "Not checked") : text)
        };

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.CheckUpdateModels, v => v.lstCheckUpdates.ItemsSource).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.EnableUpdateViaProxy, v => v.togEnableUpdateViaProxy.IsChecked).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CheckOnlyCmd, v => v.btnCheckOnly).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CheckUpdateCmd, v => v.btnCheckUpdate).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SourceSettingsCmd, v => v.btnSources).DisposeWith(disposables);
            ViewModel!.SourceSettingsRequested.AsObservable().SubscribeAsync(async _ =>
            {
                if (TopLevel.GetTopLevel(this) is MainWindow main) await main.OpenSettingsAsync("Sources");
            }).DisposeWith(disposables);
        });
    }

}
