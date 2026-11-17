using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation;
using ServiceLib.Services;

namespace v2rayN.Views;

public sealed class ServerPickerWindow : Window
{
    private readonly ServerPickerViewModel _picker = new(AppManager.Instance.Config);
    private readonly ListBox _rows = new();
    private readonly ComboBox _groups = new() { MinWidth = 160, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _scope = new() { ItemsSource = new[] { T("全部", "All"), T("常用", "Favorites") }, SelectedIndex = 0 };
    private readonly TextBox _search = new() { Margin = new Thickness(0, 10, 0, 10) };
    private readonly Button _test = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _progress = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly Action<string> _select;
    private readonly StatusBarViewModel _status;
    public ServerPickerWindow(StatusBarViewModel status, Action<string> select)
    {
        _select = select; _status = status;
        Title = T("更换服务器", "Change server");
        Width = 520; Height = 680; MinWidth = 400; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = T("搜索名称、地址、分组", "Search name, address or group") });
        top.Children.Add(_search);
        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        filters.Children.Add(_groups); filters.Children.Add(_scope); top.Children.Add(filters);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(_test);
        var cancel = new Button { Content = T("取消", "Cancel") };
        cancel.Click += (_, _) => _picker.Cancel();
        actions.Children.Add(cancel); top.Children.Add(actions); top.Children.Add(_progress);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var factory = new FrameworkElementFactory(typeof(ServerPickerRowControl));
        _rows.ItemTemplate = new DataTemplate { VisualTree = factory };
        _rows.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        _rows.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        _rows.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _rows.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        root.Children.Add(_rows); Content = root;
        _picker.RowsChanged += () =>
        {
            _rows.ItemsSource = _picker.Rows;
            _test.Content = T("检测当前结果", "Test results") + $" ({_picker.VisibleCount})";
        };
        _picker.PropertyChanged += (_, _) => { _test.IsEnabled = !_picker.Busy; cancel.IsEnabled = _picker.Busy; _progress.Text = _picker.Progress; };
        _test.Click += async (_, _) => await _picker.TestVisibleAsync();
        _groups.SelectionChanged += (_, _) => Refresh();
        _scope.SelectionChanged += (_, _) => Refresh();
        _search.TextChanged += (_, _) => Refresh();
        Loaded += async (_, _) =>
        {
            try
            {
                await _picker.OpenAsync();
                var groupId = _picker.GroupId;
                _groups.ItemsSource = _picker.Groups;
                _groups.SelectedItem = _picker.Groups.FirstOrDefault(x => x.Id == groupId);
                if (_picker.Rows.FirstOrDefault(r => !r.IsHeader && r.Node.IndexId == AppManager.Instance.Config.IndexId) is { } row)
                    _rows.ScrollIntoView(row);
            }
            catch { _progress.Text = T("加载失败", "Loading failed"); }
        };
        status.PropertyChanged += StatusChanged;
        Closed += (_, _) => { status.PropertyChanged -= StatusChanged; _picker.Dispose(); };
    }
    private void StatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_status.CoreReloading) _picker.Cancel();
    }
    private void Refresh()
    {
        _picker.Search = _search.Text; _picker.FavoritesOnly = _scope.SelectedIndex == 1;
        if (_groups.SelectedItem is ServerPickerGroup group) _picker.GroupId = group.Id;
        _picker.Refresh();
    }
    internal UIElement CreateRow(ServerPickerRow row)
    {
        if (row.IsHeader) return new TextBlock { Text = row.GroupTitle, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 8) };
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = row.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 44 });
        body.Children.Add(new TextBlock { Text = row.Endpoint, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = row.Identity, TextTrimming = TextTrimming.CharacterEllipsis });
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        result.SetBinding(TextBlock.TextProperty, new Binding(nameof(ServerPickerRow.Results)) { Source = row });
        body.Children.Add(result);
        if (row.Selection.Length > 0) body.Children.Add(new TextBlock { Text = row.Selection });
        var select = new Button { Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 6, 4), ToolTip = row.Name + "\n" + row.Endpoint };
        select.Click += (_, _) => { _picker.Close(); Close(); _select(row.Node.IndexId); };
        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var favorite = new Button { Content = row.Favorite ? "★" : "☆" };
        AutomationProperties.SetName(favorite, T("常用", "Favorite") + ": " + row.Name);
        favorite.Click += async (_, _) => await _picker.ToggleFavoriteAsync(row);
        var test = new Button { Content = T("检测", "Test") };
        test.SetBinding(IsEnabledProperty, new Binding(nameof(ServerPickerViewModel.Busy)) { Source = _picker, Converter = new NotConverter() });
        test.Click += async (_, _) => await _picker.TestOneAsync(row);
        actions.Children.Add(favorite); actions.Children.Add(test);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(select); Grid.SetColumn(actions, 1); grid.Children.Add(actions);
        return grid;
    }
    private static string T(string zh, string en) => ProxyCheckText.T(zh, en);
    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not true;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }
}

public sealed class ServerPickerRowControl : ContentControl
{
    public ServerPickerRowControl()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Loaded += (_, _) => Render();
        DataContextChanged += (_, _) => Render();
    }
    private void Render()
    {
        if (DataContext is ServerPickerRow row && Window.GetWindow(this) is ServerPickerWindow window) Content = window.CreateRow(row);
    }
}
