using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.LogicalTree;

namespace v2rayN.Desktop.Common;

/// <summary>Groups existing settings controls by task while retaining their names and bindings.</summary>
public static class SettingsPresentation
{
    public static void Apply(Control root)
    {
        if (root is Views.OptionSettingWindow options)
        {
            var tabs = options.FindControl<TabControl>("pageTabs")!;
            Group((TabItem)tabs.Items[0]!,
                (WorkspaceText.T("本地监听", "Local listeners"), [0, 2, 3, 7, 8, 9, 10]),
                (WorkspaceText.T("流量探测", "Traffic sniffing"), [4, 5, 6]),
                (WorkspaceText.T("日志与连接", "Logging and connections"), [12, 13, 15, 16, 17, 18]),
                (WorkspaceText.T("带宽与分片", "Bandwidth and fragmentation"), [19, 20, 21]),
                (WorkspaceText.T("网络接口与多路复用", "Network interface and multiplexing"), [22, 23, 24]));
            Group((TabItem)tabs.Items[1]!,
                (WorkspaceText.T("启动与窗口", "Startup and window"), [1, 6, 7, 8]),
                (WorkspaceText.T("节点操作与统计", "Node operations and statistics"), [2, 3, 4, 9, 10]),
                (WorkspaceText.T("更新与字体", "Updates and font"), [11, 14, 15, 24]),
                (WorkspaceText.T("连接检测", "Connection tests"), [16, 17, 18, 19, 20, 22]),
                (WorkspaceText.T("订阅与规则来源", "Subscriptions and rule sources"), [23, 25, 26, 27]));
            Group((TabItem)tabs.Items[3]!,
                (WorkspaceText.T("路由与网络栈", "Routing and network stack"), [2, 3, 4, 5, 6, 8]),
                (WorkspaceText.T("接口地址", "Interface addresses"), [7, 9, 10, 11]));
            if (((TabItem)tabs.Items[4]!).Content is ScrollViewer { Content: Grid engineGrid })
            {
                var order = new[] { 1, 5, 3, 4, 6, 7, 8, 9, 2 };
                foreach (var child in engineGrid.Children)
                {
                    var row = Array.IndexOf(order, Grid.GetRow(child));
                    if (row >= 0) Grid.SetRow(child, row + 1);
                }
            }
            Group((TabItem)tabs.Items[4]!,
                (WorkspaceText.T("协议与内核", "Protocols and cores"), [1, 2, 3, 4, 5, 6, 7, 8, 9]));
        }
        else if (root is Views.GlobalHotkeySettingWindow)
        {
            PresentHotkeys(root.FindControl<Grid>("gridText")!);
        }
    }

    private static void PresentHotkeys(Grid grid)
    {
        var rows = grid.Children.GroupBy(Grid.GetRow).OrderBy(row => row.Key).ToArray();
        grid.Children.Clear();
        grid.Classes.Add("taskFields");
        grid.MaxWidth = 900;
        grid.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.ColumnDefinitions = new ColumnDefinitions("*");
        grid.RowDefinitions.Clear();
        var heading = rows[0].OfType<TextBlock>().Single();
        heading.Classes.Add("sectionTitle");
        heading.Margin = new Thickness(0, 0, 0, 16);
        Grid.SetRow(heading, 0);
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Children.Add(heading);
        var table = new StackPanel();
        for (var index = 1; index < rows.Length; index++)
        {
            var label = rows[index].OfType<TextBlock>().Single();
            var input = rows[index].OfType<TextBox>().Single();
            label.Margin = input.Margin = new Thickness(0);
            Grid.SetRow(label, 0);
            Grid.SetColumn(label, 0);
            Grid.SetRowSpan(label, 1);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextWrapping = TextWrapping.Wrap;
            input.Width = double.NaN;
            input.MinWidth = 120;
            input.PlaceholderText = WorkspaceText.T("未设置", "Not set");
            var record = new Button { Content = WorkspaceText.T("录制", "Record"), MinWidth = 76 };
            Avalonia.Automation.AutomationProperties.SetName(record, WorkspaceText.T("录制：", "Record: ") + label.Text);
            record.Click += (_, _) => input.Focus();
            var row = new Grid { Margin = new Thickness(16, 12) };
            row.Children.Add(label);
            row.Children.Add(input);
            row.Children.Add(record);
            void ArrangeRow()
            {
                var compact = row.Bounds.Width < 640;
                row.ColumnDefinitions = new ColumnDefinitions(compact ? "*,Auto" : "*,240,Auto");
                row.RowDefinitions = new RowDefinitions(compact ? "Auto,Auto" : "Auto");
                Grid.SetColumnSpan(label, compact ? 2 : 1);
                Grid.SetColumn(input, compact ? 0 : 1);
                Grid.SetRow(input, compact ? 1 : 0);
                Grid.SetColumn(record, compact ? 1 : 2);
                Grid.SetRow(record, compact ? 1 : 0);
                input.Margin = compact ? new Thickness(0, 8, 12, 0) : new Thickness(12, 0);
                record.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
            }
            row.SizeChanged += (_, _) => ArrangeRow();
            var separator = new Border { Child = row, BorderThickness = index == rows.Length - 1 ? new Thickness(0) : new Thickness(0, 0, 0, 1) };
            separator.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceBorder"));
            table.Children.Add(separator);
        }
        var frame = new Border { Child = table, Padding = new Thickness(0) };
        frame.Classes.Add("settingsGroup");
        Grid.SetRow(frame, 1);
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Children.Add(frame);
    }

    private static void Group(TabItem tab, params (string Title, int[] Rows)[] groups)
    {
        if (tab.Content is not ScrollViewer { Content: Grid original } scroll) return;
        var controls = original.Children.ToArray();
        var body = new StackPanel { Spacing = 12 };
        var cards = new Grid();
        if (groups[0].Title == WorkspaceText.T("启动与窗口", "Startup and window")) { cards.Classes.Add("responsiveCards"); body.Children.Add(cards); }
        scroll.Content = null;
        foreach (var (title, rows) in groups)
        {
            var grid = new Grid();
            foreach (var child in controls.Where(child => rows.Contains(Grid.GetRow(child))))
            {
                original.Children.Remove(child);
                grid.Children.Add(child);
            }
            if (grid.Children.Count == 0) continue;
            PresentRows(grid, groups.Length == 1);
            var heading = new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 8) };
            heading.Classes.Add("sectionTitle");
            var group = new Border { Child = new StackPanel { Children = { heading, grid } } };
            group.Classes.Add("settingsGroup");
            if (groups.Length == 1)
            {
                group.MaxWidth = 740;
                group.HorizontalAlignment = HorizontalAlignment.Left;
                group.Width = 740;
                scroll.SizeChanged += (_, _) => group.Width = Math.Min(740, scroll.Bounds.Width);
                var content = (StackPanel)group.Child;
                var description = new TextBlock { Text = WorkspaceText.T("默认核心用于未显式指定核心的配置。", "Default cores apply to configurations without an explicit core."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
                description.Classes.Add("muted");
                var headers = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), Margin = new Thickness(0, 0, 0, 8), Children =
                    { new TextBlock { Text = WorkspaceText.T("协议", "Protocol"), FontWeight = FontWeight.SemiBold }, new TextBlock { Text = WorkspaceText.T("默认核心", "Default core"), FontWeight = FontWeight.SemiBold } } };
                Grid.SetColumn(headers.Children[1], 1);
                content.Children.Insert(1, description); content.Children.Insert(2, headers);
                grid.SizeChanged += (_, _) => headers.IsVisible = grid.Bounds.Width >= 350;
            }
            if (cards.Parent != null) cards.Children.Add(group);
            else if (title == WorkspaceText.T("带宽与分片", "Bandwidth and fragmentation") || title == WorkspaceText.T("网络接口与多路复用", "Network interface and multiplexing"))
            {
                ((StackPanel)group.Child!).Children.Remove(heading);
                body.Children.Add(new Expander { Header = title, Content = group, HorizontalAlignment = HorizontalAlignment.Stretch });
            }
            else body.Children.Add(group);
        }
        // Future fields remain reachable even when no existing task group covers them.
        if (original.Children.Count != 0)
        {
            PresentRows(original);
            body.Children.Add(original);
        }
        scroll.Content = body;
        if (groups[0].Title == WorkspaceText.T("路由与网络栈", "Routing and network stack"))
        {
            var hint = new TextBlock
            {
                Text = WorkspaceText.T("启用 TUN 需要系统网络权限；首次启用时按系统提示授权。路由和地址修改在保存后生效。",
                    "TUN requires system network privileges. Grant access when prompted; route and address changes apply after saving."),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            };
            hint.Classes.Add("muted");
            body.Children.Insert(0, hint);
        }
        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    private static void PresentRows(Grid grid, bool singleColumn = false)
    {
        grid.Classes.Add("taskFields");
        var fields = new List<Grid>();
        foreach (var row in grid.Children.GroupBy(Grid.GetRow).OrderBy(row => row.Key).ToArray())
        {
            var label = row.OfType<TextBlock>().FirstOrDefault(text => Grid.GetColumn(text) == 0);
            if (label == null) continue;
            var input = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var hints = new StackPanel { Spacing = 4 };
            var field = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 2, 12, 6) };
            foreach (var child in row.OrderBy(Grid.GetColumn).ToArray())
            {
                grid.Children.Remove(child);
                child.Margin = new Thickness(0);
                if (ReferenceEquals(child, label)) continue;
                if (child is TextBlock hint)
                {
                    hint.TextWrapping = TextWrapping.Wrap;
                    hint.Classes.Add("muted");
                    hints.Children.Add(hint);
                }
                else
                {
                    child.Margin = new Thickness(0, 0, 8, 4);
                    if (child is TextBox or ComboBox)
                    {
                        child.Width = double.NaN;
                        child.MinWidth = 120;
                        child.MaxWidth = 300;
                    }
                    input.Children.Add(child);
                }
            }
            label.TextWrapping = TextWrapping.Wrap;
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(label, 0);
            Grid.SetColumn(label, 0);
            Grid.SetRowSpan(label, 1);
            Grid.SetColumnSpan(label, 1);
            field.Bind(Control.IsVisibleProperty, new Binding(nameof(Control.IsVisible)) { Source = label });
            field.Children.Add(label);
            field.Children.Add(input);
            field.Children.Add(hints);
            Grid.SetRow(hints, 1);
            Grid.SetColumnSpan(hints, 2);
            void ArrangeField()
            {
                var inline = field.Bounds.Width >= 350;
                field.ColumnDefinitions = new ColumnDefinitions(inline ? "150,*" : "*");
                field.RowDefinitions = new RowDefinitions(inline ? "Auto,Auto" : "Auto,Auto,Auto");
                Grid.SetColumn(input, inline ? 1 : 0);
                Grid.SetRow(input, inline ? 0 : 1);
                Grid.SetRow(hints, inline ? 1 : 2);
                Grid.SetColumnSpan(hints, inline ? 2 : 1);
                label.Margin = inline ? new Thickness(0, 0, 12, 4) : new Thickness(0, 0, 0, 8);
                foreach (var child in input.Children.OfType<TemplatedControl>().Where(child => child is TextBox or ComboBox))
                    child.Width = Math.Max(120, Math.Min(300, field.Bounds.Width - (inline ? 168 : 8)));
            }
            field.SizeChanged += (_, _) => ArrangeField();
            fields.Add(field);
        }
        var remaining = grid.Children.ToArray();
        grid.Children.Clear();
        if (remaining.Length != 0)
        {
            var retained = new Grid();
            foreach (var child in remaining) retained.Children.Add(child);
            fields.Add(retained);
        }
        foreach (var field in fields) grid.Children.Add(field);
        var currentColumns = 0;
        void ArrangeRows()
        {
            var columns = !singleColumn && grid.Bounds.Width >= 760 ? 2 : 1;
            if (currentColumns == columns) return;
            currentColumns = columns;
            grid.ColumnDefinitions = new ColumnDefinitions(columns == 2 ? "*,*" : "*");
            grid.RowDefinitions.Clear();
            for (var index = 0; index < (fields.Count + columns - 1) / columns; index++)
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var index = 0; index < fields.Count; index++)
            {
                Grid.SetRow(fields[index], index / columns);
                Grid.SetColumn(fields[index], index % columns);
            }
        }
        grid.SizeChanged += (_, _) => ArrangeRows();
        ArrangeRows();
    }
}
