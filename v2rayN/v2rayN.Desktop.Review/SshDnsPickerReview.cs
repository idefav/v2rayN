using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using ServiceLib.ViewModels;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Review;

public partial class ReviewApp
{
    private sealed class PickerReviewProbe : IServerProbeService
    {
        internal int Calls;
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ServerProbeResult> TestAsync(ProfileItem node, Config config, Guid id, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            await Task.Run(async () => await Release.Task.WaitAsync(token), token);
            return new(node.IndexId, ServerProbeService.Identity(node), id, DateTimeOffset.UtcNow,
                new(ServerProbeState.Passed, 12), new(ServerProbeState.Responded, 48, 403));
        }
    }
    private async Task ReviewSshDnsPickerAsync()
    {
        await _main.NavigateAsync("Home");
        var bar = (StatusBarView)_main.FindControl<ContentControl>("contentStatusBarView")!.Content!;
        _vm.BlReloadEnabled = true; bar.SetReloading(false);
        var timer = (DispatcherTimer)typeof(StatusBarView).GetField("_coreTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar)!;
        timer.Stop();
        bar.FindControl<Button>("btnConnectionControls")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(250);
        var picker = (ServerPickerViewModel)typeof(StatusBarView).GetField("_picker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar)!;
        var nodes = Enumerable.Range(0, 150).Select(i => new ProfileItem
        {
            IndexId = "picker-review-" + i, Subid = i % 2 == 0 ? "ssh" : "jms",
            ConfigType = i % 2 == 0 ? EConfigType.SSH : EConfigType.Shadowsocks,
            Remarks = i == 0 ? new string('长', 160) + ".portablesubmarines.com:30907" : "Fixture server " + i,
            Address = "127.0.0.1", Port = 20000 + i
        }).ToArray();
        var probe = new PickerReviewProbe();
        typeof(ServerPickerViewModel).GetField("_probe", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(picker, probe);
        typeof(ServerPickerViewModel).GetField("_lookup", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(picker,
            (Func<string, Task<ProfileItem?>>)(id => Task.FromResult(nodes.FirstOrDefault(n => n.IndexId == id))));
        picker.Load(nodes, [new() { Id = "ssh", Remarks = "SSH" }, new() { Id = "jms", Remarks = "JMS" }], new Dictionary<string, ProfileExItem>());
        var groups = bar.FindControl<ComboBox>("pickerGroup")!;
        groups.ItemsSource = picker.Groups;
        groups.SelectedIndex = 0;
        Check(picker.VisibleCount == 150 && picker.Rows.Count(r => r.IsHeader) == 2, "picker: group headers and total");
        var threadViolation = false;
        picker.PropertyChanged += (_, _) => threadViolation |= !Dispatcher.UIThread.CheckAccess();
        foreach (var row in picker.Rows)
            row.PropertyChanged += (_, _) => threadViolation |= !Dispatcher.UIThread.CheckAccess();
        var test = bar.FindControl<Button>("btnTestPicker")!;
        test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        Check(picker.Busy && !test.IsEnabled && probe.Calls == 3, "picker: actual batch button disables; concurrency is three");
        test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(50);
        Check(probe.Calls == 3, "picker: duplicate click does not start another batch");
        probe.Release.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (picker.Busy && DateTime.UtcNow < deadline) await Task.Delay(30);
        Check(!picker.Busy && !threadViolation && probe.Calls == 150, "picker: worker results publish on UI thread");
        Check(picker.Rows.Where(r => !r.IsHeader).All(r => r.Results.Contains("HTTP 403")), "picker: responded HTTP errors remain distinct");
        await Task.Run(() => picker.TestOneAsync(picker.Rows.First(r => !r.IsHeader)));
        Check(!threadViolation && !picker.Busy && probe.Calls == 151, "picker: worker-started operation publishes busy and results on UI thread");
        foreach (var dark in new[] { false, true })
        foreach (var width in new[] { 440, 360 })
        {
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            _main.Width = width; _main.Height = width == 360 ? 480 : 620;
            await Task.Delay(180);
            bar.PresentServerDrawer(width, width, 1, true);
            await CaptureAsync($"ssh-picker-{width}-{dark}", "Grouped servers with separate checks", _main);
            var list = bar.FindControl<ListBox>("serverRows")!;
            Check(list.Bounds.Height > 50 && list.GetVisualDescendants().OfType<ListBoxItem>().Count() < 40, "picker: bounded virtualized viewport");
            var point = list.TranslatePoint(default, _main);
            Check(point is { X: >= 0, Y: >= 0 } && point.Value.X + list.Bounds.Width <= _main.ClientSize.Width + 1,
                "picker: visible list stays inside the actual connection window");
        }
        bar.CloseConnectionControls();
        Check(!picker.Busy, "picker: close leaves no active run");
        var ssh = nodes[0]; ssh.Username = "fixture";
        var dialog = new AddSshServerWindow { ViewModel = new AddSshServerViewModel(ssh) };
        dialog.ViewModel.DnsStatus = ServiceLib.Resx.SshStrings.DnsResult(new(SshDnsVerdict.Match, "203.0.113.10", ["203.0.113.10"], 3, DateTimeOffset.Now));
        dialog.ViewModel.DnsDeploymentStatus = ServiceLib.Resx.SshStrings.DnsDeploymentPhase("committed");
        dialog.ViewModel.DnsDetails = "Fixture only · no remote commands or deployment executed";
        dialog.Show(_main);
        await Task.Delay(100);
        foreach (var expander in dialog.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
        await Task.Delay(100);
        var dns = dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == ServiceLib.Resx.SshStrings.DnsTitle);
        dns.GetVisualAncestors().OfType<ScrollViewer>().First().ScrollToEnd();
        await Task.Delay(100);
        await CaptureAsync("ssh-dns-controls", "DNS deployment, detection and rollback controls", dialog);
        Check(dialog.ViewModel.DeployDnsCmd != null && dialog.ViewModel.RollbackDnsCmd != null, "DNS: shared commands bound");
        foreach (var dark in new[] { false, true })
        {
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            dialog.Width = 600; dialog.Height = 480;
            await Task.Delay(100);
            var deploy = dialog.GetVisualDescendants().OfType<Button>().First(b => b.Command == dialog.ViewModel.DeployDnsCmd);
            deploy.BringIntoView();
            await Task.Delay(100);
            await CaptureAsync($"ssh-dns-minimum-{dark}", "DNS actions in the minimum SSH editor", dialog);
            var stop = dialog.FindControl<Button>("btnStopOperation")!;
            var formScroll = dns.GetVisualAncestors().OfType<ScrollViewer>().First();
            var actionAt = deploy.TranslatePoint(default, dialog)!.Value;
            var footerAt = stop.TranslatePoint(default, dialog)!.Value;
            Check(formScroll.Bounds.Height > 80 && actionAt.Y >= 0 && actionAt.Y + deploy.Bounds.Height < footerAt.Y,
                "DNS: minimum editor keeps deployment actions above the footer");
        }
        dialog.Close();
    }
}
