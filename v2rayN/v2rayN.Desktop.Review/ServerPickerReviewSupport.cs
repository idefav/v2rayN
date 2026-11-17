using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ServiceLib.ViewModels;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private static ServerPickerViewModel PickerViewModel(StatusBarView bar) =>
        (ServerPickerViewModel)(typeof(StatusBarView).GetField("_picker", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(bar)
            ?? throw new InvalidOperationException("The server picker view model is missing."));

    private static ListBox PickerList(StatusBarView bar) => bar.FindControl<ListBox>("serverRows")
        ?? throw new InvalidOperationException("The server picker must expose its virtualized serverRows ListBox.");

    // Group headers are items too; realized controls only represent the viewport.
    private static ServerPickerRow[] PickerNodes(ListBox list) => list.Items.OfType<ServerPickerRow>()
        .Where(row => !row.IsHeader).ToArray();

    private static Task WaitForPickerAsync(ListBox list, Func<ServerPickerRow[], bool> ready, string message) =>
        WaitForUiAsync(() => list.ItemsSource != null && ready(PickerNodes(list)), message);

    private static async Task SelectPickerGroupAsync(StatusBarView bar, string id)
    {
        var groups = bar.FindControl<ComboBox>("pickerGroup")
            ?? throw new InvalidOperationException("The server picker group filter is missing.");
        var picker = PickerViewModel(bar);
        // RowsChanged fires before OpenAsync finishes binding the new group list.
        // On reopening, the ComboBox may still contain the previous load's groups.
        await WaitForUiAsync(() => ReferenceEquals(groups.ItemsSource, picker.Groups)
            && groups.Items.OfType<ServerPickerGroup>().Any(group => group.Id == id),
            "Picker group choices finish binding before selection: " + id);
        groups.SelectedItem = groups.Items.OfType<ServerPickerGroup>().Single(group => group.Id == id);
    }

    private static ScrollViewer PickerScroll(ListBox list) => list.GetVisualDescendants().OfType<ScrollViewer>().Single();

    private static async Task<Button> PickerButtonAsync(ListBox list, string id, bool favorite = false, bool reveal = false)
    {
        var row = PickerNodes(list).Single(node => node.Node.IndexId == id);
        // Only explicit scroll tests reveal rows here. Opening-position tests
        // must observe the production scroll behavior without helping it along.
        if (reveal) list.ScrollIntoView(row);
        Button? button = null;
        await WaitForUiAsync(() =>
        {
            button = list.ContainerFromItem(row)?.GetVisualDescendants().OfType<Button>().SingleOrDefault(candidate =>
                favorite ? AutomationProperties.GetName(candidate) == WorkspaceText.T("常用", "Favorite") + ": " + row.Name
                    : candidate.Name == "select-" + id);
            return button is { IsEffectivelyVisible: true, Bounds.Height: > 0 };
        }, $"Picker {(favorite ? "favorite" : "selection")} control is realized for node {id}.");
        return button!;
    }
}
