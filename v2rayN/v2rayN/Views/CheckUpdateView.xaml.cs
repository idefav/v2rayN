namespace v2rayN.Views;

public partial class CheckUpdateView
{
    public CheckUpdateView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.CheckUpdateModels, v => v.lstCheckUpdates.ItemsSource).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.EnableUpdateViaProxy, v => v.togEnableUpdateViaProxy.IsChecked).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CheckOnlyCmd, v => v.btnCheckOnly).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CheckUpdateCmd, v => v.btnCheckUpdate).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SourceSettingsCmd, v => v.btnSources).DisposeWith(disposables);
            ViewModel!.SourceSettingsRequested.AsObservable().SubscribeAsync(async _ =>
            {
                var settings = new OptionSettingViewModel { InitialSection = 5 };
                var saved = await AppManager.Instance.WindowDialog.ShowDialogAsync(settings);
                if (saved == true && !settings.OnlySourcesChanged) ViewModel.ReloadRequested.Publish();
            }).DisposeWith(disposables);
        });
    }
}
