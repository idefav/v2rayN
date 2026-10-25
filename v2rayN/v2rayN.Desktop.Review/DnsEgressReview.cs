using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Common;
using ServiceLib.Helper;
using ServiceLib.Resx;
using ServiceLib.Enums;
using ServiceLib.Handler;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Views;
using ReactiveUI.Primitives.Extensions;
using ReactiveUI.Primitives;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task CheckDnsEgressAsync()
    {
        await _main.OpenSettingsAsync("DNS");
        var page = _main.GetLogicalDescendants().OfType<DNSSettingWindow>().Single();
        var model = page.ViewModel!;
        await model.Initialization;
        var tabs = page.GetLogicalDescendants().OfType<TabControl>().Single(t => t.Name == "dnsEgressTabs");
        Check(tabs.Items.Count == 3, "three DNS egress tabs");
        var original = EditorSnapshot.Capture(model);
        model.EgressError = "validation feedback only";
        Check(EditorSnapshot.Capture(model) == original, "validation feedback does not dirty the draft");
        model.EgressError = "";
        Check(!model.IsDefaultDnsEditable, "legacy mode disables default DNS egress");
        model.EgressModeIndex = (int)DnsEgressMode.FollowRouting;
        Check(model.IsDefaultDnsEditable, "follow routing enables default DNS egress");
        Check(EditorSnapshot.Capture(model) != original, "policy changes participate in unsaved draft detection");
        var node = model.DnsNodes.First(n => n.Id.Length > 0);
        model.SetNodeOverride(node.Id, "tcp://9.9.9.9");
        var edited = EditorSnapshot.Capture(model);
        model.SetNodeOverride(node.Id, null);
        Check(edited != EditorSnapshot.Capture(model), "override changes participate in unsaved draft detection");
        model.SBCustomDNSEnableCompatible = true;
        Check(!model.IsManagedEgressEditable, "custom sing-box DNS suspends managed egress only");
        model.SBCustomDNSEnableCompatible = false;
        var normalDns = model.NormalDNS2Compatible;
        var tunDns = model.TunDNS2Compatible;
        foreach (var invalid in new[] { "{\"servers\":null}", "{\"servers\":[null]}" })
        {
            model.NormalDNS2Compatible = invalid;
            await model.SaveCmd.Execute().ToTask();
            Check(model.EgressError.Length > 0 && AppManager.Instance.Config.SimpleDNSItem.EgressMode == DnsEgressMode.Legacy,
                "invalid normal DNS rejected without mutation or exception");
            model.NormalDNS2Compatible = normalDns;
            model.TunDNS2Compatible = invalid;
            await model.SaveCmd.Execute().ToTask();
            Check(model.EgressError.Length > 0 && AppManager.Instance.Config.SimpleDNSItem.EgressMode == DnsEgressMode.Legacy,
                "invalid TUN DNS rejected without mutation or exception");
            model.TunDNS2Compatible = tunDns;
        }
        model.DefaultDnsNodeId = "missing-node";
        await model.SaveCmd.Execute().ToTask();
        Check(model.EgressError.Length > 0 && AppManager.Instance.Config.SimpleDNSItem.EgressMode == DnsEgressMode.Legacy,
            "invalid default node rejected before persistence");
        model.DefaultDnsNodeId = "";
        model.EgressError = "";
        var remoteDns = model.RemoteDNS;
        var storedNode = await AppManager.Instance.GetProfileItem(AppManager.Instance.Config.IndexId);
        var validNode = JsonUtils.DeepCopy(storedNode!);
        validNode.Network = "";
        validNode.StreamSecurity = "";
        validNode.SetProtocolExtra(validNode.GetProtocolExtra() with
        {
            SshHostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            SshHostKeyAlgorithm = "ssh-ed25519", SshTrustedAddress = validNode.Address,
            SshTrustedPort = validNode.Port, SshUdpForwarding = false,
        });
        await SQLiteHelper.Instance.ReplaceAsync(validNode);
        var originalRouting = await ConfigHandler.GetDefaultRouting(AppManager.Instance.Config);
        var invalidRouting = JsonUtils.DeepCopy(originalRouting);
        invalidRouting.RuleSet = "[null]";
        await SQLiteHelper.Instance.ReplaceAsync(invalidRouting);
        try
        {
            await model.SaveCmd.Execute().ToTask();
            Check(model.EgressError.Contains("Invalid routing rules")
                && AppManager.Instance.Config.SimpleDNSItem.EgressMode == DnsEgressMode.Legacy,
                "malformed stored routing rejected before saving without an unhandled exception");
        }
        finally
        {
            await SQLiteHelper.Instance.ReplaceAsync(originalRouting);
            model.EgressError = "";
        }
        model.RemoteDNS = "udp://1.1.1.1";
        await model.SaveCmd.Execute().ToTask();
        Check(model.EgressError == SshStrings.DnsUnsupported && AppManager.Instance.Config.SimpleDNSItem.EgressMode == DnsEgressMode.Legacy,
            "unsupported SSH UDP DNS rejected before persistence");
        await SQLiteHelper.Instance.ReplaceAsync(storedNode!);
        model.RemoteDNS = remoteDns;
        model.EgressError = "";
        var liveDns = AppManager.Instance.Config.SimpleDNSItem;
        var oldRayDns = await AppManager.Instance.GetDNSItem(ECoreType.Xray);
        var oldSbDns = await AppManager.Instance.GetDNSItem(ECoreType.sing_box);
        var blockedTempPath = Utils.GetConfigPath(ServiceLib.Global.ConfigFileName) + "_temp";
        if (File.Exists(blockedTempPath) || Directory.Exists(blockedTempPath))
            throw new InvalidOperationException("Fixture requires an unused temporary config path.");
        Directory.CreateDirectory(blockedTempPath);
        try
        {
            model.EgressModeIndex = (int)DnsEgressMode.Legacy;
            model.BlockQuic = !liveDns.BlockQuic;
            model.RayCustomDNSEnableCompatible = !oldRayDns.Enabled;
            model.SBCustomDNSEnableCompatible = !oldSbDns.Enabled;
            await model.SaveCmd.Execute().ToTask();
            Check(ReferenceEquals(liveDns, AppManager.Instance.Config.SimpleDNSItem)
                && AppManager.Instance.Config.SimpleDNSItem.BlockQuic != model.BlockQuic,
                "failed file persistence leaves live DNS settings unchanged");
            Check(JsonUtils.Serialize(await AppManager.Instance.GetDNSItem(ECoreType.Xray)) == JsonUtils.Serialize(oldRayDns)
                && JsonUtils.Serialize(await AppManager.Instance.GetDNSItem(ECoreType.sing_box)) == JsonUtils.Serialize(oldSbDns),
                "failed file persistence rolls back both core DNS database records");
        }
        finally
        {
            Directory.Delete(blockedTempPath);
            model.BlockQuic = liveDns.BlockQuic;
            model.RayCustomDNSEnableCompatible = oldRayDns.Enabled;
            model.SBCustomDNSEnableCompatible = oldSbDns.Enabled;
            model.EgressError = "";
            model.EgressModeIndex = (int)DnsEgressMode.FollowRouting;
        }
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (var width in new[] { 1440, 1000 })
        {
            RequestedThemeVariant = theme; _main.Width = width;
            for (var index = 0; index < 3; index++)
            {
                tabs.SelectedIndex = index;
                await Task.Delay(100);
                var save = page.FindControl<Button>("btnSave")!;
                Check(save.IsEffectivelyVisible && save.Bounds.Width > 0, $"save visible {theme} {width} tab {index}");
                await CaptureAsync($"dns-{theme}-{width}-{index}", "DNS egress · isolated fixture", _main);
            }
        }
    }
}
