using v2rayN.Desktop.Common;
using v2rayN.Desktop.Base;

namespace v2rayN.Desktop.Views;

public partial class BackupAndRestoreView : PageBase<BackupAndRestoreViewModel>
{
    public BackupAndRestoreView()
    {
        InitializeComponent();
        txtLocalHelp.Text = WorkspaceText.T("导出配置为 ZIP 文件，或从已有备份恢复。", "Export configuration to a ZIP file or restore an existing backup.");
        txtRemoteHelp.Text = WorkspaceText.T("配置 WebDAV 后可检查连接、备份和恢复。凭据保存前请先确认地址。", "Configure WebDAV to check the connection, back up and restore. Verify the address before saving credentials.");
        TablePresentation.TextIconButton(menuLocalBackup, ResUI.menuLocalBackup, "M4,4 L20,4 20,20 4,20 Z M8,4 L8,10 16,10 16,4 M8,20 L8,14 16,14 16,20");
        TablePresentation.TextIconButton(menuLocalRestore, ResUI.menuLocalRestore, "M4,10 A8,8 0 1 1 5,18 M4,4 L4,10 10,10");
        menuLocalBackup.Click += MenuLocalBackup_Click;
        menuLocalRestore.Click += MenuLocalRestore_Click;
        btnCancel.Click += (_, _) => Close();

        this.WhenActivated(disposables =>
        {
            this.Bind(ViewModel, vm => vm.OperationMsg, v => v.txtMsg.Text).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SelectedSource.Url, v => v.txtWebDavUrl.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSource.UserName, v => v.txtWebDavUserName.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSource.Password, v => v.txtWebDavPassword.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSource.DirName, v => v.txtWebDavDirName.Text).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.WebDavCheckCmd, v => v.menuWebDavCheck).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.RemoteBackupCmd, v => v.menuRemoteBackup).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoteRestoreCmd, v => v.menuRemoteRestore).DisposeWith(disposables);
        });
    }

    private async void MenuLocalBackup_Click(object? sender, RoutedEventArgs e)
    {
        var fileName = await UI.SaveFileDialog("Zip|*.zip");
        if (fileName.IsNullOrEmpty())
        {
            return;
        }

        ViewModel?.LocalBackup(fileName);
    }

    private async void MenuLocalRestore_Click(object? sender, RoutedEventArgs e)
    {
        var fileName = await UI.OpenFileDialog(null);
        if (fileName.IsNullOrEmpty())
        {
            return;
        }

        ViewModel?.LocalRestore(fileName);
    }
}
