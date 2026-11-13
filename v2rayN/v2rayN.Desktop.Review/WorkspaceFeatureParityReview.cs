using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using ServiceLib.Manager;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task CheckWorkspaceFeatureParityAsync()
    {
        await CheckWorkspaceInteractionParityAsync();
        var width = _main.Width;
        var height = _main.Height;
        var appearance = new ThemeSettingViewModel(editAsDraft: true);
        try
        {
            _main.Width = 800;
            _main.Height = 600;
            appearance.CurrentFontSize = 24;
            await _main.OpenSettingsAsync("Core");
            _main.SettingsWindow!.Width = 800; _main.SettingsWindow.Height = 600;
            var options = _main.WorkspaceWindow.GetLogicalDescendants().OfType<OptionSettingWindow>().Single();
            var details = _main.WorkspaceWindow.GetVisualDescendants().OfType<Expander>().Single(expander =>
                expander.Header?.ToString() == WorkspaceText.T("实际监听与检测状态", "Actual listeners and checks"));
            details.IsExpanded = true;
            await Task.Delay(150);
            _main.UpdateLayout();
            await CaptureAsync("v3-parity-expanded-listeners", "Expanded listener details at minimum window size", _main);
            var save = options.FindControl<Button>("btnSave")!;
            var savePosition = save.TranslatePoint(default, _main.SettingsWindow)!.Value;
            Check(options.Bounds.Height >= 150 && save.IsEffectivelyVisible && save.Bounds.Height > 0
                && savePosition.Y + save.Bounds.Height <= _main.SettingsWindow.ClientSize.Height + 1,
                $"expanding listener details retains settings and save at 800x600 (body={options.Bounds}, save={savePosition}/{save.Bounds})");
            foreach (var group in WorkspacePages.Available.Select(page => page.Group).Distinct())
            {
                var section = _main.WorkspaceWindow.GetVisualDescendants().OfType<Expander>().Single(item => item.Name == "group" + group);
                var title = section.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == WorkspacePages.GroupTitle(group)
                    && !text.GetVisualAncestors().OfType<Button>().Any(button => button.Name?.StartsWith("nav") == true));
                Check(title.TextLayout.WidthIncludingTrailingWhitespace <= title.Bounds.Width + 1
                    && title.TextLayout.Height <= title.Bounds.Height + 1,
                    $"settings group {group} renders its complete title at 800px and 24px font (text={title.TextLayout.WidthIncludingTrailingWhitespace}x{title.TextLayout.Height}, bounds={title.Bounds})");
            }
            details.IsExpanded = false;
        }
        finally { appearance.RevertPreview(); _main.Width = width; _main.Height = height; }

        // Validate the original command surface against visible V3 entry points.
        // No operational command (network, proxy, clipboard or OS launch) is executed.
        await _main.OpenSettingsAsync("Servers");
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _main.UpdateLayout(), Avalonia.Threading.DispatcherPriority.Background);
        var profiles = _main.WorkspaceWindow.GetLogicalDescendants().OfType<ProfilesView>().Single();
        var original = _main.FindControl<MenuItem>("menuServers")!.Items.OfType<MenuItem>().ToArray();
        var actions = new List<MenuItem>();
        foreach (var name in new[] { "btnImportServers", "btnAddServers" })
        {
            var button = profiles.FindControl<Button>(name)!;
            Check(button.IsEffectivelyVisible && button.Flyout is MenuFlyout, $"legacy server operations remain reachable through {name}");
            actions.AddRange(((MenuFlyout)button.Flyout!).Items.OfType<MenuItem>());
        }
        foreach (var source in original)
            Check(actions.Any(action => Equals(action.Header, source.Header) && action.IsVisible == source.IsVisible
                && source.Command != null && ReferenceEquals(action.Command, source.Command)),
                $"server command {source.Name} retains its visible V3 binding");
        static IEnumerable<MenuItem> MenuItems(ItemsControl menu)
        {
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                yield return item;
                foreach (var child in MenuItems(item)) yield return child;
            }
        }
        var contextActions = MenuItems(profiles.FindControl<DataGrid>("lstProfiles")!.ContextMenu!).ToArray();
        foreach (var name in new[]
        {
            "menuSetDefaultServer", "menuEditServer", "menuCopyServer", "menuRemoveServer", "menuRemoveDuplicateServer",
            "menuRemoveInvalidServerResult", "menuTcpingServer", "menuRealPingServer", "menuSpeedServer", "menuUdpTestServer",
            "menuSortServerResult", "menuMoveTop", "menuMoveUp", "menuMoveDown", "menuMoveBottom", "menuShareServer",
            "menuExport2ClientConfig", "menuExport2ClientConfigClipboard", "menuExport2ShareUrl", "menuExport2ShareUrlBase64",
            "menuExport2InnerUri", "menuGenGroupAllServer", "menuGenGroupRegionServer"
        })
            Check(contextActions.SingleOrDefault(action => action.Name == name) is { IsVisible: true, Command: not null },
                $"legacy server context operation {name} keeps its command");

        foreach (var (page, names) in new[]
        {
            ("Subscriptions", new[] { "btnUpdateAll", "btnUpdateOptions", "menuSubAdd", "txtSubscriptionFilter", "cmbSubscriptionKind" }),
            ("Routing", new[] { "menuRoutingAdvancedAdd2", "menuRoutingAdvancedImportRules2", "lstRoutings", "cmbdomainStrategy", "cmbdomainStrategy4Singbox" }),
            ("Logs", new[] { "cmbMsgFilter", "btnCopy", "btnClear", "togAutoRefresh", "togScrollToEnd" }),
            ("Updates", new[] { "togEnableUpdateViaProxy", "btnCheckOnly", "btnCheckUpdate", "btnSources", "lstCheckUpdates" }),
            ("Backup", new[] { "menuLocalBackup", "menuLocalRestore", "txtWebDavUrl", "menuWebDavCheck", "menuRemoteBackup", "menuRemoteRestore" })
        })
        {
            await _main.OpenSettingsAsync(page);
            await Task.Delay(50);
            foreach (var name in names)
                Check(_main.WorkspaceWindow.GetVisualDescendants().OfType<Control>().Any(control => control.Name == name && control.IsEffectivelyVisible),
                    $"legacy {page} operation {name} remains visible");
        }
        foreach (var (page, names) in new[]
        {
            ("Utilities", new[] { "menuOpenTheFileLocation", "menuClearServerStatistics", "menuRegionalPresetsDefault", "menuRegionalPresetsRussia", "menuRegionalPresetsIran" }),
            ("Help", new[] { "menuCheckUpdate", "menuPromotion", "menuClose" })
        })
        {
            await _main.OpenSettingsAsync(page);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _main.UpdateLayout(), Avalonia.Threading.DispatcherPriority.Background);
            foreach (var name in names)
            {
                var source = _main.FindControl<MenuItem>(name)!;
                Check(_main.WorkspaceWindow.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible
                    && Equals(button.Content, source.Header) && ReferenceEquals(button.Command, source.Command)),
                    $"legacy tool {name} has a visible V3 entry");
            }
        }

        foreach (var (query, page) in new[]
        {
            ("font", "Appearance"), ("clipboard", "Servers"), ("port", "Core"), ("bypass", "SystemProxy"),
            ("MTU", "Tun"), ("route", "Routing"), ("resolver", "DNS"), ("session", "Connections"),
            ("UDP", "ProxyCheck"), ("error", "Logs"), ("startup", "Application"), ("download", "Updates"),
            ("mirror", "Sources"), ("restore", "Backup"), ("statistics", "Utilities"), ("version", "Help"),
            ("protocol", "Engines"), ("JSON", "Templates"), ("字体", "Appearance"), ("端口", "Core"),
            ("二维码", "Servers"), ("FONT size", "Appearance"), ("DNS resolver", "DNS")
        })
            Check(WorkspacePages.Search(query).Any(entry => entry.Key == page), $"settings keyword '{query}' locates {page} in either UI language");

        var apiAvailable = _vm.ShowClashUI;
        try
        {
            _vm.ShowClashUI = false;
            await _main.OpenSettingsAsync("Connections");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _main.UpdateLayout(), Avalonia.Threading.DispatcherPriority.Background);
            var connections = _main.WorkspaceWindow.GetLogicalDescendants().OfType<ClashConnectionsView>().Single();
            var closeAll = connections.FindControl<Button>("btnConnectionCloseAll")!;
            Check(!closeAll.IsEffectivelyEnabled, "unavailable connections API cannot close sessions through a stale action");
            _vm.ShowClashUI = true;
            await Task.Delay(50);
            Check(closeAll.IsEffectivelyEnabled && _main.WorkspaceWindow.GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.Text == WorkspacePages.Available.Single(page => page.Key == "Connections").Description),
                "connections API becoming available restores actions and description without navigation");
            _vm.ShowClashUI = false;
            await Task.Delay(50);
            Check(!closeAll.IsEffectivelyEnabled, "switching away from a supported API disables connection operations in place");
        }
        finally { _vm.ShowClashUI = apiAvailable; }

        await _main.NavigateAsync("Home");
        Check(!await CoreManager.Instance.IsCoreRunningAsync(), "feature parity review does not start any core");
    }
}
