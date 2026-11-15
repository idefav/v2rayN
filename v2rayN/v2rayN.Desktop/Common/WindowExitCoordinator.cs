namespace v2rayN.Desktop.Common;

// Must be called on the UI thread. Confirmation remains outside the cleanup budget.
internal sealed class WindowExitCoordinator(Func<Task<bool>> prepare, Func<Task> cleanup, Action close, Action<Exception> failed)
{
    private Task? _request;
    public bool AllowClose { get; private set; }
    public Task RequestAsync() => _request ??= RunAsync();

    private async Task RunAsync()
    {
        await Task.Yield();
        try
        {
            if (!await prepare()) return;
            await cleanup();
            await Dispatcher.UIThread.InvokeAsync(() => { AllowClose = true; close(); });
        }
        catch (Exception ex) { failed(ex); }
        finally { if (!AllowClose) _request = null; }
    }

    public void Closing(WindowClosingEventArgs args)
    {
        if (AllowClose) return;
        args.Cancel = true;
        _ = RequestAsync();
    }
}
