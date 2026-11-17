namespace ServiceLib.Services;

/// <summary>Await UI publication, including its exception; never queue work after shutdown.</summary>
internal static class UiThread
{
    internal static async Task RunTaskAsync(Func<Task> action, Func<bool>? isCurrent = null)
    {
        Task? operation = null;
        await RunAsync(() => operation = action(), isCurrent);
        if (operation != null) await operation.ConfigureAwait(false);
    }

    internal static async Task RunAsync(Action action, Func<bool>? isCurrent = null)
    {
        if (AppManager.Instance.IsExiting) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = AppManager.Instance.ExitToken.Register(() => completion.TrySetResult());
        using var work = RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            try
            {
                if (!AppManager.Instance.IsExiting && !completion.Task.IsCompleted && isCurrent?.Invoke() != false) action();
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        await completion.Task.ConfigureAwait(false);
    }
}
