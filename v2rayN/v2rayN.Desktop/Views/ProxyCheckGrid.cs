namespace v2rayN.Desktop.Views;

// Grid definitions are CLR properties, so responsive definitions cannot be
// assigned with style setters. Reflow the same controls and retain their state.
public sealed class ProxyCheckGrid : Grid
{
    public static readonly AttachedProperty<bool> CompactProperty = AvaloniaProperty.RegisterAttached<ProxyCheckGrid, Control, bool>("Compact", false, inherits: true);
    public static void SetCompact(Control control, bool value) => control.SetValue(CompactProperty, value);
    public static bool GetCompact(Control control) => control.GetValue(CompactProperty);
    public string LayoutKind { get; set; } = "results";
    private bool? _compact;
    private bool _attached;
    private (int Column, int Row, Thickness Margin)[]? _wide;

    static ProxyCheckGrid() => CompactProperty.Changed.AddClassHandler<ProxyCheckGrid>((grid, _) => grid.Reflow());
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _attached = true; Reflow(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _attached = false; base.OnDetachedFromVisualTree(e); }

    private void Reflow()
    {
        if (!_attached || Children.Count == 0) return;
        var compact = GetCompact(this); if (_compact == compact) return;
        _wide ??= Children.Select(c => (GetColumn(c), GetRow(c), c.Margin)).ToArray();
        _compact = compact;
        ColumnDefinitions = new(compact ? LayoutKind == "environment" ? "*" : "*,*" : LayoutKind switch { "environment" => "1.7*,*", "metrics" => "*,*,*,*", _ => "2*,1.05*,1*,1.25*,1.35*,60" });
        RowDefinitions = new(compact ? LayoutKind == "results" ? "Auto,Auto,Auto" : "Auto,Auto" : "Auto");
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i]; var original = _wide[i];
            if (!compact) { SetColumn(child, original.Column); SetRow(child, original.Row); child.Margin = original.Margin; continue; }
            var (column, row) = LayoutKind switch
            {
                "environment" => (0, i),
                "metrics" => (i % 2, i / 2),
                _ => i switch { 0 => (0, 0), 1 => (0, 2), 2 => (1, 0), 3 => (0, 1), 4 => (1, 1), _ => (1, 2) }
            };
            SetColumn(child, column); SetRow(child, row);
            child.Margin = LayoutKind == "environment" ? new(0, i == 0 ? 0 : 12, 0, 0) : new(original.Margin.Left, row > 0 ? 8 : original.Margin.Top, original.Margin.Right, original.Margin.Bottom);
        }
    }
}
