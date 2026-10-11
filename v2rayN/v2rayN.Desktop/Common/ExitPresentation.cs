using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ServiceLib.Services;
using System.Globalization;

namespace v2rayN.Desktop.Common;

public static class ExitPresentation
{
    private static readonly Dictionary<string, Bitmap> Flags = new();
    public static string State(ExitInfoItem? item) => item?.State switch
    {
        "queued" => WorkspaceText.T("等待检测", "Queued"),
        "checking" => WorkspaceText.T("检测中", "Checking"),
        "failed" => WorkspaceText.T("检测失败", "Check failed"),
        "cancelled" => WorkspaceText.T("已取消或超时", "Cancelled or timed out"),
        "unsupported" => WorkspaceText.T("不支持独立检测", "Independent check unavailable"),
        "success" => WorkspaceText.T("检测成功", "Checked"),
        _ => WorkspaceText.T("未检测", "Not checked")
    };
    public static string Location(ExitInfoItem? item)
    {
        if (item?.Ip == null) return State(item);
        var country = item.Country;
        if (item.CountryCode is { Length: 2 } code)
            try { country = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? new RegionInfo(code).DisplayName : new RegionInfo(code).EnglishName; } catch (ArgumentException) { }
        if (CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            country = item.CountryCode switch { "US" => "美国", "JP" => "日本", "SG" => "新加坡", "HK" => "香港", "TW" => "台湾", "CN" => "中国", "DE" => "德国", "GB" => "英国", "FR" => "法国", "CA" => "加拿大", "AU" => "澳大利亚", "KR" => "韩国", "NL" => "荷兰", "IN" => "印度", "RU" => "俄罗斯", _ => country };
        return string.Join(" · ", new[] { country ?? item.CountryCode ?? "—", item.City ?? item.Region }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
    }
    public static string Details(ExitInfoItem? item)
    {
        if (item == null) return State(null);
        var family = System.Net.IPAddress.TryParse(item.Ip, out var ip) ? ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "IPv4" : "IPv6" : "—";
        return $"{WorkspaceText.T("最近一次检测到的出口", "Last observed exit")}\nIP: {item.Ip ?? "—"} · {family}\n{WorkspaceText.T("地区：", "Location: ")}{Location(item)}\n{WorkspaceText.T("省/地区：", "Region: ")}{item.Region ?? "—"}\n{WorkspaceText.T("检测时间：", "Checked: ")}{(item.CheckedAt == default ? "—" : item.CheckedAt.ToLocalTime().ToString("g"))}\n{State(item)}{(item.CheckedAt != default && (item.State != "success" || DateTime.UtcNow - item.CheckedAt > TimeSpan.FromHours(24)) ? WorkspaceText.T(" · 旧结果", " · previous result") : "")}\n{item.Error}\n{WorkspaceText.T("来源：", "Source: ")}{item.Source ?? "—"}\n{WorkspaceText.T("地区由查询服务提供；多出口节点可能变化。", "Location is supplied by the lookup service; multi-exit servers may vary.")}";
    }
    public static Control Cell(ExitInfoItem? item)
    {
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("24,8,*"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        if (item?.CountryCode is { Length: 2 } code && code.All(char.IsAsciiLetter))
        {
            var uri = new Uri($"avares://v2rayN/Assets/Flags/{code.ToLowerInvariant()}.png");
            if (AssetLoader.Exists(uri))
            {
                if (!Flags.TryGetValue(code, out var flag)) { using var stream = AssetLoader.Open(uri); Flags[code] = flag = new Bitmap(stream); }
                panel.Children.Add(new Image { Source = flag, Width = 22, Height = 16, Stretch = Stretch.Uniform });
            }
        }
        var label = new TextBlock { Text = Location(item), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(label);
        Grid.SetColumn(text, 2); panel.Children.Add(text);
        if (item?.Ip != null && item.State != "success") text.Children.Add(new TextBlock { Text = State(item) + WorkspaceText.T(" · 旧结果", " · previous"), Classes = { "muted" }, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTip.SetTip(panel, Details(item));
        return panel;
    }
    public static DataGridTemplateColumn RegionColumn() => new()
    {
        Header = WorkspaceText.T("出口地区", "Exit location"), Tag = "ExitLocation", Width = new DataGridLength(200), MinWidth = 140,
        CanUserSort = false,
        CellTemplate = new FuncDataTemplate<ProfileItemModel>((_, _) =>
        {
            var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            host.Bind(ContentControl.ContentProperty, new Binding(nameof(ProfileItemModel.ExitInfo)) { Converter = new FuncValueConverter<ExitInfoItem?, Control>(Cell) });
            return host;
        })
    };
    public static async Task CheckAsync(IEnumerable<string> ids)
    {
        foreach (var node in await AppManager.Instance.GetProfileItemsByIndexIds(ids)) ExitInfoService.Instance.Queue(node);
    }
}
