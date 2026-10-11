using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace v2rayN.Desktop.Common;

public static class TablePresentation
{
    public static void FitHeaders(DataGrid table)
    {
        if (table.Classes.Contains("measuredHeaders")) return;
        table.Classes.Add("measuredHeaders");
        var minimums = new Dictionary<DataGridColumn, double>();
        void Measure()
        {
            var size = Math.Max(table.FontSize, AppManager.Instance.Config.UiItem.CurrentFontSize);
            foreach (var column in table.Columns)
            {
                if (column.Header is CheckBox || column.Header == null) continue;
                var label = column.Header is TextBlock text ? text.Text : column.Header.ToString();
                var header = new TextBlock { Text = label, FontSize = size, FontFamily = table.FontFamily, FontWeight = FontWeight.SemiBold };
                header.Measure(Size.Infinity);
                if (!minimums.TryGetValue(column, out var minimum)) minimums[column] = minimum = column.MinWidth;
                if (column.Tag?.ToString() == "Actions") minimum = Math.Max(minimum,
                    RowActionWidth(table, table.Name == "lstSubscription" ? [WorkspaceText.Edit, WorkspaceText.T("更新", "Update"), "⋯"] : [WorkspaceText.T("详情", "Details"), WorkspaceText.Edit]));
                column.MinWidth = Math.Max(minimum, header.DesiredSize.Width + 40);
            }
        }
        table.Loaded += (_, _) => Measure();
        table.SizeChanged += (_, _) => Measure();
        table.PropertyChanged += (_, change) => { if (change.Property.Name == "FontSize") Measure(); };
    }

    public static double RowActionWidth(Control owner, params string[] labels)
    {
        var size = Math.Max((owner as TemplatedControl)?.FontSize ?? 14, AppManager.Instance.Config.UiItem.CurrentFontSize);
        return labels.Sum(label =>
        {
            var text = new TextBlock { Text = label, FontSize = size, FontWeight = FontWeight.SemiBold };
            text.Measure(Size.Infinity);
            return text.DesiredSize.Width + 16;
        }) + 16;
    }

    public static double TextWidth(Control owner, params string[] labels)
    {
        var size = Math.Max((owner as TemplatedControl)?.FontSize ?? 14, AppManager.Instance.Config.UiItem.CurrentFontSize);
        return labels.Max(label =>
        {
            var text = new TextBlock { Text = label, FontSize = size };
            text.Measure(Size.Infinity);
            return text.DesiredSize.Width;
        });
    }

    public static void DisplaySelection(ComboBox combo, string empty, Func<object?, string?>? display = null)
    {
        combo.SelectionBoxItemTemplate = new FuncDataTemplate<object>((value, _) => new TextBlock
        {
            Text = (display?.Invoke(value) ?? value?.ToString()) is { Length: > 0 } text ? text : empty,
            VerticalAlignment = VerticalAlignment.Center
        });
    }
    private static readonly FuncValueConverter<object?, string> DetailValue = new(value => value switch
    {
        true => WorkspaceText.T("是", "Yes"), false => WorkspaceText.T("否", "No"),
        null => "—", string text when string.IsNullOrWhiteSpace(text) => "—", _ => value.ToString() ?? "—"
    });
    public static void TextIconButton(Button button, string label, string geometry, bool compact = false)
    {
        var panel = button.Content as StackPanel;
        if (panel == null)
        {
            var path = new Avalonia.Controls.Shapes.Path { Data = StreamGeometry.Parse(geometry), Fill = null,
                Width = 20, Height = 20, StrokeThickness = 1.6, Stretch = Stretch.Uniform };
            path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Binding(nameof(Button.Foreground)) { Source = button });
            panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            { new Viewbox { Width = 18, Height = 18, Child = path }, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } };
            button.Content = panel;
        }
        ((TextBlock)panel.Children[1]).IsVisible = !compact;
        ToolTip.SetTip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
    }

    public static void AddSelectionColumn(DataGrid table)
    {
        var selectAll = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(selectAll, ResUI.menuSelectAll);
        selectAll.Click += (_, _) =>
        {
            if (selectAll.IsChecked != true) table.SelectedItems.Clear();
            else if (table.SelectionMode == DataGridSelectionMode.Extended) table.SelectAll();
            else table.SelectedItem = table.ItemsSource?.Cast<object>().FirstOrDefault();
        };
        table.Columns.Insert(0, new DataGridTemplateColumn
        {
            Header = selectAll, Width = new DataGridLength(52), CanUserResize = false, CanUserSort = false,
            CellTemplate = new FuncDataTemplate<object>((item, _) =>
            {
                var check = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Avalonia.Automation.AutomationProperties.SetName(check, WorkspaceText.T("选择节点", "Select server"));
                check.Bind(CheckBox.IsCheckedProperty, new Binding("IsSelected")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(DataGridRow) },
                    Mode = BindingMode.OneWay
                });
                // React only to a user activation. Recycling, detaching and binding a row
                // can change IsChecked without the user changing table selection.
                check.Click += (_, _) =>
                {
                    var current = check.DataContext ?? item;
                    if (current == null) return;
                    var selected = table.SelectedItems.Contains(current);
                    if (check.IsChecked == true && !selected)
                    {
                        if (table.SelectionMode != DataGridSelectionMode.Extended) table.SelectedItems.Clear();
                        table.SelectedItems.Add(current);
                    }
                    else if (check.IsChecked != true && selected) table.SelectedItems.Remove(current);
                };
                return check;
            })
        });
        table.SelectionChanged += (_, _) => selectAll.IsChecked = table.SelectedItems.Count == table.ItemsSource?.Cast<object>().Count() && table.SelectedItems.Count > 0;
    }

    public static Border AddDetails(Control root, DataGrid table, string title, (string Label, string Property)[] fields, params Button[] actions)
    {
        var parent = table.Parent;
        var host = new Grid();
        Grid.SetRow(host, Grid.GetRow(table));
        Grid.SetColumn(host, Grid.GetColumn(table));
        DockPanel.SetDock(host, DockPanel.GetDock(table));
        if (parent is Panel panel)
        {
            var index = panel.Children.IndexOf(table);
            panel.Children.Remove(table);
            panel.Children.Insert(index, host);
        }
        else if (parent is ContentControl content) { content.Content = null; content.Content = host; }
        else throw new InvalidOperationException("Unsupported table host");
        host.Children.Add(table);
        var body = new StackPanel { Spacing = 16 };
        var header = new DockPanel();
        var close = new Button { Content = "×" };
        ToolTip.SetTip(close, WorkspaceText.Close);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var heading = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        heading.Classes.Add("sectionTitle");
        header.Children.Add(heading);
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        if (actions.Length > 0)
        {
            var footer = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
            foreach (var action in actions) { action.Margin = new Thickness(0, 0, 8, 8); footer.Children.Add(action); }
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);
        }
        foreach (var (label, property) in fields)
        {
            var caption = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
            caption.Classes.Add("muted");
            var value = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            value.Bind(TextBlock.TextProperty, new Binding("SelectedItem." + property) { Source = table, Converter = DetailValue, TargetNullValue = "—" });
            body.Children.Add(new StackPanel { Spacing = 6, Children = { caption, value } });
        }
        dock.Children.Add(new ScrollViewer { Content = body, Margin = new Thickness(0, 16, 0, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var drawer = new Border { Child = dock, Width = 320, Padding = new Thickness(24, 16), BorderThickness = new Thickness(1, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsVisible = false };
        drawer.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceSurface"));
        drawer.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceBorder"));
        host.Children.Add(drawer);
        void Arrange() { drawer.Width = Math.Min(320, host.Bounds.Width); table.Margin = new Thickness(0, 0, drawer.IsVisible && host.Bounds.Width >= 900 ? 344 : 0, 0); }
        table.SelectionChanged += (_, _) => { drawer.IsVisible = table.SelectedItem != null; Arrange(); };
        host.SizeChanged += (_, _) => Arrange();
        close.Click += (_, _) => { drawer.IsVisible = false; Arrange(); table.Focus(); };
        root.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || !drawer.IsVisible) return;
            drawer.IsVisible = false; Arrange(); table.Focus(); e.Handled = true;
        }, RoutingStrategies.Tunnel);
        return drawer;
    }

    public static void TextButton(Button button, string label)
    {
        button.Classes.Remove("IconButton");
        button.Classes.Remove("Success");
        button.Content = label;
        Avalonia.Automation.AutomationProperties.SetName(button, label);
    }
}
