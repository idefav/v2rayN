using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace v2rayN.Desktop.Common;

/// <summary>
/// A trailing column driven by the DataGrid's realized rows. The grid remains the
/// only owner of scrolling, selection and virtualization; no second items view is created.
/// </summary>
public sealed class RightPinnedColumn : Border
{
    private readonly DataGrid _table;
    private readonly IDataTemplate _template;
    private readonly DataGridColumnHeader _header;
    private readonly Canvas _rows = new() { ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top };
    private readonly Dictionary<DataGridRow, Border> _cells = [];
    private DataGridRowsPresenter? _presenter;
    private bool _updating;
    private bool _updateQueued;

    public RightPinnedColumn(DataGrid table, string title, IDataTemplate template)
    {
        _table = table;
        _template = template;
        Name = "pinnedActions";
        Width = 144;
        BorderThickness = new Thickness(1, 0, 0, 0);
        Bind(BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WorkspaceBorder"));
        Bind(BackgroundProperty, new Binding(nameof(DataGrid.Background)) { Source = table });
        _header = new DataGridColumnHeader { Name = "pinnedActionsHeader", Content = title, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, Focusable = false };
        Child = new Grid { ClipToBounds = true, Children = { _header, _rows } };

        // Reserve space beside the scrolling viewport, including when every optional
        // data column is enabled. Data columns cannot cover the fixed action buttons.
        var parent = table.Parent as Panel ?? throw new InvalidOperationException("Unsupported table host");
        var index = parent.Children.IndexOf(table);
        var host = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        DockPanel.SetDock(host, DockPanel.GetDock(table));
        Grid.SetRow(host, Grid.GetRow(table));
        Grid.SetColumn(host, Grid.GetColumn(table));
        parent.Children.Remove(table);
        parent.Children.Insert(index, host);
        host.Children.Add(table);
        Grid.SetColumn(this, 1);
        host.Children.Add(this);

        table.LayoutUpdated += (_, _) => UpdateRows();
        table.LoadingRow += (_, _) => QueueUpdate();
        table.UnloadingRow += (_, e) => RemoveRow(e.Row);
        table.AttachedToVisualTree += (_, _) => QueueUpdate();
        SizeChanged += (_, _) => QueueUpdate();
        table.DetachedFromVisualTree += (_, _) =>
        {
            if (_presenter != null) _presenter.LayoutUpdated -= PresenterLayoutUpdated;
            _presenter = null;
            foreach (var row in _cells.Keys.ToArray()) RemoveRow(row);
        };
        // Wheel/trackpad input over the fixed cells uses the grid's normal scrolling
        // handler, so inertia, row positioning and keyboard selection stay consistent.
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var forwarded = new PointerWheelEventArgs(table, e.Pointer, table, e.GetPosition(table),
                e.Timestamp, e.Properties, e.KeyModifiers, e.Delta);
            table.RaiseEvent(forwarded);
            e.Handled = forwarded.Handled;
        });
    }

    private void RemoveRow(DataGridRow row)
    {
        if (!_cells.Remove(row, out var cell)) return;
        _rows.Children.Remove(cell);
        cell.ClearValue(BackgroundProperty);
        cell.ClearValue(BorderBrushProperty);
        cell.DataContext = null;
        cell.Child = null;
    }

    private void QueueUpdate()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        Dispatcher.UIThread.Post(() => { _updateQueued = false; UpdateRows(); }, DispatcherPriority.Render);
    }

    private void PresenterLayoutUpdated(object? sender, EventArgs e) => UpdateRows();

    private void UpdateRows()
    {
        if (_updating || !_table.IsAttachedToVisualTree()) return;
        _updating = true;
        try
        {
            var presenter = _table.GetVisualDescendants().OfType<DataGridRowsPresenter>().FirstOrDefault();
            var headers = _table.GetVisualDescendants().OfType<DataGridColumnHeadersPresenter>().FirstOrDefault();
            if (presenter == null || headers == null || presenter.TranslatePoint(default, this) is not { } origin) return;
            if (_presenter != presenter)
            {
                if (_presenter != null) _presenter.LayoutUpdated -= PresenterLayoutUpdated;
                _presenter = presenter;
                _presenter.LayoutUpdated += PresenterLayoutUpdated;
            }
            _header.Height = headers.Bounds.Height;
            _rows.Margin = new Thickness(0, origin.Y, 0, 0);
            _rows.Height = presenter.Bounds.Height;
            var realized = presenter.GetVisualChildren().OfType<DataGridRow>()
                .Where(row => row.IsVisible && row.DataContext != null).ToHashSet();
            foreach (var row in _cells.Keys.Where(row => !realized.Contains(row)).ToArray()) RemoveRow(row);
            foreach (var row in realized)
            {
                if (row.TranslatePoint(default, presenter) is not { } point) continue;
                if (!_cells.TryGetValue(row, out var cell))
                {
                    var content = new Grid();
                    // Semi layers a translucent selection tint over Row.Background.
                    // Reproduce both layers so the fixed and scrolling cells match in
                    // every theme, rather than painting the tint over the table surface.
                    var tintSource = row.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "BackgroundBorder");
                    if (tintSource != null)
                    {
                        var tint = new Border();
                        tint.Bind(BackgroundProperty, new Binding(nameof(Border.Background)) { Source = tintSource });
                        content.Children.Add(tint);
                    }
                    if (_template.Build(row.DataContext) is { } actions) content.Children.Add(actions);
                    cell = new Border { Child = content, BorderThickness = new Thickness(0, 0, 0, 1) };
                    cell.Bind(BackgroundProperty, new Binding(nameof(DataGridRow.Background)) { Source = row });
                    cell.Bind(BorderBrushProperty, new Binding(nameof(DataGrid.HorizontalGridLinesBrush)) { Source = _table });
                    _cells.Add(row, cell);
                    _rows.Children.Add(cell);
                }
                // DataGrid reuses row containers after scrolling; actions must follow
                // the current row data, never the item that first created the button.
                cell.DataContext = row.DataContext;
                cell.Width = Math.Max(0, Bounds.Width - BorderThickness.Left);
                cell.Height = row.Bounds.Height;
                Canvas.SetTop(cell, point.Y);
            }
        }
        finally { _updating = false; }
    }
}
