using DialogHostAvalonia;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class QrcodeView : UserControl
{
    public QrcodeView()
    {
        InitializeComponent();
        DialogPresentation.ConstrainToWindow(this);
        if (Design.IsDesignMode)
        {
            //const string url = Global.AppName;
            const string url = Global.SystemProxyExceptionsWindows;
            txtContent.Text = url;
            imgQrcode.Source = GetQRCode(url);
        }
    }

    public QrcodeView(string? url, string? dialogIdentifier = null)
    {
        InitializeComponent();
        DialogPresentation.ConstrainToWindow(this);

        btnCopyLink.Click += async (_, _) => await AvaUtils.SetClipboardData(this, txtContent.Text ?? "");
        btnClose.Click += (_, _) => DialogHost.Close(dialogIdentifier);
        txtContent.Text = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var share))
        {
            txtNodeSummary.Text = share.Scheme.ToUpperInvariant() + (share.Fragment.Length > 1 ? " · " + Uri.UnescapeDataString(share.Fragment[1..]) : "");
            txtNodeSummary.IsVisible = true;
        }
        imgQrcode.Source = GetQRCode(url);

        txtContent.GotFocus += (_, _) => Dispatcher.UIThread.Post(() => { txtContent.SelectAll(); });
    }

    private Bitmap? GetQRCode(string? url)
    {
        try
        {
            var bytes = QRCodeUtils.GenQRCode(url);
            return ByteToBitmap(bytes);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("GetQRCode", ex);
            return null;
        }
    }

    private Bitmap? ByteToBitmap(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }
}
