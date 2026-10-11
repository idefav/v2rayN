using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Manager;

public class WindowDialog : IWindowDialog
{
    public async Task<bool> ShowDialogAsync<TViewModel>(TViewModel vm)
        where TViewModel : class
    {
        var owner = TryGetOwnerWindow();

        var view = SimpleViewLocator.Instance.Build(vm);

        if (view is IWorkspacePage page)
        {
            ((ReactiveUserControl<TViewModel>)view).ViewModel = vm;
            var host = new WindowBase<TViewModel>
            {
                ViewModel = vm, Content = view, Title = page.PageTitle,
                WindowSizeKey = view.GetType().Name,
                Width = 1100, Height = 800, MinWidth = 600,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var closingFromPage = false;
            Action<object?> close = result => { closingFromPage = true; host.Close(result is true); };
            host.Closing += (_, args) =>
            {
                if (closingFromPage) return;
                args.Cancel = true;
                page.Close();
            };
            EventHandler saved = (_, _) => Dispatch(() => page.Close(true));
            page.CloseRequested += close;
            if (vm is ServiceLib.Base.ICloseable editable) editable.RequestClose += saved;
            try { return await host.ShowDialog<bool>(owner); }
            finally
            {
                page.CloseRequested -= close;
                if (vm is ServiceLib.Base.ICloseable editablePage) editablePage.RequestClose -= saved;
                if (vm is GlobalHotkeySettingViewModel) HotkeyManager.Instance.IsPause = false;
            }
        }

        if (view is not WindowBase<TViewModel> window)
        {
            return false;
        }

        window.ViewModel = vm;

        EventHandler closed = (_, _) => Dispatch(() => window.Close(true));
        if (vm is ServiceLib.Base.ICloseable closeable)
        {
            closeable.RequestClose += closed;
        }
        try { return await window.ShowDialog<bool>(owner); }
        finally { if (vm is ServiceLib.Base.ICloseable editableWindow) editableWindow.RequestClose -= closed; }
    }

    public static Window TryGetOwnerWindow()
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
            ?? throw new InvalidOperationException("Application lifetime is not of type IClassicDesktopStyleApplicationLifetime.");
        var openWindows = desktop.Windows
            .Where(w => w.IsVisible)
            .ToArray();
        if (openWindows.Length == 0)
        {
            return desktop.MainWindow
                ?? throw new InvalidOperationException("No open windows and no main window found.");
        }

        if (openWindows.Length == 1)
        {
            return openWindows[0];
        }

        try
        {
            Window.SortWindowsByZOrder(openWindows);

            var activeTopmost = openWindows.Reverse().FirstOrDefault(w => w.IsActive);
            return activeTopmost ?? openWindows[^1];
        }
        catch
        {
            return desktop.Windows.FirstOrDefault(w => w.IsActive)
                   ?? desktop.MainWindow
                   ?? openWindows[0];
        }
    }

    private static void Dispatch(Action action)
    {
        Dispatcher.UIThread.InvokeAsync(action);
    }
}
