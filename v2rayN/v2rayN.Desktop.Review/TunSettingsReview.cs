using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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
            Mtu = 9000, IcmpRouting = "default", EnableLegacyProtect = true
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
        Check(!page.GetLogicalDescendants().OfType<Button>().Any(b => b.Name == "tunNav_linux"),
            "unsupported Linux settings are hidden by default on this Mac review");
        await CaptureAsync("tun-basic", "Native TUN settings · isolated fixtures", _main);

        page.SelectGroup("dns");
        var dns = page.GetLogicalDescendants().OfType<ComboBox>()
            .Single(c => AutomationProperties.GetName(c) == model["dns_mode"].Definition.Title);
        dns.SelectedItem = model["dns_mode"].Choices.Single(c => c.Value == "native");
        Check(model["dns_mode"].Value == "native" && model.IsDirty && model.CanSave, "native dropdown edits a tracked TUN draft");
        Check(config.TunModeItem.Singbox == null, "editing does not mutate live configuration");
        page.ShowHelpForReview("dns_mode");
        await CaptureAsync("tun-dns", "DNS handling and contextual help", _main);
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
        Check(page.GetLogicalDescendants().OfType<Expander>().Count() == 1, "offline help is searchable by parameter name");
        page.GetLogicalDescendants().OfType<Expander>().Single().IsExpanded = true;
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
