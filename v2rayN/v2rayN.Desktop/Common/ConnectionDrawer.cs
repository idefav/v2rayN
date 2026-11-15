using System.Diagnostics;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Common;

/// <summary>A reversible window expansion. Opening a picker never changes a connection.</summary>
internal sealed class ConnectionDrawer : IDisposable
{
    internal const double PanelWidth = 350;
    internal const double DurationMs = 280;
    private readonly Window _window;
    private readonly StatusBarView _view;
    private readonly Control _header;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private double _progress, _from, _to;
    private Layout? _layout;
    private bool _applying, _disposed;
    internal Func<bool> ReduceMotion { get; set; } = () => OperatingSystem.IsMacOS() && MacAppUtils.PrefersReducedMotion();
    internal double CollapsedWidth => _layout?.Width ?? _window.Width;
    internal bool IsAnimating => _timer.IsEnabled;

    internal sealed record Layout(double Width, double Panel, PixelPoint Origin, PixelPoint Expanded, bool Narrow);

    internal static Layout Plan(double width, PixelPoint origin, PixelRect work, double scaling, double frameWidth, bool resizable = true)
    {
        scaling = scaling > 0 ? scaling : 1;
        var available = Math.Max(0, work.Width / scaling - frameWidth);
        width = Math.Min(width, available);
        var panel = Math.Min(PanelWidth, available - width);
        var narrow = !resizable || panel < 300;
        var expandedWidth = width + (narrow ? 0 : panel);
        var left = Math.Clamp(origin.X, work.X, Math.Max(work.X, work.Right - (int)Math.Ceiling((expandedWidth + frameWidth) * scaling)));
        return new(width, narrow ? width : panel, origin, new PixelPoint(left, origin.Y), narrow);
    }

    internal ConnectionDrawer(Window window, StatusBarView view, Control header)
    {
        _window = window; _view = view; _header = header;
        _timer.Tick += Tick;
        _window.PositionChanged += Moved;
        _window.PropertyChanged += WindowChanged;
        view.PickerVisibilityRequested = Change;
    }

    private void Change(bool open)
    {
        if (_disposed) return;
        if (_layout == null && open)
        {
            var screen = _window.Screens.ScreenFromWindow(_window) ?? _window.Screens.Primary;
            var width = _window.ClientSize.Width > 0 ? _window.ClientSize.Width : _window.Width;
            var frame = Math.Max(0, (_window.FrameSize ?? _window.ClientSize).Width - _window.ClientSize.Width);
            _layout = Plan(width, _window.Position, screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080),
                screen?.Scaling ?? 1, frame, _window.WindowState == WindowState.Normal && _window.CanResize);
        }
        if (_layout == null) return;
        _timer.Stop();
        _from = _progress; _to = open ? 1 : 0;
        _clock.Restart();
        if (ReduceMotion()) { Present(_to); Finish(); }
        else { Present(_progress); _timer.Start(); }
    }

    private void Tick(object? sender, EventArgs args)
    {
        var t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / DurationMs, 0, 1);
        // Start at the currently displayed geometry, including rapid reversal.
        Present(_from + (_to - _from) * (1 - Math.Pow(1 - t, 3)));
        if (t >= 1) Finish();
    }

    private void Present(double progress)
    {
        if (_layout is not { } layout) return;
        _progress = progress;
        _applying = true;
        try
        {
            if (!layout.Narrow)
            {
                _window.Width = layout.Width + layout.Panel * progress;
                _window.Position = new PixelPoint((int)Math.Round(layout.Origin.X + (layout.Expanded.X - layout.Origin.X) * progress), layout.Origin.Y);
                _header.Width = layout.Width;
                _header.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            }
            _view.PresentServerDrawer(layout.Width, layout.Panel, progress, layout.Narrow);
        }
        finally { _applying = false; }
    }

    private void Finish()
    {
        _timer.Stop();
        if (_to == 0)
        {
            _layout = null;
            _header.Width = double.NaN;
            _header.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            _view.FocusServerPickerButton();
        }
        else _view.FocusServerSearch();
    }

    private void Moved(object? sender, PixelPointEventArgs e)
    {
        if (_applying || _layout is not { } layout) return;
        // Keep a user's drag; collapsing should not jump back to an old screen location.
        var expected = (int)Math.Round(layout.Origin.X + (layout.Expanded.X - layout.Origin.X) * _progress);
        var dx = e.Point.X - expected;
        var dy = e.Point.Y - layout.Origin.Y;
        _layout = layout with { Origin = new(layout.Origin.X + dx, layout.Origin.Y + dy), Expanded = new(layout.Expanded.X + dx, layout.Expanded.Y + dy) };
    }

    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Window.WindowStateProperty && _layout != null) Reset();
    }

    internal void Reset()
    {
        _timer.Stop();
        _view.CloseConnectionControls();
        _timer.Stop(); _to = 0;
        Present(0); Finish();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset(); _disposed = true;
        _timer.Tick -= Tick;
        _window.PositionChanged -= Moved;
        _window.PropertyChanged -= WindowChanged;
        _view.PickerVisibilityRequested = null;
    }
}
