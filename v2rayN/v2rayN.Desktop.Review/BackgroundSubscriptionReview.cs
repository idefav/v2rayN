using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Threading;
using ReactiveUI.Primitives.Extensions;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private async Task ReviewBackgroundSubscriptionAsync(StatusBarView bar)
    {
        var status = _vm.StatusBarViewModel;
        var command = (ICommand)status.RestartCoreCmd;
        var notifications = 0; var wrongThread = false;
        void Changed(object? sender, EventArgs e) { notifications++; wrongThread |= !Dispatcher.UIThread.CheckAccess(); }
        command.CanExecuteChanged += Changed;
        var originalRestart = status.RestartCore;
        try
        {
            await Task.Run(() => _vm.SetReloadEnabledAsync(false));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(status.CoreReloading && !_vm.BlReloadEnabled && !command.CanExecute(null), "worker reload disables the real bound restart button");
            status.ConnectionError = "[Fixture] original error";
            await Task.Run(() => _vm.FinishReloadAsync(() => Task.FromException(new IOException("[Fixture] refresh failed"))));
            Check(!status.CoreReloading && _vm.BlReloadEnabled && status.ConnectionError == "[Fixture] original error", "failed cleanup refresh preserves original error and restores controls");
            await Task.Run(() => UiThread.RunAsync(() => status.ConnectionError = "stale", () => false));
            Check(status.ConnectionError == "[Fixture] original error", "stale queued UI publication is discarded");
            await Task.Run(() =>
            {
                // These notifications must be safe even when a legacy producer runs off the UI thread.
                status.CoreRunning = true;
                status.ConnectionIncomplete = false;
                status.ExitUnconfirmed = false;
                status.Transition = null;
                status.InboundLanDisplay = "[Fixture] LAN";
                status.RunningServerDisplay = "[Fixture] background node";
            });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var starts = 0;
            status.RestartCore = async () => { starts++; await release.Task; };
            command.Execute(null); command.Execute(null);
            Check(starts == 1, "continuous restart requests execute exactly once");
            release.SetResult();
            await Task.Delay(40);
            Check(notifications > 0 && !wrongThread, "all restart CanExecuteChanged notifications run on the dispatcher");

            await Task.Run(() => _vm.UpdateTaskHandler(true, "[Fixture] background subscription", true));
            Check(status.BackgroundRefreshError == null, "production subscription callback refreshes an inactive server list safely");
            Check(status.ConnectionError == "[Fixture] original error", "background refresh does not erase an unrelated connection error");
            var exiting = typeof(AppManager).GetProperty("IsExiting", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                exiting.SetValue(AppManager.Instance, true);
                var posted = false;
                await Task.Run(() => UiThread.RunAsync(() => posted = true));
                Check(!posted, "shutdown suppresses UI publications without waiting on a closed dispatcher");
            }
            finally { exiting.SetValue(AppManager.Instance, false); }
        }
        finally
        {
            command.CanExecuteChanged -= Changed;
            status.RestartCore = originalRestart;
            status.ConnectionError = null; status.BackgroundApplyError = null; status.BackgroundRefreshError = null;
            status.HasPendingChanges = false; status.CoreRunning = false;
        }
        if (Environment.GetEnvironmentVariable("HANDOFF_TEST_XRAY") is { Length: > 0 } binary)
            await ReviewBackgroundSubscriptionCoreAsync(binary);
        else Console.WriteLine("SKIP: real background subscription lifecycle requires HANDOFF_TEST_XRAY.");
    }

    private async Task ReviewBackgroundSubscriptionCoreAsync(string binary)
    {
        if (!Path.GetFullPath(Utils.StartupPath()).TrimEnd('/').Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('/')))
            throw new InvalidOperationException("Background lifecycle review requires isolated storage");
        var config = AppManager.Instance.Config;
        var saved = JsonUtils.DeepCopy(config);
        var design = _vm.DesignMode; var workspace = _vm.UseConnectionWorkspace;
        var core = CoreManager.Instance;
        var info = CoreInfoManager.Instance.GetCoreInfo(ECoreType.Xray)!;
        var exes = info.CoreExes;
        var status = _vm.StatusBarViewModel;
        var starts = 0;
        using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        await using var server = new SubscriptionServer();
        var sub = new SubItem { Id = "background-review-" + Guid.NewGuid().ToString("N"), Url = server.Url + "sub", Remarks = "background fixture", Enabled = true };
        string Content(string outbound = "freedom", string tag = "proxy") => $$$"""
            {"remarks":"background fixture","inbounds":[{"protocol":"mixed","listen":"127.0.0.1","port":{{{port}}},"settings":{"udp":true}}],"outbounds":[{"protocol":"{{{outbound}}}","tag":"{{{tag}}}","settings":{}}]}
            """;
        async Task Update()
        {
            var messages = new List<string>();
            var result = await SubscriptionHandler.UpdateWithResultAsync(config, sub.Id, false,
                (_, message) => { messages.Add(message); return Task.CompletedTask; });
            Check(result.Imported, "local HTTP subscription is downloaded and imported: " + string.Join("; ", messages));
            if (string.IsNullOrEmpty(config.IndexId)) config.IndexId = (await AppManager.Instance.ProfileItems(sub.Id))[0].IndexId;
            await _vm.UpdateTaskHandler(result.Imported, "[Fixture] imported", true);
        }
        try
        {
            config.IndexId = ""; config.SubIndexId = "";
            config.TunModeItem.EnableTun = false;
            config.SystemProxyItem.SysProxyType = ESysProxyType.Unchanged;
            config.GuiItem.EnableStatistics = false;
            config.Inbound[0].LocalPort = port;
            config.SpeedTestItem.SpeedPingTestUrl = server.Url + "probe";
            config.SpeedTestItem.IPAPIUrl = "";
            _vm.DesignMode = false; _vm.UseConnectionWorkspace = false;
            await CertPemManager.Instance.Init(config);
            info.CoreExes = [binary];
            core.ProcessStarted = _ => Interlocked.Increment(ref starts);
            await SQLiteHelper.Instance.ReplaceAsync(sub);
            server.Content = Content();
            await Task.Run(Update);
            Check(starts == 1 && (await core.GetRuntimeStateAsync()).Ready, "production background callback starts one isolated core: " + status.DisplayConnectionError);
            var runtime = core.RuntimeSummary!;
            var process = typeof(CoreManager).GetField("_processService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(core);
            using var proxy = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy("socks5://127.0.0.1:" + port), UseProxy = true });
            Check(await proxy.GetStringAsync(server.Url + "probe") == "fixture-ok", "isolated production core serves a SOCKS proxy request");
            status.ConnectionError = "[Fixture] unrelated failure";
            await Task.Run(Update);
            Check(starts == 1 && core.RuntimeSummary!.Generation == runtime.Generation
                && ReferenceEquals(process, typeof(CoreManager).GetField("_processService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(core)), "equivalent subscription preserves process and generation");
            Check(core.RuntimeSummary.ProfileId == config.IndexId && core.RuntimeSummary.ProfileId != runtime.ProfileId
                && !status.HasPendingChanges, "rebuilt subscription ID is associated with the existing runtime");
            Check(status.ConnectionError == "[Fixture] unrelated failure", "unchanged background apply preserves unrelated failure");
            status.ConnectionError = null;
            using (var fault = _vm.ProfilesViewModel.DispatcherRefreshServersBizInteraction.RegisterHandler(_ => throw new IOException("[Fixture] view refresh failed")))
                await Task.Run(Update);
            Check(status.BackgroundRefreshError?.Contains("view refresh failed") == true && starts == 1,
                "view refresh failure is separate and preserves the healthy core");
            await Task.Run(status.RetryBackgroundUpdateAsync);
            Check(starts == 1 && status.BackgroundRefreshError == null, "background retry does not force-restart a healthy unchanged core");
            var notificationFailure = await Task.Run(() => SubscriptionHandler.UpdateWithResultAsync(config, sub.Id, false,
                (_, _) => throw new IOException("[Fixture] progress consumer")));
            Check(notificationFailure.Imported && notificationFailure.NotificationError != null, "progress consumer failure cannot turn successful import into download failure");
            server.Content = Content("invalid-outbound");
            await Task.Run(Update);
            Check(starts == 1 && (await core.GetRuntimeStateAsync()).Ready && status.BackgroundApplyError != null
                && status.ConnectionError == null && status.HasPendingChanges, "background preflight failure preserves connection and pending config");
            server.Content = Content(tag: "changed");
            await Task.Run(Update);
            Check(starts == 2 && status.BackgroundApplyError == null && status.BackgroundRefreshError == null && !status.HasPendingChanges,
                "changed configuration switches once and clears only its own background failures");
            status.BackgroundApplyError = "[Fixture] earlier application failure";
            Check(await Task.Run(() => _vm.ReloadWithResultAsync("[Fixture] explicit restart", onlyIfChanged: false))
                && starts == 3 && status.BackgroundApplyError == null,
                "explicit restart forces a new process and clears the resolved background application failure");

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var hold = _vm.ProfilesViewModel.DispatcherRefreshServersBizInteraction.RegisterHandler(async context =>
            { entered.SetResult(); await release.Task; context.SetOutput(ReactiveUI.Primitives.RxVoid.Default); }))
            {
                var updating = Task.Run(Update); await entered.Task;
                _vm.CancelConnectionTransition(); release.SetResult(); await updating;
                Check(starts == 3 && !status.CoreReloading, "cancel during background refresh prevents late restart and leaves controls idle");
            }
            server.Content = "";
            var failed = await Task.Run(() => SubscriptionHandler.UpdateWithResultAsync(config, sub.Id, false, (_, _) => Task.CompletedTask));
            Check(!failed.Imported && failed.Attempts.Single().Status == SubscriptionAttemptStatus.Failed && starts == 3,
                "download failure remains an import outcome and never starts a core");
        }
        finally
        {
            _vm.CancelConnectionTransition();
            if (!(await core.CoreStop()).Success) throw new Exception("Isolated background core cleanup failed");
            core.ProcessStarted = null; info.CoreExes = exes;
            _vm.DesignMode = design; _vm.UseConnectionWorkspace = workspace;
            typeof(ServiceLib.ViewModels.MainWindowViewModel).GetField("_connectionPaused", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_vm, false);
            config.IndexId = saved.IndexId; config.SubIndexId = saved.SubIndexId;
            config.TunModeItem = saved.TunModeItem; config.SystemProxyItem = saved.SystemProxyItem;
            config.GuiItem = saved.GuiItem; config.Inbound = saved.Inbound;
            config.SpeedTestItem = saved.SpeedTestItem;
            await SQLiteHelper.Instance.DeleteAsync(sub);
            status.ConnectionError = null; status.BackgroundApplyError = null; status.BackgroundRefreshError = null;
            status.Transition = null; status.HasPendingChanges = false;
            await _vm.FinishReloadAsync();
        }
    }

    private sealed class SubscriptionServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancel = new();
        private readonly Task _loop;
        internal string Content = "";
        internal string Url { get; }
        internal SubscriptionServer()
        {
            _listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
            _loop = Run();
        }
        private async Task Run()
        {
            try
            {
                while (!_cancel.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cancel.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    var request = await reader.ReadLineAsync(_cancel.Token);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_cancel.Token))) { }
                    var body = Encoding.UTF8.GetBytes(request?.Contains("/sub") == true ? Content : "fixture-ok");
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, _cancel.Token); await stream.WriteAsync(body, _cancel.Token);
                }
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { _cancel.Cancel(); _listener.Stop(); await _loop; _cancel.Dispose(); }
    }
}
