namespace v2rayN.Desktop.Common;

public static class DialogPresentation
{
    public static void ConstrainToWindow(Control view)
    {
        TopLevel? owner = null;
        void UpdateBounds()
        {
            view.MaxHeight = Math.Max(160, (owner?.ClientSize.Height ?? 768) - 32);
            view.MaxWidth = Math.Max(240, (owner?.ClientSize.Width ?? 800) - 32);
        }
        void Resize(object? sender, SizeChangedEventArgs args) => UpdateBounds();
        view.AttachedToVisualTree += (_, _) =>
        {
            owner = TopLevel.GetTopLevel(view);
            if (owner == null) return;
            owner.SizeChanged += Resize;
            UpdateBounds();
        };
        view.DetachedFromVisualTree += (_, _) =>
        {
            if (owner != null) owner.SizeChanged -= Resize;
            owner = null;
        };
    }
}
