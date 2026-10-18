using v2rayN.Desktop.Common;
using v2rayN.Desktop.Views;
using ServiceLib.Services;
using ServiceLib.Handler.SysProxy;

namespace v2rayN.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var viewLocator = SimpleViewLocator.Instance;
        DataTemplates.Add(viewLocator);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!Design.IsDesignMode)
            {
                AppManager.Instance.InitComponents();
                DataContext = StatusBarViewModel.Instance;
            }

            var mainWindowViewModel = new MainWindowViewModel();
            var mainWindow = (MainWindow)viewLocator.Build(mainWindowViewModel);
            mainWindow.ViewModel = mainWindowViewModel;
            desktop.MainWindow = mainWindow;
            _ = ServiceLib.Services.ExitInfoService.Instance.StartAsync();
            desktop.Exit += (_, _) => ServiceLib.Services.ExitInfoService.Instance.Stop();

            if (OperatingSystem.IsMacOS())
            {
                Current?.TryGetFeature<IActivatableLifetime>()?.Activated += OnMacOSActivated;
                if (!Design.IsDesignMode && SysProxyHandler.ChangesEnabled)
                {
                    var recovery = new ConnectionRecoveryService(mainWindowViewModel.GetRecoveryContext,
                        MacRecoveryPlatform.NetworkAsync, MacRecoveryPlatform.ReadProxyAsync,
                        (_, token) => SysProxyHandler.UpdateSysProxy(AppManager.Instance.Config, false, cancellationToken: token),
                        (snapshot, token) => RecoveryProbe.ProbeAsync(snapshot, token, RecoveryLog),
                        mainWindowViewModel.ReloadForRecoveryAsync, RecoveryLog);
                    AppManager.Instance.ConnectionRecovery = recovery;
                    var events = new MacConnectionEvents(recovery);
                    recovery.Stopping += events.Dispose;
                    desktop.Exit += (_, _) => { events.Dispose(); _ = recovery.StopAsync(); };
                    _ = StartRecoveryAsync(mainWindowViewModel, recovery);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RecoveryLog(string message) => Logging.SaveLog("ConnectionRecovery " + message);

    private static async Task StartRecoveryAsync(MainWindowViewModel viewModel, ConnectionRecoveryService recovery)
    {
        try
        {
            await viewModel.Initialization;
            if (!AppManager.Instance.IsExiting) recovery.Start();
        }
        catch (Exception ex) { RecoveryLog("startup failed=" + ex.GetType().Name); }
    }

    #region MacOS Activation

    private void OnMacOSActivated(object? sender, ActivatedEventArgs args)
    {
        if (args.Kind != ActivationKind.Reopen)
        {
            return;
        }

        if ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not MainWindow mainWindow)
        {
            return;
        }

        var isMiniaturized = MacAppUtils.IsWindowMiniaturized(mainWindow);

        Dispatcher.UIThread.Post(() =>
        {
            if (isMiniaturized)
            {
                RestoreMacOSAccessoryPolicyAfterMiniaturize(mainWindow);
                mainWindow.ShowHideWindow(true);
                return;
            }

            if (!AppManager.Instance.Config.UiItem.MacOSShowInDock)
            {
                MacAppUtils.SetActivationPolicyAccessory();
            }

            mainWindow.ShowHideWindow(true);
        });
    }

    private static void RestoreMacOSAccessoryPolicyAfterMiniaturize(MainWindow mainWindow)
    {
        if (AppManager.Instance.Config.UiItem.MacOSShowInDock)
        {
            return;
        }

        mainWindow
            .GetObservable(Window.WindowStateProperty)
            .Skip(1)
            .Where(state => state != WindowState.Minimized)
            .Take(1)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => QueueMacOSAccessoryPolicyRestore(mainWindow));
    }

    private static void QueueMacOSAccessoryPolicyRestore(MainWindow mainWindow)
    {
        // AppKit may keep isMiniaturized set until the Dock restore animation finishes.
        DispatcherTimer.RunOnce(() => RestoreMacOSAccessoryPolicy(mainWindow), TimeSpan.FromMilliseconds(300));
    }

    private static void RestoreMacOSAccessoryPolicy(MainWindow mainWindow)
    {
        if (AppManager.Instance.Config.UiItem.MacOSShowInDock || MacAppUtils.IsWindowMiniaturized(mainWindow))
        {
            return;
        }

        MacAppUtils.SetActivationPolicyAccessory();
        mainWindow.Activate();
        mainWindow.Focus();
    }

    #endregion MacOS Activation

    #region App Event

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject != null)
        {
            Logging.SaveLog("CurrentDomain_UnhandledException", (Exception)e.ExceptionObject);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logging.SaveLog("TaskScheduler_UnobservedTaskException", e.Exception);
    }

    private async void MenuAddServerViaClipboardClick(object? sender, EventArgs e)
    {
        try
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: not null })
            {
                AppEvents.AddServerViaClipboardRequested.Publish();
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MenuAddServerViaClipboardClick", ex);
        }
    }

    private async void MenuExit_Click(object? sender, EventArgs e)
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window }
            && !await window.PrepareExitAsync()) return;
        await AppManager.Instance.AppExitAsync(false);
        AppManager.Instance.Shutdown(true);
    }

    #endregion App Event
}
