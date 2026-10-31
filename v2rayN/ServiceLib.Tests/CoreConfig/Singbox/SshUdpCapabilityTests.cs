using ReactiveUI.Primitives;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.CoreConfig.Singbox;

[NotInParallel]
public class SshUdpCapabilityTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Probe(string state) : ISshUdpCapabilityService
    {
        public ProfileItem? Draft;
        public CancellationToken Token;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SshUdpCapabilitySnapshot>? Pending;
        public Task<SshUdpCapabilitySnapshot> DetectAsync(ProfileItem draft, CancellationToken token)
        {
            Draft = draft; Token = token; Started.TrySetResult();
            return Pending?.Task ?? Task.FromResult(new SshUdpCapabilitySnapshot { Custom = state, Reason = "fixture", Gateway = "ready" });
        }
    }

    [Test]
    [Arguments("enabled", true)]
    [Arguments("unsupported", false)]
    [Arguments("disabled", false)]
    [Arguments("upstream_unsupported", false)]
    [Arguments("unknown", true)]
    public async Task CapabilitiesGateNewChoicesWithoutChangingHistoricalModes(string state, bool selectable)
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        foreach (var newNode in new[] { false, true })
        {
            var original = SshTests.Node(); if (newNode) original.IndexId = "";
            original.SetProtocolExtra(original.GetProtocolExtra() with { SshUdpForwarding = true });
            var before = JsonUtils.Serialize(original);
            var probe = new Probe(state);
            var editor = new AddSshServerViewModel(original, capabilityService: probe);
            await editor.DetectUdpCmd.Execute().ToTask();
            Check(editor.UdpModes[0].Enabled == selectable && editor.UdpModes[1].Enabled, "mode availability");
            Check(editor.UdpModeIndex == (newNode && state == "unsupported" ? 1 : 0), "historical mode preserved");
            Check(JsonUtils.Serialize(original) == before, "probe mutated source");
            if (!selectable) { editor.UdpModeIndex = 1; editor.UdpModeIndex = 0; Check(editor.UdpModeIndex == 1, "disabled option selected"); }
            Check(editor.CreateDraftSnapshot().GetProtocolExtra().SshUdpCapability?.Custom == state, "snapshot not persisted in draft");
            editor.Password = "changed";
            Check(editor.CreateDraftSnapshot().GetProtocolExtra().SshUdpCapability == null && editor.UdpModes[0].Enabled, "credentials did not invalidate");
            editor.CancelOperation();
        }
    }

    [Test]
    public async Task LateResponseIsDiscardedAndUnconfirmedHostIsNeverProbed()
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        var probe = new Probe("enabled") { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var editor = new AddSshServerViewModel(SshTests.Node(), capabilityService: probe);
        var running = editor.DetectUdpCmd.Execute().ToTask();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        editor.Username = "different-user";
        Check(probe.Token.IsCancellationRequested, "old request not cancelled");
        probe.Pending.SetResult(new() { Custom = "unsupported" });
        await running;
        Check(editor.CreateDraftSnapshot().GetProtocolExtra().SshUdpCapability == null, "late result applied");
        editor.HostKey = "";
        probe.Draft = null;
        await editor.DetectUdpCmd.Execute().ToTask();
        Check(probe.Draft == null, "unconfirmed identity authenticated");
        editor.CancelOperation();
    }

    private sealed class Deployment : ISshGatewayDeploymentService, ISshGatewayDeployment
    {
        public ProfileItem Node { get; private set; } = null!;
        public SshGatewayPlan Plan => new(new string('a', 64), "arm64", 7301, null, new("/usr/sbin/sshd", "/etc/ssh/sshd_config", "ssh.service", true, true, [], true));
        public bool Installed, Disposed;
        public Task<ISshGatewayDeployment> PrepareAsync(ProfileItem snapshot, string sudoPassword, CancellationToken token) { Node = snapshot; return Task.FromResult<ISshGatewayDeployment>(this); }
        public Task<SshUdpDeploymentRecord> InstallAsync(CancellationToken token) { Installed = true; return Task.FromResult(new SshUdpDeploymentRecord { State = "committed", Port = 7301, InstallationId = "fixture" }); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Test]
    public async Task DeploymentRequiresConcretePreviewAndOnlyUpdatesDraft()
    {
        var config = CoreConfigTestFactory.CreateConfig(); config.IndexId = "keep-running";
        CoreConfigTestFactory.BindAppManagerConfig(config);
        foreach (var approve in new[] { false, true })
        {
            var node = SshTests.Node(); var before = JsonUtils.Serialize(node); var deploy = new Deployment();
            var editor = new AddSshServerViewModel(node, capabilityService: new Probe("enabled"), deploymentService: deploy) { SudoPassword = "never-persist-this" };
            using var confirmation = editor.ConfirmDeploymentInteraction.RegisterHandler(c =>
            {
                Check(c.Input.Contains("127.0.0.1:7301") && c.Input.Contains("ForwardProxyBypass") && c.Input.Contains("SHA256:"), "incomplete preview");
                c.SetOutput(approve);
            });
            await editor.DeployGatewayCmd.Execute().ToTask();
            Check(deploy.Installed == approve && deploy.Disposed, "confirmation or cleanup");
            Check(JsonUtils.Serialize(node) == before && config.IndexId == "keep-running", "running node changed");
            Check(editor.SudoPassword == "" && !JsonUtils.Serialize(editor.CreateDraftSnapshot()).Contains("never-persist-this"), "sudo password persisted");
            if (approve) Check(editor.UdpModeIndex == 1 && editor.UdpGateway == "127.0.0.1:7301" && editor.UdpForwarding, "deployment did not fill draft");
            editor.CancelOperation();
        }
    }
}
