using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using ServiceLib.Enums;
using ServiceLib.Helper;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using ServiceLib.ViewModels;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task ReviewRoutePickerAsync()
    {
        foreach (var sub in new[] { "route-a", "route-b" })
        {
            await SQLiteHelper.Instance.ReplaceAsync(new SubItem { Id = sub, Remarks = sub });
            foreach (var suffix in new[] { "one", "two" })
                await SQLiteHelper.Instance.ReplaceAsync(new ProfileItem { IndexId = sub + suffix, Remarks = sub + suffix,
                    ConfigType = EConfigType.SOCKS, Address = "127.0.0.1", Port = 12345, Subid = sub });
        }
        var rule = new RulesItem { Id = "route-picker", Port = "443", OutboundTag = "proxy" };
        var editor = new RoutingRuleDetailsViewModel(rule, ECoreType.Xray);
        var editorWindow = new RoutingRuleDetailsWindow { ViewModel = editor };
        editorWindow.Show(_main);
        var open = editor.SelectProfileCmd.Execute().ToTask();
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        await WaitForUiAsync(() => desktop.Windows.OfType<ProfilesSelectWindow>().Any(), "open the rule picker");
        var window = desktop.Windows.OfType<ProfilesSelectWindow>().Single();
        var model = window.ViewModel!;
        await model.Initialization;
        await WaitForUiAsync(() => window.FindControl<StackPanel>("routeControls")!.IsEffectivelyVisible, "route picker activated");
        var grid = window.FindControl<DataGrid>("lstProfiles")!;
        async Task SelectSub(string id)
        {
            var tabs = window.FindControl<ListBox>("lstGroup")!;
            await WaitForUiAsync(() => tabs.Items.OfType<SubItem>().Any(s => s.Id == id), "subscription tabs bound: " + id);
            tabs.SelectedItem = model.SubItems.Single(s => s.Id == id);
            await WaitForUiAsync(() => model.ProfileItems.Count == 2 && model.ProfileItems.All(p => p.Subid == id),
                $"subscription filter {id}, selected {model.SelectedSub?.Id}, view {((SubItem?)tabs.SelectedItem)?.Id}, rows {model.ProfileItems.Count}, {model.RouteStatus}");
            await Task.Delay(60);
        }
        async Task<CheckBox> Box(string id)
        {
            CheckBox? box = null;
            await WaitForUiAsync(() => (box = grid.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c =>
                c.IsEffectivelyVisible && c.DataContext is ProfileItemModel row && row.IndexId == id)) != null, "realized checkbox " + id);
            return box!;
        }
        void Click(Window owner, CheckBox box)
        {
            if (Program.Headless)
            {
                owner.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                var point = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), owner)!.Value;
                owner.MouseDown(point, MouseButton.Left);
                owner.MouseUp(point, MouseButton.Left);
            }
            else typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(box, null);
        }

        await SelectSub("route-a");
        Click(window, await Box("route-aone"));
        Click(window, await Box("route-atwo"));
        Check(model.RouteSelection().ProfileIds!.Count == 2, "Clicking a second checkbox preserves both rows");
        Click(window, await Box("route-atwo"));
        Check(model.RouteSelection().ProfileIds!.SequenceEqual(["route-aone"]), "Unchecking a row removes only that ID");
        await SelectSub("route-b");
        model.ServerFilter = "two";
        await model.RefreshServers();
        await Task.Delay(60);
        Click(window, window.FindControl<CheckBox>("routeAll")!);
        model.SortServer("Remarks");
        Check(model.RouteSelection().ProfileIds!.SequenceEqual(["route-aone", "route-btwo"]), "Select all affects filtered rows and preserves the other group");
        await model.SaveCmd.Execute().ToTask();
        await open;
        await editor.SaveCmd.Execute().ToTask();
        Check(rule.OutboundTag == "block" && rule.OutboundTarget?.ProfileIds?.Count == 2, "Editor saves IDs with compatibility block");
        editorWindow.Close();
        editor = new RoutingRuleDetailsViewModel(rule, ECoreType.Xray);
        editorWindow = new RoutingRuleDetailsWindow { ViewModel = editor };
        editorWindow.Show(_main);
        async Task Reopen()
        {
            open = editor.SelectProfileCmd.Execute().ToTask();
            await WaitForUiAsync(() => desktop.Windows.OfType<ProfilesSelectWindow>().Any(), "reopen through the editor");
            window = desktop.Windows.OfType<ProfilesSelectWindow>().Single();
            model = window.ViewModel!;
            await model.Initialization;
            grid = window.FindControl<DataGrid>("lstProfiles")!;
            await WaitForUiAsync(() => window.FindControl<StackPanel>("routeControls")!.IsEffectivelyVisible, "route picker activated");
        }
        await Reopen();
        Check(model.ProfileItems.Count(r => r.RouteChoice!.Selected) == 2, "Both selected machines are checked on reopening");
        window.FindControl<ComboBox>("routeMode")!.SelectedIndex = 1;
        Check(!model.CanOk(), "All subscriptions cannot be saved as a dynamic target");
        await SelectSub("route-b");
        Check(model.RouteSelection().SubscriptionId == "route-b" && rule.OutboundTarget!.ProfileIds!.Count == 2,
            "Follow mode is a separate draft until confirmed");
        await model.SaveCmd.Execute().ToTask();
        await open;
        await editor.SaveCmd.Execute().ToTask();
        Check(rule.OutboundTarget?.SubscriptionId == "route-b" && rule.OutboundTarget.ProfileIds == null, "Saving follows one subscription");
        await Reopen();
        Check(model.FollowSubscription && model.SelectedSub.Id == "route-b", "Reopening restores follow mode and the subscribed group");
        window.FindControl<ComboBox>("routeMode")!.SelectedIndex = 0;
        Click(window, await Box("route-bone"));
        window.Close(false);
        await open;
        await editor.SaveCmd.Execute().ToTask();
        Check(rule.OutboundTarget?.SubscriptionId == "route-b", "Cancelling keeps the original subscription");
        editorWindow.Close();

        var single = new ProfilesSelectViewModel();
        await single.Initialization;
        single.SelectedSub = single.SubItems.Single(s => s.Id == "route-a");
        await single.RefreshServers();
        var legacy = new ProfilesSelectWindow { ViewModel = single };
        legacy.Show(_main);
        await Task.Delay(250);
        var legacyGrid = legacy.FindControl<DataGrid>("lstProfiles")!;
        foreach (var id in new[] { "route-atwo", "route-aone" })
        {
            var check = legacyGrid.GetVisualDescendants().OfType<CheckBox>().First(c =>
                c.IsEffectivelyVisible && c.DataContext is ProfileItemModel row && row.IndexId == id);
            Click(legacy, check);
            Check(single.SelectedProfile?.IndexId == id, "Single-select click uses the selected item: " + id);
        }
        Check(!legacyGrid.GetVisualDescendants().OfType<CheckBox>().Any(c => c.IsEffectivelyVisible && c.FindAncestorOfType<DataGridColumnHeader>() != null),
            "Single-select header has no select-all checkbox");
        legacy.Close();
    }
}
