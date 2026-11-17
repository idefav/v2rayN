namespace ServiceLib.ViewModels;

public partial class AddSshServerViewModel
{
    [Reactive] public partial string DnsStatus { get; set; } = "";
    [Reactive] public partial string DnsDetails { get; set; } = "";
    [Reactive] public partial string DnsDeploymentStatus { get; set; } = SshStrings.DnsDeploymentPhase(null);
    public ReactiveCommand<RxVoid, RxVoid> DeployDnsCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> CheckDnsCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> RollbackDnsCmd { get; private set; } = null!;
    public Interaction<string, bool> ConfirmDnsInteraction { get; } = new();
    private readonly SshDnsService _dnsService = new();
    private void InitializeDns()
    {
        var available = this.WhenAnyValue(x => x.IsBusy).Select(busy => !busy).ObserveOn(RxSchedulers.MainThreadScheduler);
        DeployDnsCmd = ReactiveCommand.CreateFromTask(() => RunAsync(() => DnsOperationAsync("deploy")), available);
        CheckDnsCmd = ReactiveCommand.CreateFromTask(() => RunAsync(() => DnsOperationAsync("check")), available);
        RollbackDnsCmd = ReactiveCommand.CreateFromTask(() => RunAsync(() => DnsOperationAsync("rollback")), available);
    }
    private async Task DnsOperationAsync(string action)
    {
        var revision = _testRevision;
        try
        {
            if (_trustedKey == null) await ConfirmAsync(true);
            if (_closed || _trustedKey == null) return;
            var node = CreateDraftSnapshot();
            revision = _testRevision;
            var token = _operation!.Token;
            await Publish(ProxyCheckText.T("正在检查…", "Checking…"));
            if (action == "check")
            {
                var result = await _dnsService.CheckAsync(node, token);
                await Publish(SshStrings.DnsResult(result));
                return;
            }
            if (action == "rollback")
            {
                if (!await ConfirmDnsInteraction.HandleSafe(ProxyCheckText.T(
                    "恢复此 SSH 服务器首次部署前的 DNS；已建立的 SSH 连接保持。服务端即时生效。",
                    "Restore this SSH server's DNS from before the first deployment. Existing SSH connections are retained. Takes effect immediately."))) return;
                token.ThrowIfCancellationRequested();
                if (_closed || revision != _testRevision) return;
                var status = await _dnsService.RollbackAsync(node, SudoPassword, token);
                await Phase("rolled_back");
                await Publish(ProxyCheckText.T("已回退", "Rolled back"), status.ToJsonString());
                var result = await _dnsService.CheckAsync(node, token);
                await Publish(ProxyCheckText.T("已回退", "Rolled back") + "\n" + SshStrings.DnsResult(result), status.ToJsonString());
                return;
            }
            await using var prepared = await _dnsService.PrepareAsync(node, SudoPassword, token);
            if (_closed || revision != _testRevision) return;
            var plan = prepared.Plan;
            await Phase(plan["phase"]?.GetValue<string>());
            var preview = $"{node.Address}:{node.Port}\n{_trustedKey.Fingerprint}\n"
                + $"{plan["architecture"]} · {plan["interface"]} · IPv4: {plan["exit"]} · {plan["phase"]}\n\n"
                + ProxyCheckText.T(
                    "仅部署服务端回环递归 DNS，切换系统解析上游；服务端其他应用也会使用新 DNS。保留原始备份及 10 分钟自动回退保护。已有手工部署将校验后接管，不重新安装。\n不重启 SSH 或网络服务，不修改当前客户端。",
                    "Deploy a loopback recursive resolver and switch server DNS; other server applications will also use it. Keep the original backup and a 10-minute rollback timer. Existing manual installations are validated and adopted without reinstalling.\nSSH/network services and the current client are not restarted or reconfigured.");
            if (!await ConfirmDnsInteraction.HandleSafe(preview)) return;
            if (_closed || revision != _testRevision) return;
            token.ThrowIfCancellationRequested();
            await Publish(ProxyCheckText.T("正在部署并验证…", "Deploying and verifying…"), preview);
            var deployed = await prepared.InstallAsync(token);
            await Phase("committed");
            var outcome = plan["phase"]?.GetValue<string>() == "committed"
                ? ProxyCheckText.T("已部署，检测结果：", "Already deployed; check result:")
                : ProxyCheckText.T("已部署并验证", "Deployment verified");
            await Publish(outcome + "\n" + SshStrings.DnsResult(deployed), preview + "\n\n" + outcome);

            Task Publish(string status, string? details = null) => UiThread.RunAsync(() =>
            { DnsStatus = status; if (details != null) DnsDetails = details; },
                () => !_closed && revision == _testRevision);
            Task Phase(string? phase) => UiThread.RunAsync(() => DnsDeploymentStatus = SshStrings.DnsDeploymentPhase(phase),
                () => !_closed && revision == _testRevision);
        }
        catch (OperationCanceledException)
        { await UiThread.RunAsync(() => DnsStatus = SshStrings.Cancelled, () => !_closed && revision == _testRevision); }
        catch (Exception error)
        {
            var code = error is SshSetupException known ? known.Code : "E_DNS_OPERATION";
            await UiThread.RunAsync(() =>
            {
                DnsStatus = SshStrings.DnsError(code);
                DnsDeploymentStatus = SshStrings.DnsDeploymentPhase(null);
            }, () => !_closed && revision == _testRevision);
        }
        finally { await UiThread.RunAsync(() => SudoPassword = "", () => !_closed); }
    }
}
