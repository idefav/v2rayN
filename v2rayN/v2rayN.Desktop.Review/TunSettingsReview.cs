using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Services;
using ServiceLib.ViewModels;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task ReviewTunSettingsAsync()
    {
        var config = AppManager.Instance.Config;
        config.TunModeItem = new()
        {
            EnableTun = false, AutoRoute = true, StrictRoute = true, Stack = "gvisor",
            Mtu = 9000, IcmpRouting = Global.TunIcmpRoutingPolicies.First(), EnableLegacyProtect = true
        };
        await _main.OpenSettingsAsync("Core");
        var options = _main.GetLogicalDescendants().OfType<OptionSettingWindow>().Single().ViewModel!;
        Check(!options.ManagesTunSettings, "Avalonia options do not overwrite the independent TUN draft");
        await _main.OpenSettingsAsync("Tun");
        var page = _main.GetLogicalDescendants().OfType<TunSettingsWindow>().Single();
        await page.ViewModel!.Initialization;
        // Production saving checks the future TUN graph even while inactive.
        // The review uses an explicit fixture model and never probes the synthetic SSH node.
        // Platform cases are explicit so Linux CI exercises the same Mac baseline,
        // then the Linux fixture below, without changing the runner's network.
        var model = new TunSettingsViewModel(config, TunPlatform.MacOS);
        page.ViewModel = model;
        page.DataContext = model;
        Check(!model.IsDirty && !model.CanSave, "opening TUN settings preserves the configuration");
        Check(page.GetLogicalDescendants().OfType<Button>().Count(b => b.Classes.Contains("tunNav")) == 6
            && !page.GetLogicalDescendants().OfType<Control>().Any(b => b.Name == "tunField_auto_redirect"),
            "six design navigation entries remain discoverable while Linux fields are hidden on Mac");
        await CaptureAsync("tun-basic", "Native TUN settings · isolated fixtures", _main);
        Check(!model.IsDirty, "first layout does not turn automatic addresses into modified settings");
        Check(!page.FindControl<Button>("resetGroup")!.IsEnabled, "reset is disabled when the current group already uses defaults");
        var navButtons = page.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("tunNav")).ToArray();
        Check(navButtons.All(b => b.Bounds.Height >= 44), "design navigation retains 44 px hit targets");
        Check(navButtons.Where(b => !b.Classes.Contains("isCurrent")).All(b => b.BorderBrush is Avalonia.Media.ISolidColorBrush brush && brush.Color.A == 0),
            "unselected navigation is flat rather than outlined button cards");
        var rail = page.FindControl<Border>("navigationRail")!;
        if (page.Bounds.Width > 760)
            Check(Math.Abs(rail.Bounds.Width - (page.Bounds.Width > 1100 ? 168 : 152)) < 1,
                "navigation width follows the original 168/152 px design columns");

        page.SelectGroup("dns");
        var dns = page.GetLogicalDescendants().OfType<ComboBox>()
            .Single(c => c.Name == "tunChoice_dns_mode");
        dns.SelectedItem = model["dns_mode"].Choices.Single(c => c.Value == "native");
        Check(model["dns_mode"].Value == "native" && model.IsDirty && model.CanSave, "native dropdown edits a tracked TUN draft");
        Check(config.TunModeItem.Singbox == null, "editing does not mutate live configuration");
        page.FindControl<Button>("discardChanges")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(60);
        var confirmation = ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Windows.OfType<MessageBoxDialog>().Single();
        confirmation.FindControl<Button>("btnNo")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(60);
        Check(model.IsDirty && model["dns_mode"].Value == "native", "cancelling the discard confirmation retains the complete draft");
        page.ShowHelpForReview("dns_mode");
        await CaptureAsync("tun-dns", "DNS handling and contextual help", _main);
        Check(page.GetLogicalDescendants().OfType<ComboBox>().Any(c => c.Name == "tunDnsAddressMode")
            && !page.GetLogicalDescendants().OfType<TextBox>().Any(t => t.PlaceholderText == "搜索预设或已知项"),
            "DNS entry uses the design's automatic/custom selector rather than a generic list editor");
        Check(Math.Abs(dns.Bounds.Height - 44) < 1, "DNS dropdown matches the 44 px design control height");
        Check(page.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "tunDnsModeExplanation").Text!.Contains(TunSettingsText.Chinese ? "仅发往虚拟入口" : "Only requests"),
            "DNS mode explanation follows the selected mode without rebuilding the form");
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)page.Bounds.Width, (int)page.Bounds.Height), new Vector(96, 96)))
        {
            bitmap.Render(page);
            using var stream = File.Create(Path.Combine(Program.Output, "tun-dns-content.png"));
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        }
        File.WriteAllText(Path.Combine(Program.Output, "tun-layout.json"), JsonSerializer.Serialize(new
        {
            page = new { width = page.Bounds.Width, height = page.Bounds.Height },
            navigation = new { width = rail.Bounds.Width, buttonWidths = navButtons.Select(b => b.Bounds.Width), buttonHeights = navButtons.Select(b => b.Bounds.Height) },
            editor = new { width = page.FindControl<ScrollViewer>("editorScroll")!.Bounds.Width },
            help = new { visible = page.FindControl<Border>("helpDock")!.IsVisible, width = page.FindControl<Border>("helpDock")!.Bounds.Width },
            dnsControl = new { width = dns.Bounds.Width, height = dns.Bounds.Height },
            runtimeColumns = page.FindControl<Grid>("runtimeGrid")!.Children.Select(c => new { name = c.Name, column = Grid.GetColumn(c), x = c.Bounds.X, width = c.Bounds.Width }),
            scrollParts = page.FindControl<ScrollViewer>("editorScroll")!.GetVisualDescendants().OfType<Control>().Where(c => c is Avalonia.Controls.Primitives.ScrollBar || c.GetType().Name.Contains("RepeatButton"))
                .Select(c => new { type = c.GetType().Name, name = c.Name, visible = c.IsVisible, width = c.Bounds.Width, height = c.Bounds.Height }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        page.FindControl<Button>("closeHelp")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(await model.SaveAsync(), "saving an inactive TUN configuration succeeds without starting a core");
        Check(!config.TunModeItem.EnableTun && !model.IsDirty, "saving never enables TUN and clears the saved draft");
        await CaptureAsync("tun-saved", "Saved while TUN remains off", _main);

        page.SelectGroup("udp");
        model["udp_timeout"].Value = "10m";
        model["udp_nat_max"].Value = "8192";
        Check(model.Status.Length == 0 && model.IsDirty, "a new edit replaces the previous save status");
        await CaptureAsync("tun-udp", "UDP dropdowns and presets", _main);
        model["udp_timeout"].Value = "-5m";
        Check(!model.CanSave && model["udp_timeout"].Error.Length > 0, "invalid custom duration blocks saving");
        await CaptureAsync("tun-invalid", "Field validation with retained draft", _main);
        model.Discard();

        page.SelectGroup("routes");
        model.SetRuleSets([new() { Tag = "office-net", Type = "remote", Format = "source", Url = "https://example.com/office.json" }]);
        model["route_address_set"].Value = "office-net";
        Check(model.IsDirty && model.DraftSnapshot.Contains("office-net"), "rule-set source and selection are included in draft tracking");
        await CaptureAsync("tun-routes", "Capture lists and rule-set editor", _main);
        model.Discard();

        page.SelectGroup("help");
        page.GetLogicalDescendants().OfType<TextBox>().Single(t => t.Name == "tunHelpSearch").Text = "udp_nat_max";
        await System.Threading.Tasks.Task.Delay(60);
        var helpResult = page.GetLogicalDescendants().OfType<Button>().Single(b => b.Classes.Contains("tunHelpResult"));
        Check(helpResult.IsVisible, "offline help is searchable by parameter name");
        helpResult.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await CaptureAsync("tun-help-search", "Searchable offline usage guide", _main);

        var linux = new TunSettingsViewModel(JsonUtils.DeepCopy(config)!, TunPlatform.Linux);
        page.ViewModel = linux;
        page.DataContext = linux;
        page.SelectGroup("linux");
        linux["auto_redirect"].Value = "true";
        linux["include_interface"].Value = "eth0";
        linux["include_uid_range"].Value = "1000:1999";
        Check(linux["include_mac_address"].Enabled, "Linux dependent controls become available");
        await CaptureAsync("tun-linux", "Linux configuration · simulated platform, no OS mutations", _main);
        linux["auto_route"].Value = "false";
        Check(linux["auto_redirect"].Error.Length > 0 && !linux.CanSave, "Linux dependency conflict is visible and prevents apply");
        await CaptureAsync("tun-linux-dependency", "Linux auto-route dependency", _main);

        page.ViewModel = model;
        page.DataContext = model;
        model.IsTemplateOwned = true;
        page.SelectGroup("dns");
        Check(!model.CanSave && model.Fields.All(f => !f.Enabled), "full TUN templates own configuration and disable managed controls");
        await CaptureAsync("tun-template", "Template ownership is explicit", _main);
        model.IsTemplateOwned = false;
        page.SelectGroup("dns");
        Check(page.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0)
            .All(c => c.Bounds.Width <= page.Bounds.Width + 1), "dropdowns fit within the actual TUN viewport");
        foreach (var name in new[] { "saveChanges", "discardChanges", "resetGroup" })
        {
            var button = page.FindControl<Button>(name)!;
            var point = button.TranslatePoint(default, _main);
            Check(point != null && point.Value.X >= -1 && point.Value.X + button.Bounds.Width <= _main.ClientSize.Width + 1,
                "footer action remains visible: " + name);
        }
        Check(!config.TunModeItem.EnableTun, "UI review never enables real TUN");
    }
}
