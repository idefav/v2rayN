using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.LogicalTree;

namespace v2rayN.Desktop.Common;

/// <summary>Applies the shared desktop form layout without replacing named, bound controls.</summary>
public static class FormPresentation
{
    public static void Apply(Control root)
    {
        if (root is Views.DNSSettingWindow) return; // Dedicated design preserves its three-tab layout.
        if (root.Tag is string tag && tag == "workspace-presented") return;
        SettingsPresentation.Apply(root);
        if (root is Views.SubEditWindow) GroupSubscriptionForm(root);
        if (root is Views.FullConfigTemplateWindow or Views.DNSSettingWindow) PresentEditorTasks(root);
        if (root is Views.AddServerWindow)
        {
            var transportGrid = root.FindControl<Grid>("gridTransport")!;
            foreach (var title in transportGrid.Children.OfType<TextBlock>().Where(label => label.Text == ResUI.GbTransport).ToArray())
                transportGrid.Children.Remove(title);
            SplitAdvancedSecurity(root.FindControl<Grid>("gridTlsMore")!, 5);
            SplitAdvancedSecurity(root.FindControl<Grid>("gridRealityMore")!, 5);
        }
        var controls = root.GetLogicalDescendants().OfType<Control>().ToArray();
        foreach (var combo in controls.OfType<ComboBox>().Where(combo => !combo.IsEditable
            && combo.SelectionBoxItemTemplate == null && combo.ItemTemplate == null
            && combo.ItemsSource != null && combo.ItemsSource.Cast<object>().Any()
            && combo.ItemsSource.Cast<object>().All(value => value is string)))
            TablePresentation.DisplaySelection(combo, string.IsNullOrWhiteSpace(combo.PlaceholderText) ? "—" : combo.PlaceholderText);
        foreach (var button in controls.OfType<Button>())
        {
            if (button.Name is "btnSave" or "btnRun" or "btnCheckUpdate") button.Classes.Add("primary");
            if (ToolTip.GetTip(button) is string label) Avalonia.Automation.AutomationProperties.SetName(button, label);
        }
        foreach (var table in controls.OfType<DataGrid>())
        {
            table.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            table.BorderThickness = new Thickness(0);
            TablePresentation.FitHeaders(table);
        }
        foreach (var label in controls.OfType<TextBlock>().Where(label => label.Parent is Grid grid && grid.Classes.Contains("taskFields")))
            label.TextWrapping = TextWrapping.Wrap;
        foreach (var tabs in controls.OfType<TabControl>()) tabs.Margin = new Thickness(0, 8, 0, 0);
        foreach (var scroll in controls.OfType<ScrollViewer>().Where(s => s.Content is Grid or StackPanel))
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        foreach (var cards in controls.OfType<Grid>().Where(g => g.Classes.Contains("responsiveCards")))
        {
            var children = cards.Children.ToArray();
            var currentColumns = 0;
            void ArrangeCards()
            {
                var columns = cards.Bounds.Width >= 760 ? 2 : 1;
                if (columns == currentColumns) return;
                currentColumns = columns;
                cards.ColumnDefinitions.Clear();
                for (var i = 0; i < columns; i++) cards.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                cards.RowDefinitions.Clear();
                for (var i = 0; i < (children.Length + columns - 1) / columns; i++) cards.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var i = 0; i < children.Length; i++)
                {
                    Grid.SetColumn(children[i], i % columns);
                    Grid.SetRow(children[i], i / columns);
                    children[i].Margin = columns == 1 ? new Thickness(0, 0, 0, 16) : new Thickness(i % 2 == 0 ? 0 : 8, 0, i % 2 == 0 ? 8 : 0, 0);
                }
            }
            cards.SizeChanged += (_, _) => ArrangeCards();
            ArrangeCards();
        }
        foreach (var editors in controls.OfType<Grid>().Where(g => g.Children.OfType<HeaderedContentControl>().Count() >= 2))
        {
            var fields = editors.Children.OfType<HeaderedContentControl>().OrderBy(Grid.GetColumn).ToArray();
            var splitters = editors.Children.OfType<GridSplitter>().OrderBy(Grid.GetColumn).ToArray();
            var wasCompact = (bool?)null;
            void ArrangeEditors()
            {
                var compact = editors.Bounds.Width < 760;
                if (compact == wasCompact) return;
                wasCompact = compact;
                editors.ColumnDefinitions.Clear();
                editors.RowDefinitions.Clear();
                if (compact) editors.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                else editors.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                for (var i = 0; i < fields.Length * 2 - 1; i++)
                {
                    var length = i % 2 == 0 ? GridLength.Star : new GridLength(12);
                    if (compact) editors.RowDefinitions.Add(new RowDefinition(length));
                    else editors.ColumnDefinitions.Add(new ColumnDefinition(length));
                }
                for (var i = 0; i < fields.Length; i++)
                {
                    Grid.SetRow(fields[i], compact ? i * 2 : 0);
                    Grid.SetColumn(fields[i], compact ? 0 : i * 2);
                    fields[i].Bind(TemplatedControl.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceBorder"));
                    if (i >= splitters.Length) continue;
                    Grid.SetRow(splitters[i], compact ? i * 2 + 1 : 0);
                    Grid.SetColumn(splitters[i], compact ? 0 : i * 2 + 1);
                    splitters[i].HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                    splitters[i].VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                    splitters[i].ResizeDirection = compact ? GridResizeDirection.Rows : GridResizeDirection.Columns;
                }
            }
            editors.SizeChanged += (_, _) => ArrangeEditors();
            ArrangeEditors();
        }
        foreach (var grid in controls.OfType<Grid>())
        {
            if (grid.Classes.Contains("responsiveCards") || grid.Classes.Contains("taskFields")
                || grid.GetLogicalAncestors().OfType<Grid>().Any(parent => parent.Classes.Contains("taskFields"))
                || grid.ColumnDefinitions.Count < 2 || grid.Children.Count < 2) continue;
            if (grid.Children.Any(c => c is TabControl or DataGrid or ScrollViewer)) continue;
            var rows = grid.Children.GroupBy(Grid.GetRow).OrderBy(g => g.Key).ToArray();
            if (!rows.Any(r => r.Any(c => c is TextBlock && Grid.GetColumn(c) == 0)
                && r.Any(c => c is not TextBlock && Grid.GetColumn(c) > 0))) continue;

            var fields = new List<Control>();
            var fullWidth = new HashSet<Control>();
            foreach (var row in rows)
            {
                var field = new StackPanel { Spacing = 4, Margin = new Thickness(4, 4, 12, 4) };
                var inputs = new WrapPanel();
                var fluidInputs = new List<Control>();
                var hasInput = row.Any(c => c is not TextBlock and not Separator);
                if (!hasInput || !row.Any(c => c is TextBlock && Grid.GetColumn(c) == 0)) fullWidth.Add(field);
                foreach (var child in row.OrderBy(Grid.GetColumn).ThenBy(c => Grid.GetColumnSpan(c)))
                {
                    grid.Children.Remove(child);
                    child.Margin = new Thickness(0, 0, 8, 4);
                    if (child is TextBlock label && Grid.GetColumn(child) == 0)
                    {
                        label.TextWrapping = TextWrapping.Wrap;
                        label.FontWeight = FontWeight.Medium;
                        field.Children.Add(label);
                        field.Bind(Control.IsVisibleProperty, new Binding(nameof(Control.IsVisible)) { Source = label });
                    }
                    else if (child is TextBlock hint)
                    {
                        hint.TextWrapping = TextWrapping.Wrap;
                        hint.Classes.Add("muted");
                        field.Children.Add(hint);
                    }
                    else
                    {
                        if (child is StackPanel { Orientation: Avalonia.Layout.Orientation.Horizontal } rowInputs)
                        {
                            var wrap = new WrapPanel();
                            foreach (var input in rowInputs.Children.ToArray())
                            {
                                rowInputs.Children.Remove(input);
                                if (input is TextBlock text) text.TextWrapping = TextWrapping.Wrap;
                                wrap.Children.Add(input);
                                if (input is TextBox or ComboBox && input.Width >= 180)
                                {
                                    input.MinWidth = 180;
                                    input.MaxWidth = 360;
                                    fluidInputs.Add(input);
                                }
                            }
                            rowInputs.Orientation = Avalonia.Layout.Orientation.Vertical;
                            rowInputs.Children.Add(wrap);
                            fluidInputs.Add(rowInputs);
                            rowInputs.MaxWidth = 360;
                        }
                        if (child is TextBox or ComboBox || child.Width >= 300)
                        {
                            child.Width = double.NaN;
                            child.MinWidth = 180;
                            child.MaxWidth = 360;
                            fluidInputs.Add(child);
                        }
                        if (child is Views.JsonEditor || child is TextBox { AcceptsReturn: true } || child is StackPanel panel && panel.Children.OfType<TextBox>().Any(box => box.AcceptsReturn))
                        {
                            fullWidth.Add(field);
                            child.MaxWidth = double.PositiveInfinity;
                            if (!fluidInputs.Contains(child)) fluidInputs.Add(child);
                            if (child is Views.JsonEditor) child.MinHeight = 150;
                        }
                        inputs.Children.Add(child);
                    }
                }
                field.Children.Insert(Math.Min(1, field.Children.Count), inputs);
                field.SizeChanged += (_, _) =>
                {
                    var buttonsWidth = inputs.Children.OfType<Button>().Sum(button => button.DesiredSize.Width + button.Margin.Left + button.Margin.Right);
                    foreach (var input in fluidInputs) input.Width = Math.Max(120, Math.Min(field.Bounds.Width - 16 - buttonsWidth, fullWidth.Contains(field) ? double.PositiveInfinity : 420));
                };
                fields.Add(field);
            }
            grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Clear();
            foreach (var field in fields) grid.Children.Add(field);
            var currentColumns = 0;
            void ArrangeFields()
            {
                var columns = grid.Name == "gridBasicDNSSettings" && grid.Bounds.Width >= 900 ? 6 : grid.Bounds.Width >= 760 ? 2 : 1;
                if (columns == currentColumns) return;
                currentColumns = columns;
                grid.ColumnDefinitions.Clear();
                for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                grid.RowDefinitions.Clear();
                var rowNumber = 0;
                var columnNumber = 0;
                if (columns == 6)
                {
                    var layout = new[]
                    {
                        ("cmbDirectDNS", 0, 0, 3), ("cmbRemoteDNS", 0, 3, 3), ("cmbBootstrapDNS", 1, 0, 3),
                        ("cmbDirectDNSStrategy", 2, 0, 2), ("cmbRemoteDNSStrategy", 2, 2, 2), ("cmbProxyDialDNSStrategy", 2, 4, 2),
                        ("togParallelQuery", 3, 0, 2), ("togServeStale", 3, 2, 2), ("togEnableHappyEyeballs", 3, 4, 2)
                    };
                    var nextRow = 4;
                    foreach (var field in fields)
                    {
                        var position = layout.FirstOrDefault(item => field.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == item.Item1));
                        Grid.SetRow(field, position.Item1 == null ? nextRow++ : position.Item2);
                        Grid.SetColumn(field, position.Item1 == null ? 0 : position.Item3);
                        Grid.SetColumnSpan(field, position.Item1 == null ? 6 : position.Item4);
                    }
                    for (var i = 0; i <= nextRow; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    return;
                }
                foreach (var field in fields)
                {
                    if (fullWidth.Contains(field) && columnNumber != 0) { rowNumber++; columnNumber = 0; }
                    Grid.SetColumn(field, columnNumber);
                    Grid.SetRow(field, rowNumber);
                    Grid.SetColumnSpan(field, fullWidth.Contains(field) ? columns : 1);
                    columnNumber += fullWidth.Contains(field) ? columns : 1;
                    if (columnNumber >= columns) { rowNumber++; columnNumber = 0; }
                }
                for (var i = 0; i <= rowNumber; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
            ArrangeFields();
            grid.SizeChanged += (_, _) => ArrangeFields();
            if (grid.Parent is Panel parent)
            {
                var index = parent.Children.IndexOf(grid);
                var card = new Border();
                card.Classes.Add("formGroup");
                Grid.SetRow(card, Grid.GetRow(grid));
                Grid.SetColumn(card, Grid.GetColumn(grid));
                Grid.SetColumnSpan(card, Grid.GetColumnSpan(grid));
                Grid.SetRowSpan(card, Grid.GetRowSpan(grid));
                DockPanel.SetDock(card, DockPanel.GetDock(grid));
                parent.Children.Remove(grid);
                card.Child = grid;
                card.Bind(Control.IsVisibleProperty, new Binding(nameof(Control.IsVisible)) { Source = grid });
                parent.Children.Insert(index, card);
            }
            else if (grid.Parent is ScrollViewer scroll)
            {
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                var card = new Border();
                card.Classes.Add("formGroup");
                scroll.Content = null;
                card.Child = grid;
                scroll.Content = card;
            }
        }
        var dock = (root as ContentControl)?.Content as DockPanel;
        if (dock == null) return;
        if (root is Views.AddServerWindow && dock.Children.OfType<ScrollViewer>().FirstOrDefault()?.Content is Grid editor)
        {
            foreach (var separator in editor.Children.OfType<Separator>().ToArray()) editor.Children.Remove(separator);
            foreach (var section in editor.Children.OfType<Border>().Where(c => Grid.GetRow(c) is 0 or 2))
            {
                if (section.Child is not Control fields) continue;
                section.Child = null;
                var title = new TextBlock { Text = Grid.GetRow(section) == 0 ? WorkspaceText.Basic : WorkspaceText.T("协议配置", "Protocol settings"), Margin = new Thickness(8, 0, 0, 8) };
                title.Classes.Add("sectionTitle");
                section.Child = new StackPanel { Children = { title, fields } };
            }
            var transport = new StackPanel { Spacing = 8 };
            var transportTitle = new TextBlock { Text = WorkspaceText.T("传输与安全", "Transport and security"), Margin = new Thickness(8, 0, 0, 8) };
            transportTitle.Classes.Add("sectionTitle");
            transport.Children.Add(transportTitle);
            var advanced = new StackPanel { Spacing = 8 };
            foreach (var section in editor.Children.Where(c => Grid.GetRow(c) >= 4).OrderBy(Grid.GetRow).ToArray())
            {
                editor.Children.Remove(section);
                if (Grid.GetRow(section) == 5) advanced.Children.Add(section);
                else transport.Children.Add(section);
            }
            void UpdateTransportHeading() => transportTitle.IsVisible = transport.Children.Skip(1).Any(section => section.IsVisible);
            foreach (var section in transport.Children.Skip(1))
                section.PropertyChanged += (_, change) => { if (change.Property == Control.IsVisibleProperty) UpdateTransportHeading(); };
            UpdateTransportHeading();
            Grid.SetRow(transport, 4);
            editor.Children.Add(transport);
            var expander = new Expander { Header = WorkspaceText.Advanced, Content = advanced, IsExpanded = false, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            Grid.SetRow(expander, 5);
            editor.Children.Add(expander);
        }
        dock.Margin = new Thickness(24);
        foreach (var footer in dock.Children.OfType<StackPanel>().Where(p => DockPanel.GetDock(p) == Dock.Bottom))
        {
            footer.HorizontalAlignment = footer.Orientation == Avalonia.Layout.Orientation.Horizontal
                ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;
            footer.Margin = new Thickness(0, 16, 0, 0);
        }
        if (root is Views.RoutingRuleDetailsWindow)
        {
            var body = new StackPanel { Spacing = 16 };
            foreach (var child in dock.Children.Where(c => c is not StackPanel || DockPanel.GetDock(c) != Dock.Bottom).ToArray())
            {
                dock.Children.Remove(child);
                DockPanel.SetDock(child, Dock.Top);
                if (child is Grid editorGrid) editorGrid.Height = 420;
                body.Children.Add(child);
            }
            var scroll = new ScrollViewer { Content = body };
            dock.Children.Add(scroll);
            foreach (var footer in dock.Children.OfType<StackPanel>().Where(p => DockPanel.GetDock(p) == Dock.Bottom).ToArray())
            {
                var wrap = new WrapPanel { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = footer.Margin };
                foreach (var child in footer.Children.ToArray())
                {
                    footer.Children.Remove(child);
                    if (child.Width >= 300) { child.Width = double.NaN; child.MaxWidth = 300; }
                    wrap.Children.Add(child);
                }
                dock.Children.Remove(footer);
                DockPanel.SetDock(wrap, Dock.Bottom);
                dock.Children.Insert(0, wrap);
            }
        }
        if (root is Window window)
        {
            var title = root.DataContext switch
            {
                AddServerViewModel vm => (vm.SelectedSource.IndexId.IsNotEmpty() ? WorkspaceText.T("编辑 ", "Edit ") : WorkspaceText.T("添加 ", "Add ")) + vm.SelectedSource.ConfigType + WorkspaceText.T(" 服务器", " server"),
                AddGroupServerViewModel vm => vm.SelectedSource.ConfigType == EConfigType.PolicyGroup ? ResUI.menuAddPolicyGroupServer : ResUI.menuAddProxyChainServer,
                _ => window.Title
            };
            window.Title = title;
        }
    }

    private static void PresentEditorTasks(Control root)
    {
        foreach (var grid in root.GetLogicalDescendants().OfType<Grid>().Where(g => g.Children.OfType<HeaderedContentControl>().Count() == 2).ToArray())
        {
            var fields = grid.Children.OfType<HeaderedContentControl>().OrderBy(Grid.GetColumn).ToArray();
            if (root is Views.DNSSettingWindow && fields[0].GetLogicalDescendants().OfType<Views.JsonEditor>().Any(editor => editor.Name == "txtnormalDNS2Compatible")) continue;
            if (grid.Parent is not Panel parent) continue;
            var index = parent.Children.IndexOf(grid);
            var tabs = new TabControl { HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch };
            for (var i = 0; i < fields.Length; i++)
            {
                grid.Children.Remove(fields[i]);
                fields[i].Header = null;
                fields[i].BorderThickness = new Thickness(0);
                tabs.Items.Add(new TabItem { Header = i == 0 ? WorkspaceText.T("普通代理", "HTTP / SOCKS") : "TUN", Content = fields[i] });
            }
            Grid.SetRow(tabs, Grid.GetRow(grid));
            DockPanel.SetDock(tabs, DockPanel.GetDock(grid));
            parent.Children.Remove(grid);
            parent.Children.Insert(index, tabs);
        }
    }

    private static void GroupSubscriptionForm(Control root)
    {
        var scroll = root.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Content is Grid);
        if (scroll?.Content is not Grid original) return;
        var controls = original.Children.ToArray();
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        body.Classes.Add("taskFields");
        scroll.Content = null;
        var groups = new[] { (WorkspaceText.Basic, new[] { 1, 2, 3, 9, 14 }), (WorkspaceText.T("请求与过滤", "Requests and filters"), new[] { 4, 5, 6, 7, 8 }), (WorkspaceText.T("代理与自定义配置", "Proxy and custom configuration"), new[] { 10, 11, 12, 13 }) };
        var sections = new List<StackPanel>();
        foreach (var (title, rows) in groups)
        {
            var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            foreach (var child in controls.Where(c => rows.Contains(Grid.GetRow(c)))) { original.Children.Remove(child); fields.Children.Add(child); }
            var heading = new TextBlock { Text = title };
            heading.Classes.Add("sectionTitle");
            var section = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 16, 16), Children = { heading, fields } };
            sections.Add(section); body.Children.Add(section);
        }
        // Grids still pass through the generic field transformation; do not hide their bindings.
        body.Classes.Remove("taskFields");
        foreach (var label in original.Children.OfType<TextBlock>().ToArray()) original.Children.Remove(label);
        if (original.Children.Count > 0) { sections[^1].Children.Add(original); }
        void Arrange()
        {
            var narrow = body.Bounds.Width < 900;
            body.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,*");
            body.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto,Auto" : "Auto,Auto");
            for (var i = 0; i < sections.Count; i++) { Grid.SetRow(sections[i], narrow ? i : i == 2 ? 1 : 0); Grid.SetColumn(sections[i], narrow ? 0 : i == 1 ? 1 : 0); Grid.SetColumnSpan(sections[i], !narrow && i == 2 ? 2 : 1); }
        }
        body.SizeChanged += (_, _) => Arrange();
        scroll.Content = body;
        Arrange();
        if (root is Window window) { window.Width = 1120; window.Height = 840; }
    }

    private static void SplitAdvancedSecurity(Grid host, int firstAdvancedRow)
    {
        var basic = new Grid { ColumnDefinitions = new ColumnDefinitions("300,Auto") };
        var advanced = new Grid { ColumnDefinitions = new ColumnDefinitions("300,Auto") };
        foreach (var child in host.Children.ToArray())
        {
            host.Children.Remove(child);
            var target = Grid.GetRow(child) >= firstAdvancedRow ? advanced : basic;
            while (target.RowDefinitions.Count <= Grid.GetRow(child)) target.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            target.Children.Add(child);
        }
        host.ColumnDefinitions = new ColumnDefinitions("*");
        host.RowDefinitions = new RowDefinitions("Auto,Auto");
        host.Children.Add(basic);
        var expander = new Expander
        {
            Header = WorkspaceText.T("证书与高级安全选项", "Certificates and advanced security"),
            Content = advanced,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        Grid.SetRow(expander, 1);
        host.Children.Add(expander);
    }
}
