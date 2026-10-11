namespace v2rayN.Desktop.Common;

public static class DialogPresentation
{
    public static void ConstrainToWindow(Control view)
    {
        TopLevel? owner = null;
        void Resize(object? sender, SizeChangedEventArgs args) => view.MaxHeight = Math.Max(240, (owner?.ClientSize.Height ?? 768) - 48);
        view.AttachedToVisualTree += (_, _) =>
        {
            owner = TopLevel.GetTopLevel(view);
            if (owner == null) return;
            owner.SizeChanged += Resize;
            view.MaxHeight = Math.Max(240, owner.ClientSize.Height - 48);
        };
        view.DetachedFromVisualTree += (_, _) =>
        {
            if (owner != null) owner.SizeChanged -= Resize;
            owner = null;
        };
    }
}
