using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using ServiceLib.Services;
namespace v2rayN.Desktop.Views;

public partial class CheckUpdateView : ReactiveUserControl<CheckUpdateViewModel>
{
    private readonly Dictionary<ECoreType, Task<string>> _versions = new();
    public CheckUpdateView()
    {
        InitializeComponent();
        ((DataGridTextColumn)lstCheckUpdates.Columns[3]).Binding = new Binding(nameof(CheckUpdateModel.Remarks))
        {
            Converter = new FuncValueConverter<string?, string>(text => string.IsNullOrWhiteSpace(text) || text == ResUI.menuCheckUpdate
                ? Common.WorkspaceText.T("未检查", "Not checked") : text)
        };
        ((DataGridTemplateColumn)lstCheckUpdates.Columns[2]).CellTemplate = new FuncDataTemplate<CheckUpdateModel>((model, _) =>
        {
            var label = new TextBlock { Text = "—", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            label.Loaded += async (_, _) =>
            {
                if (model?.CoreType == ECoreType.v2rayN) { label.Text = Utils.GetVersionInfo(); return; }
                if (model?.CoreType is not ECoreType type) return;
                if (!_versions.TryGetValue(type, out var read)) _versions[type] = read = ReadInstalledVersionAsync(type);
                label.Text = await read;
            };
            return label;
        });

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.CheckUpdateModels, v => v.lstCheckUpdates.ItemsSource).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.EnableUpdateViaProxy, v => v.togEnableUpdateViaProxy.IsChecked).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CheckOnlyCmd, v => v.btnCheckOnly).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CheckUpdateCmd, v => v.btnCheckUpdate).DisposeWith(disposables);
        });
    }

    private static async Task<string> ReadInstalledVersionAsync(ECoreType type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var service = new UpdateService(AppManager.Instance.Config, (_, _) => Task.CompletedTask);
        var version = await service.GetCoreVersion(type, timeout.Token);
        var value = version?.ToString();
        return string.IsNullOrWhiteSpace(value) || value == "0.0.0" ? "—" : value;
    }
}
