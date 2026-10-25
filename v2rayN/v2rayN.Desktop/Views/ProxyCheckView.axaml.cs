using Avalonia.Platform.Storage;
using ServiceLib.Models.Dto;

namespace v2rayN.Desktop.Views;

public partial class ProxyCheckView : UserControl
{
    public ProxyCheckViewModel Model { get; }
    public ProxyCheckView() : this(new ProxyCheckViewModel()) { }
    internal ProxyCheckView(ProxyCheckViewModel model)
    {
        InitializeComponent(); DataContext = Model = model;
        static string T(string zh, string en) => ProxyCheckText.T(zh, en);
        start.Content = T("开始检查", "Start checks"); cancel.Content = T("取消", "Cancel");
        retry.Content = T("重试失败项", "Retry failed"); saveBaseline.Content = T("保存为基线", "Save as baseline");
        loadBaseline.Content = T("选择基线", "Choose baseline"); export.Content = T("导出报告", "Export report");
        advanced.Header = T("高级选项", "Advanced options");
        expectedLabel.Text = T("预期出口 IP / CIDR（多个值以逗号分隔）", "Expected egress IP / CIDR (comma-separated)");
        rulesLabel.Text = T("按目标设置分流预期（每行：主机名或 host:port=IP/CIDR，优先于全局预期）", "Routing expectations (one hostname or host:port=IP/CIDR per line; overrides global expectation)");
        dnsLabel.Text = T("公共 DNS（host:port）", "Public DNS (host:port)");
        localDnsLabel.Text = T("本地 DNS（留空自动发现；系统解析单独检查）", "Local DNS (empty to discover; system resolver is checked separately)");
        samplesLabel.Text = T("采样次数", "Samples"); timeoutLabel.Text = T("超时 / 秒", "Timeout / s"); h3TimeoutLabel.Text = T("HTTP3 超时 / 秒", "HTTP3 timeout / s");
        limits.Text = T("结论仅适用于当前探测目标。保存直连基线前请自行关闭代理。WebRTC/浏览器 DoH、断网保护、网络切换、长连接、局域网/.local、UDP 大包与 MTU 尚未覆盖。", "Conclusions apply only to the probed targets. Turn your proxy off before capturing a direct baseline. Browser WebRTC/DoH, kill switch, network changes, long-lived connections, LAN/.local, large UDP datagrams and MTU are not covered.");
        start.Click += async (_, _) => await Model.StartAsync();
        retry.Click += async (_, _) => await Model.StartAsync(true);
        cancel.Click += (_, _) => Model.Cancel();
        saveBaseline.Click += (_, _) => { try { Model.SaveBaseline(); } catch (Exception ex) { Model.Summary = ex.Message; } };
        loadBaseline.Click += async (_, _) => await LoadBaselineAsync();
        export.Click += async (_, _) => await ExportAsync();
        AttachedToVisualTree += async (_, _) => await Model.RefreshEnvironmentAsync();
    }
    private async Task LoadBaselineAsync()
    {
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider; if (provider == null) return;
            var files = await provider.OpenFilePickerAsync(new() { Title = ProxyCheckText.T("选择基线报告", "Choose baseline report"), AllowMultiple = false, FileTypeFilter = [new("JSON") { Patterns = ["*.json"] }] });
            if (files.Count == 0) return;
            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            if (stream.Length > 2 * 1024 * 1024) throw new IOException("Baseline exceeds limit");
            using var reader = new StreamReader(stream); Model.ImportBaseline(await reader.ReadToEndAsync());
        }
        catch (Exception ex) { Model.Summary = ex.Message; }
    }
    private async Task ExportAsync()
    {
        try
        {
            var json = Model.ExportReport();
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider; if (provider == null) return;
            using var file = await provider.SaveFilePickerAsync(new() { Title = ProxyCheckText.T("导出检查报告", "Export check report"), SuggestedFileName = $"proxy-check-{DateTime.Now:yyyyMMdd-HHmmss}.json", DefaultExtension = "json" });
            if (file == null) return;
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
            await using var writer = new StreamWriter(stream); await writer.WriteAsync(json);
        }
        catch (Exception ex) { Model.Summary = ex.Message; }
    }
}
