using DialogHostAvalonia;
using Avalonia.Automation;
using Avalonia.VisualTree;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class SudoPasswordInputView : UserControl
{
    private readonly Func<string, CancellationToken, Task<bool>> _validate;
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(AppManager.Instance.ExitToken);
    private bool _closed;
    private bool _disposed;
    private DialogOverlayPopupHost? _popup;
    public string? DialogIdentifier { get; set; }

    public SudoPasswordInputView() : this(CoreAdminManager.ValidateAuthorizationAsync) { }

    internal SudoPasswordInputView(Func<string, CancellationToken, Task<bool>> validate)
    {
        _validate = validate;
        InitializeComponent();
        DialogPresentation.ConstrainToWindow(this);
        authorizationIcon.Content = WorkspaceIcons.Create("tun", 28);
        txtAuthorizationTitle.Text = WorkspaceText.T("允许启用 TUN？", "Allow TUN to start?");
        txtAuthorizationPurpose.Text = WorkspaceText.T("创建虚拟网络接口需要管理员授权。", "Creating a virtual network interface requires administrator authorization.");
        txtAuthorizationPrivacy.Text = WorkspaceText.T("密码仅在本次应用会话中使用，不写入配置。", "The password is kept only for this app session and is never written to configuration.");
        btnSave.Content = WorkspaceText.T("授权", "Authorize");
        AutomationProperties.SetName(txtPassword, ResUI.TbSettingsLinuxSudoPassword);
        Loaded += (_, _) =>
        {
            _popup = this.GetVisualAncestors().OfType<DialogOverlayPopupHost>().FirstOrDefault();
            if (_popup != null) _popup.GotFocus += PopupGotFocus;
            FocusPassword();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_popup != null) _popup.GotFocus -= PopupGotFocus;
            _popup = null;
            _closed = true;
            if (!_disposed)
            {
                _disposed = true;
                _lifetime.Cancel();
                _lifetime.Dispose();
            }
            txtPassword.Text = string.Empty;
        };
        btnSave.Click += async (_, _) => await SavePasswordAsync();
        btnCancel.Click += (_, _) => Close(null);
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(null); }
            else if (e.Key == Key.Enter && btnSave.IsEnabled) { e.Handled = true; await SavePasswordAsync(); }
        };
    }

    private void PopupGotFocus(object? sender, RoutedEventArgs e)
    {
        // AppKit can restore focus to the popup after a hidden window activates.
        // Redirect only that container focus, never a user's focus on another control.
        if (ReferenceEquals(e.Source, _popup)) FocusPassword();
    }

    private void FocusPassword() => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && txtPassword.IsEnabled) txtPassword.Focus();
    }, DispatcherPriority.Loaded);

    private void Close(string? password)
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        txtPassword.Text = string.Empty;
        DialogHost.Close(DialogIdentifier, password);
    }

    private async Task SavePasswordAsync()
    {
        if (_closed || !btnSave.IsEnabled) return;
        if (txtPassword.Text.IsNullOrEmpty())
        {
            txtPassword.Focus();
            return;
        }

        var password = txtPassword.Text;
        txtPassword.IsEnabled = false;
        txtPassword.Text = string.Empty;
        btnSave.IsEnabled = false;
        txtAuthorizationError.IsVisible = false;
        txtAuthorizationStatus.Text = WorkspaceText.T("正在验证授权…", "Verifying authorization…");
        txtAuthorizationStatus.IsVisible = true;
        try
        {
            var valid = await _validate(password, _lifetime.Token);
            if (_closed) return;
            if (valid) Close(password);
            else
            {
                txtAuthorizationError.Text = ResUI.SudoIncorrectPasswordTip;
                txtAuthorizationError.IsVisible = true;
                txtPassword.IsEnabled = true;
                txtPassword.Focus();
            }
        }
        catch (OperationCanceledException) { if (!_closed) Close(null); }
        catch (Exception ex)
        {
            Logging.SaveLog("SudoPassword", ex);
            if (!_closed)
            {
                txtAuthorizationError.Text = WorkspaceText.T("授权验证失败，请重试。", "Authorization could not be verified. Please retry.");
                txtAuthorizationError.IsVisible = true;
            }
        }
        finally
        {
            if (!_closed) { btnSave.IsEnabled = true; txtPassword.IsEnabled = true; txtAuthorizationStatus.IsVisible = false; }
        }
    }
}
