using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Extensions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models.Entities;
using ServiceLib.ViewModels;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task CheckWorkspaceInteractionParityAsync()
    {
        await CheckTablePreferenceLifetimeAsync();
        await CheckSavedHotkeyDraftAsync();
        await CheckDuplicateServerPickerAsync();
    }

    private async Task CheckTablePreferenceLifetimeAsync()
    {
        var config = AppManager.Instance.Config;
        var width = _main.Width;
        var available = _vm.ShowClashUI;
        var savedProfiles = JsonUtils.DeepCopy(config.UiItem.MainColumnItem);
        var savedConnections = JsonUtils.DeepCopy(config.ClashUIItem.ConnectionsColumnItem);
        try
        {
            _main.Width = 1440;
            await _main.OpenSettingsAsync("Servers");
            await Task.Delay(100);
            var profiles = _main.WorkspaceWindow.GetLogicalDescendants().OfType<ProfilesView>().Single();
            var table = profiles.FindControl<DataGrid>("lstProfiles")!;
            var column = table.Columns.Single(c => c.Tag?.ToString() == "Remarks");
            column.Width = new DataGridLength(347);
            column.DisplayIndex = 2;
            await Task.Delay(50);
            var expectedWidth = (int)column.ActualWidth;
            var expectedIndex = column.DisplayIndex;
            await _main.NavigateAsync("Home");
            Check(config.UiItem.MainColumnItem.Any(c => c.Name == "Remarks" && c.Width == expectedWidth && c.Index == expectedIndex),
                "server column width and order are retained when leaving management for home before exit");

            await _main.OpenSettingsAsync("Servers");
            column.Width = new DataGridLength(373);
            await Task.Delay(50);
            expectedWidth = (int)column.ActualWidth;
            _main.Width = 800;
            await Task.Delay(80);
            await _main.NavigateAsync("Home");
            Check(config.UiItem.MainColumnItem.Any(c => c.Name == "Remarks" && c.Width == expectedWidth && c.Index == expectedIndex),
                "compact server layout preserves the user's wide-table preferences on navigation");

            _main.Width = 1440;
            _vm.ShowClashUI = true;
            await _main.OpenSettingsAsync("Connections");
            await Task.Delay(80);
            var connections = _main.WorkspaceWindow.GetLogicalDescendants().OfType<ClashConnectionsView>().Single();
            var sessions = connections.FindControl<DataGrid>("lstConnections")!;
            var sessionColumn = sessions.Columns.First(c => c.Tag != null);
            sessionColumn.Width = new DataGridLength(319);
            await Task.Delay(50);
            var expectedSessionWidth = (int)sessionColumn.ActualWidth;
            await _main.OpenSettingsAsync("Help");
            Check(config.ClashUIItem.ConnectionsColumnItem.Any(c => c.Name == sessionColumn.Tag!.ToString() && c.Width == expectedSessionWidth),
                "connection table preferences survive leaving the page before application exit");
            var reopened = new ClashConnectionsView();
            Check(reopened.FindControl<DataGrid>("lstConnections")!.Columns.First(c => Equals(c.Tag, sessionColumn.Tag)).Width.Value == expectedSessionWidth,
                "a new connections view restores the saved width");
        }
        finally
        {
            await _main.NavigateAsync("Home");
            _main.Width = width;
            _vm.ShowClashUI = available;
            config.UiItem.MainColumnItem = savedProfiles;
            config.ClashUIItem.ConnectionsColumnItem = savedConnections;
        }
    }

    private async Task CheckSavedHotkeyDraftAsync()
    {
        var config = AppManager.Instance.Config;
        var original = config.GlobalHotkeys;
        try
        {
            // Only the platform-independent editor and disposable fixture file are used.
            // This does not register OS hotkeys on any platform.
            var editor = new GlobalHotkeySettingViewModel();
            var draft = editor.GetKeyEventItem(EGlobalHotkey.ShowForm);
            draft.KeyCode = 42;
            draft.Control = true;
            await editor.SaveCmd.Execute().ToTask();
            var saved = JsonUtils.Serialize(config.GlobalHotkeys);
            draft.KeyCode = 43;
            draft.Control = false;
            Check(JsonUtils.Serialize(config.GlobalHotkeys) == saved,
                "editing a hotkey again after Save does not mutate the saved configuration");
            var reopened = new GlobalHotkeySettingViewModel();
            Check(reopened.GetKeyEventItem(EGlobalHotkey.ShowForm).KeyCode == 42
                && reopened.GetKeyEventItem(EGlobalHotkey.ShowForm).Control,
                "discarding an edited hotkey draft restores the last saved shortcut");
            editor.ResetKeyEventItem();
            Check(JsonUtils.Serialize(config.GlobalHotkeys) == saved,
                "resetting hotkeys after Save remains a draft operation");
        }
        finally { config.GlobalHotkeys = original; await ConfigHandler.SaveConfig(config); }
    }

    private async Task CheckDuplicateServerPickerAsync()
    {
        var status = (StatusBarView)_main.FindControl<ContentControl>("contentStatusBarView")!.Content!;
        var config = AppManager.Instance.Config;
        var originalId = config.IndexId;
        var width = _main.Width;
        var height = _main.Height;
        var first = Profile(EConfigType.SSH);
        first.IndexId = "review-duplicate-first"; first.Remarks = "Same-name fixture";
        first.Address = "first.example.test"; first.Port = 22; first.Subid = "review-sub";
        var second = Profile(EConfigType.Trojan);
        second.IndexId = "review-duplicate-second"; second.Remarks = first.Remarks;
        second.Address = "2001:db8::2"; second.Port = 443; second.Subid = "review-backup";
        try
        {
            await SQLiteHelper.Instance.ReplaceAsync(first);
            await SQLiteHelper.Instance.ReplaceAsync(second);
            await _main.NavigateAsync("Home");
            status.CloseConnectionControls();
            status.FindControl<ComboBox>("pickerScope")!.SelectedIndex = 0;
            status.FindControl<Button>("btnConnectionControls")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForDrawerAsync();
            var choices = PickerList(status);
            await WaitForPickerAsync(choices, rows => rows.Length > 0, "duplicate-name fixture waits for picker load");
            await SelectPickerGroupAsync(status, "*");
            // Every open intentionally clears the old filter to reveal the
            // selected server. Enter this test's query after opening the drawer.
            status.FindControl<TextBox>("txtServerSearch")!.Text = first.Remarks;
            await WaitForPickerAsync(choices, rows => rows.Length == 2 && rows.All(row => row.Name == first.Remarks),
                "same-name search completes across groups");
            Check(choices.Items.OfType<ServerPickerRow>().Count(row => row.IsHeader) == 2 && PickerNodes(choices).Length == 2,
                "same-name servers in distinct groups retain two headings and two selectable rows");
            foreach (var (id, protocol, endpoint) in new[] { (first.IndexId, "SSH", "first.example.test:22"),
                         (second.IndexId, "Trojan", "[2001:db8::2]:443") })
            {
                var choice = await PickerButtonAsync(choices, id, reveal: true);
                var labels = choice.GetVisualDescendants().OfType<TextBlock>().Select(c => c.Text ?? "").ToArray();
                Check(labels.Contains(endpoint) && labels.Any(text => text.StartsWith(protocol + " · "))
                    && AutomationProperties.GetName(choice)?.Contains(endpoint) == true,
                    "same-name row separates protocol and endpoint, including IPv6: " + id);
            }
            var select = await PickerButtonAsync(choices, second.IndexId, reveal: true);
            await CaptureAsync("v3-interaction-picker", "Same-name servers show protocol, endpoint and group", _main);
            var position = select.TranslatePoint(default, _main)!.Value;
            Check(position.Y >= 68 && position.Y + select.Bounds.Height <= _main.ClientSize.Height + 1,
                "scrolling a virtualized same-name row brings the complete choice into view");
            status.CloseConnectionControls();
            await WaitForDrawerAsync();
            _main.Width = 800;
            _main.Height = 600;
            status.FindControl<Button>("btnConnectionControls")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForDrawerAsync();
            await WaitForPickerAsync(choices, rows => rows.Length > 0, "small picker finishes reloading");
            await SelectPickerGroupAsync(status, "*");
            status.FindControl<TextBox>("txtServerSearch")!.Text = first.Remarks;
            await WaitForPickerAsync(choices, rows => rows.Length == 2 && rows.All(row => row.Name == first.Remarks),
                "small picker applies the cross-group search");
            select = await PickerButtonAsync(choices, second.IndexId, reveal: true);
            await CaptureAsync("v3-interaction-picker-small", "Same-name selection at 800x600", _main);
            position = select.TranslatePoint(default, _main)!.Value;
            Check(position.Y >= 68 && position.Y + select.Bounds.Height <= _main.ClientSize.Height + 1,
                "the complete server choice remains visible at 800x600");
            string? requested = null;
            using var subscription = _vm.StatusBarViewModel.SetDefaultServerRequested.AsObservable().Subscribe(id => requested = id);
            select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(80);
            Check(requested == second.IndexId && !status.IsServerPickerOpen,
                "choosing a duplicate-name row selects its own profile and closes the picker");
        }
        finally
        {
            status.CloseConnectionControls();
            status.FindControl<TextBox>("txtServerSearch")!.Text = "";
            _main.Width = width;
            _main.Height = height;
            config.IndexId = originalId;
            await SQLiteHelper.Instance.DeleteAsync(first);
            await SQLiteHelper.Instance.DeleteAsync(second);
            await _vm.ProfilesViewModel.RefreshServers();
        }
    }
}
