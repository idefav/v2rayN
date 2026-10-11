using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class SshKeySetupWindow : WindowBase<SshKeySetupViewModel>
{
    private bool _allowClose;

    public SshKeySetupWindow() => InitializeComponent();

    public SshKeySetupWindow(SshKeySetupViewModel model) : this()
    {
        ViewModel = model;
        DataContext = model;
        btnCancel.Click += (_, _) => model.Cancel();
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            model.Cancel();
        };
        model.RequestClose += OnRequestClose;
        Closed += (_, _) => model.RequestClose -= OnRequestClose;
        this.WhenActivated(disposables =>
        {
            var stages = new[] { WorkspaceText.T("1 验证登录", "1 Verify login"), WorkspaceText.T("2 安装公钥", "2 Install key"), WorkspaceText.T("3 再次验证", "3 Verify key") };
            setupStages.Children.Clear();
            foreach (var stage in stages) setupStages.Children.Add(new TextBlock { Text = stage, Margin = new Thickness(0, 0, 20, 8) });
            this.WhenAnyValue(v => v.ViewModel.CurrentStage).Subscribe(stage =>
            {
                var active = stage switch { "connecting" => 0, "generating" or "installing" => 1, "verifying" or "verified" => 2, _ => -1 };
                for (var i = 0; i < setupStages.Children.Count; i++)
                {
                    var label = (TextBlock)setupStages.Children[i];
                    if (i == active) label.Foreground = new SolidColorBrush(Color.Parse("#1677FF"));
                    else label.ClearValue(TextBlock.ForegroundProperty);
                    label.FontWeight = i == active ? FontWeight.SemiBold : FontWeight.Normal;
                }
            }).DisposeWith(disposables);
            txtTarget.Text = model.TargetDescription;
            txtDiscovery.Text = model.DiscoveryMessage;
            listKeys.ItemsSource = model.Candidates;
            listKeys.IsVisible = model.Candidates.Count > 0;
            System.Collections.Specialized.NotifyCollectionChangedEventHandler keysChanged = (_, _) => listKeys.IsVisible = model.Candidates.Count > 0;
            model.Candidates.CollectionChanged += keysChanged;
            new ActionDisposable(() => model.Candidates.CollectionChanged -= keysChanged).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedKey, v => v.listKeys.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.GenerateNew, v => v.radioGenerate.IsChecked).DisposeWith(disposables);
            radioReuse.IsCheckedChanged += ReuseSelected;
            new ActionDisposable(() => radioReuse.IsCheckedChanged -= ReuseSelected).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.GenerateNew).Subscribe(generate =>
            {
                radioReuse.IsChecked = !generate;
                confirmationPanel.IsVisible = generate;
                listKeys.IsEnabled = !generate;
            }).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.NeedsPassword).Subscribe(needed =>
                passwordPanel.IsVisible = needed).DisposeWith(disposables);
            this.WhenAnyValue(v => v.ViewModel.IsBusy).Subscribe(busy =>
            {
                form.IsEnabled = !busy;
                btnRun.IsEnabled = !busy;
            }).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.Status, v => v.txtStatus.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.PublicKeyText, v => v.txtPublicKey.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.KeyPassphrase, v => v.txtPassphrase.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ConfirmPassphrase, v => v.txtConfirmation.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.InstallationPassword, v => v.txtPassword.Text).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RunCmd, v => v.btnRun).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.BrowseCmd, v => v.btnBrowse).DisposeWith(disposables);
            model.BrowseInteraction.RegisterHandler(async interaction =>
                interaction.SetOutput(await UI.OpenFileDialog(null))).DisposeWith(disposables);
        });
    }

    private void ReuseSelected(object? sender, RoutedEventArgs e)
    {
        if (radioReuse.IsChecked == true) ViewModel!.GenerateNew = false;
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        _allowClose = true;
        Close();
    }

}
