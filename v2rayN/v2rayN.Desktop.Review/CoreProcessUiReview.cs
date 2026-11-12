using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using ReactiveUI.Avalonia;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Manager;
using ServiceLib.Services;

namespace v2rayN.Desktop.Review;

// Runs the real Avalonia dispatcher and production process checks, without loading
// user configuration, starting a proxy, requesting sudo, or creating any TUN route.
internal static class CoreProcessUiReview
{
    public static void Run()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS UI identity regression");
        Environment.SetEnvironmentVariable(Global.LocalAppData, "0");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var watchdog = deadline.Token.Register(() =>
        {
            Console.Error.WriteLine("FAIL: UI process query blocked the dispatcher or cleanup exceeded its deadline.");
            Environment.Exit(1);
        });
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).UseReactiveUI(_ => { }).SetupWithoutStarting();
        using var finished = new CancellationTokenSource();
        Exception? failure = null;
        Dispatcher.UIThread.Post(async () =>
        {
            try { await VerifyAsync(deadline.Token); }
            catch (Exception ex) { failure = ex; }
            finally { finished.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(finished.Token);
        if (failure != null) throw new InvalidOperationException("UI process regression failed", failure);
        Console.WriteLine("PASS: Avalonia UI process checks completed; isolated children stopped.");
    }

    private static async Task VerifyAsync(CancellationToken token)
    {
        Check(SynchronizationContext.Current is AvaloniaSynchronizationContext, "real Avalonia UI context is missing");
        await VerifyWindowExitAsync();
        using (var cancellation = new CancellationTokenSource())
        {
            var endpoint = new CoreListener(System.Net.IPAddress.Loopback, 34567, System.Net.Sockets.ProtocolType.Tcp);
            var handoff = PortHandoff.WaitAsync([endpoint], cancellation.Token,
                probe: _ => System.Net.Sockets.SocketError.AddressAlreadyInUse, listeners: () => []);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(!handoff.IsCompleted, "handoff unexpectedly finished");
            cancellation.Cancel();
            try { await handoff; throw new Exception("handoff ignored cancellation"); }
            catch (OperationCanceledException) { }
            Console.WriteLine("PASS: port handoff preserves real UI heartbeat and cancels without waiting for deadline.");
        }
        await VerifySavedTunAsync();
        using var process = new ProcessService("/bin/sleep", "15", Path.GetTempPath(), false, false, null, null);
        using var pre = new ProcessService("/bin/sleep", "15", Path.GetTempPath(), false, false, null, null);
        var manager = new CoreManager();
        SetField("_processService", process);
        SetField("_processPreService", pre);
        SetField("_processRequirements", new CoreProcessRequirements(true));
        try
        {
            await process.StartAsync();
            await pre.StartAsync();
            process.IsReady = pre.IsReady = true;
            Check(await manager.IsCoreRunningAsync(token), "plain process was not ready");
            // Exercise macOS ps identity paths with our own children, never sudo.
            process.IsElevated = pre.IsElevated = true;
            for (var round = 0; round < 3; round++)
            {
                var heartbeat = false;
                var query = manager.IsCoreRunningAsync(token);
                var pending = !query.IsCompleted;
                Dispatcher.UIThread.Post(() => heartbeat = true);
                Check(await query, "live core was not ready");
                if (pending) Check(heartbeat, "UI heartbeat did not run while awaiting process identity");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Check(heartbeat, "UI dispatcher stopped processing callbacks");
                var health = await manager.GetRecoveryProcessHealthAsync(token);
                Check(health.Main == LocalHealth.Healthy && health.Pre == LocalHealth.Healthy, "main/pre health is incorrect");
                Check(Dispatcher.UIThread.CheckAccess(), "caller did not resume on the UI thread");
            }
            Console.WriteLine("PASS: main/pre macOS identity queries preserved UI heartbeats in all 3 rounds.");

            var identity = typeof(ProcessService).GetProperty("CoreIdentity", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var original = process.CoreIdentity!;
            identity.SetValue(process, original with { StartTime = "different-process" });
            try
            {
                Check(!await manager.IsCoreRunningAsync(token), "changed identity was accepted as running");
                Check((await manager.GetRecoveryProcessHealthAsync(token)).Main == LocalHealth.Unknown,
                    "unverified identity was treated as a failed core eligible for recovery");
            }
            finally { identity.SetValue(process, original); }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await ExpectCancellation(() => manager.IsCoreRunningAsync(cancelled.Token));
            await ExpectCancellation(() => manager.GetRecoveryProcessHealthAsync(cancelled.Token));
            Console.WriteLine("PASS: identity failure stays unknown; caller cancellation propagates.");
        }
        finally
        {
            process.IsElevated = pre.IsElevated = false;
            var mainStop = await process.StopAndWaitAsync();
            var preStop = await pre.StopAndWaitAsync();
            Check(mainStop.Success && preStop.Success, "isolated child cleanup failed");
        }
        Check(!await manager.IsCoreRunningAsync(token), "stopped core still reported running");
        var stopped = await manager.GetRecoveryProcessHealthAsync(token);
        Check(stopped.Main == LocalHealth.Failed && stopped.Pre == LocalHealth.Failed, "stopped main/pre health is incorrect");
        Console.WriteLine("PASS: stopped instances reported exited without blocking the UI.");

        void SetField(string name, object value) => typeof(CoreManager)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(manager, value);
    }

    private static async Task VerifyWindowExitAsync()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Avalonia.Controls.Window();
        var cleanup = 0; var closed = 0; var confirmed = false;
        var coordinator = new v2rayN.Desktop.Common.WindowExitCoordinator(
            () => Task.FromResult(confirmed),
            () => { cleanup++; return pending.Task; }, window.Close, ex => throw ex);
        window.Closing += (_, e) => coordinator.Closing(e);
        window.Closed += (_, _) => closed++;
        window.Show();
        window.Close();
        await coordinator.RequestAsync();
        Check(window.IsVisible && cleanup == 0 && closed == 0, "cancelled exit closed window or started cleanup");
        confirmed = true;
        window.Close();
        var request = coordinator.RequestAsync();
        Check(ReferenceEquals(request, coordinator.RequestAsync()), "exit requests were not coalesced");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Check(cleanup == 1 && closed == 0 && window.IsVisible, "window closed before cleanup finished");
        pending.SetResult();
        await request;
        Check(closed == 1 && coordinator.AllowClose, "window did not close exactly once after cleanup");
        Console.WriteLine("PASS: real Avalonia closing event waits for cleanup; cancellation, duplicate requests and UI heartbeat verified.");
    }

    private static async Task VerifySavedTunAsync()
    {
        Check(File.Exists(Utils.GetBaseDirectory("v2rayN.Desktop.Review.dll")), "requires isolated review executable");
        Check(Utils.HasWritePermission(), "review must not fall back to user data");
        Check(AppManager.Instance.InitApp(), "isolated configuration failed");
        var config = AppManager.Instance.Config;
        config.TunModeItem.EnableTun = true;
        AppManager.Instance.LinuxSudoPwd = string.Empty;
        var vm = new ServiceLib.ViewModels.StatusBarViewModel();
        Check(config.TunModeItem.EnableTun && vm.EnableTun, "startup erased saved TUN intent");
        var prompts = 0;
        using var handler = vm.PasswordInputInteraction.RegisterHandler(context =>
        {
            prompts++;
            context.SetOutput(null!);
        });
        var authorization = vm.EnsureTunAuthorizationAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Check(prompts == 0 && !authorization.IsCompleted, "authorization did not wait for UI readiness");
        vm.MarkAuthorizationUiReady();
        Check(!await authorization && prompts == 1, "cancelled authorization was accepted");
        Check(config.TunModeItem.EnableTun && vm.EnableTun, "cancel erased TUN preference");
        Check(!await vm.EnsureTunAuthorizationAsync() && prompts == 2, "explicit authorization retry unavailable");
        Console.WriteLine("PASS: saved TUN intent survives empty credentials and cancellation; startup waits for UI and explicit retry works.");
    }

    private static async Task ExpectCancellation(Func<Task> query)
    {
        try { await query(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("caller cancellation was swallowed");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
