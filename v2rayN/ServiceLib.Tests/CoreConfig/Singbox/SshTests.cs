using ServiceLib.ViewModels;
using ReactiveUI.Builder;
using ReactiveUI.Primitives.Signals;

namespace ServiceLib.Tests.CoreConfig.Singbox;

[NotInParallel]
public class SshTests
{
    static SshTests()
    {
        ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
    }
    public const string TestKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public static ProfileItem Node()
    {
        var node = new ProfileItem
        {
            IndexId = "ssh-test", ConfigType = EConfigType.SSH, Address = "127.0.0.1",
            Port = 2222, Username = "test", Password = "test-password", Remarks = "SSH",
        };
        node.SetProtocolExtra(new()
        {
            SshHostKey = TestKey, SshHostKeyAlgorithm = "ssh-ed25519",
            SshTrustedAddress = node.Address, SshTrustedPort = node.Port,
        });
        return node;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    [Test]
    public void PublicKeyParsingChecksTypeAndWireData()
    {
        Check(SshProfileService.ParseHostKey(TestKey).Fingerprint.StartsWith("SHA256:"), "fingerprint");
        foreach (var invalid in new[] { "", "ssh-rsa " + TestKey.Split(' ')[1], TestKey + "AAAA", "ssh-ed25519 AA==" })
        {
            try { SshProfileService.ParseHostKey(invalid); }
            catch (FormatException) { continue; }
            throw new Exception("Accepted invalid key");
        }
    }

    [Test]
    public void TrustIsBoundToEndpointNotUsername()
    {
        var node = Node();
        Check(node.IsValid(), "initial trust");
        node.Username = "other";
        Check(node.IsValid(), "username change must retain trust");
        node.Port++;
        Check(!node.IsValid(), "port change must invalidate trust");
        node.Port--;
        node.Address = "localhost";
        Check(!node.IsValid(), "address change must invalidate trust");
    }

    [Test]
    public void SshForcesSingboxEvenWithExplicitXray()
    {
        var node = Node();
        node.CoreType = ECoreType.Xray;
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        Check(AppManager.Instance.GetCoreType(node, node.ConfigType) == ECoreType.sing_box, "core selection");
        Check(!NodeValidator.Validate(node, ECoreType.Xray).Success, "Xray groups must reject SSH");
        Check(NodeValidator.Validate(node, ECoreType.sing_box).Success, "sing-box support");
        Check(FmtHandler.GetShareUri(node) == null, "must not export credentials via an invented URI");
    }

    [Test]
    public void PasswordConfigUsesGlobalInboundAndTcpDns()
    {
        var node = Node();
        // Even stale fields must not leak TLS or mux options into SSH.
        node.StreamSecurity = Global.StreamSecurity;
        node.MuxEnabled = true;
        var config = CoreConfigTestFactory.CreateConfig();
        config.SimpleDNSItem.RemoteDNS = "8.8.8.8";
        config.Inbound[0].LocalPort = 12345;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var result = new CoreConfigSingboxService(CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box))
            .GenerateClientConfigContent();
        Check(result.Success, result.Msg ?? "generation failed");
        var json = JsonNode.Parse(result.Data!.ToString()!)!;
        var outbound = json["outbounds"]!.AsArray().Single(o => o?["type"]?.ToString() == "ssh")!;
        Check(outbound["user"]!.ToString() == "test" && outbound["username"] == null, "SSH user field");
        Check(outbound["password"]!.ToString() == node.Password, "password");
        Check(outbound["tls"] == null && outbound["multiplex"] == null && outbound["transport"] == null, "SSH-only options");
        Check(json["inbounds"]!.AsArray().Any(i => i?["listen_port"]?.ToString() == "12345"), "global port");
        Check(json["dns"]!["servers"]!.AsArray().Any(s => s?["detour"]?.ToString() == Global.ProxyTag && s["type"]?.ToString() == "tcp"), "TCP DNS");
    }

    [Test]
    public async Task PrivateKeyConfigAndPersistence()
    {
        var path = Path.GetTempFileName();
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            var node = Node();
            node.Password = "";
            node.SetProtocolExtra(node.GetProtocolExtra() with
            {
                SshPrivateKeyAuth = true, SshPrivateKeyPath = path, SshPrivateKeyPassphrase = "key-passphrase"
            });
            var config = CoreConfigTestFactory.CreateConfig();
            CoreConfigTestFactory.BindAppManagerConfig(config);
            var result = new CoreConfigSingboxService(CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box))
                .GenerateClientSpeedtestConfig(12346);
            Check(result.Success, result.Msg ?? "generation");
            var json = JsonNode.Parse(result.Data!.ToString()!)!;
            var ssh = json["outbounds"]!.AsArray().Single(o => o?["type"]?.ToString() == "ssh")!;
            Check(ssh["password"] == null && ssh["private_key_path"]!.ToString() == path, "private key auth");
            Check(ssh["private_key_passphrase"]!.ToString() == "key-passphrase", "passphrase");
            using (var db = new SQLiteConnection(dbPath))
            {
                db.CreateTable<ProfileItem>();
                db.Insert(node);
            }
            using (var db = new SQLiteConnection(dbPath))
            {
                var restored = db.Find<ProfileItem>(node.IndexId);
                Check(restored.GetProtocolExtra().SshPrivateKeyPassphrase == "key-passphrase", "restart persistence");
                Check(JsonUtils.DeepCopy(restored).IsValid(), "clone");
            }
            node.SetProtocolExtra(node.GetProtocolExtra() with { SshPrivateKeyPath = path + ".missing" });
            Check(SshProfileService.Validate(node) != null, "unreadable file");
            await Task.CompletedTask;
        }
        finally { File.Delete(path); File.Delete(dbPath); }
    }

    [Test]
    public void DnsPolicyFollowsSelectorsAndChainsAndFailsClosed()
    {
        const string content = """
            {"outbounds":[{"tag":"proxy","type":"selector","outbounds":["chain"]},
             {"tag":"chain","type":"socks","detour":"ssh"},{"tag":"ssh","type":"ssh"},
             {"tag":"direct","type":"direct"}],"route":{"final":"proxy"},
             "dns":{"servers":[{"tag":"remote","type":"udp","server":"1.1.1.1","detour":"proxy"},
             {"tag":"local","type":"udp","server":"8.8.8.8","detour":"direct"}]}}
            """;
        var result = JsonNode.Parse(SshDnsPolicy.Apply(content, false))!;
        Check(result["dns"]!["servers"]![0]!["type"]!.ToString() == "tcp", "convert proxy DNS");
        Check(result["dns"]!["servers"]![1]!["type"]!.ToString() == "udp", "preserve direct DNS");
        try { SshDnsPolicy.Apply(content, true); }
        catch (SshConfigurationException) { return; }
        throw new Exception("Unsafe custom DNS accepted");
    }

    [Test]
    public void EditorInvalidatesTrustPermanentlyOnEndpointChange()
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        var vm = new AddSshServerViewModel(Node());
        Check(vm.Status.Contains("SHA256:"), "restored trust");
        vm.Username = "another";
        Check(vm.Status.Contains("SHA256:"), "user change");
        vm.Port = "2223";
        Check(!vm.Status.Contains("SHA256:"), "port change");
        vm.Port = "2222";
        Check(!vm.Status.Contains("SHA256:"), "must confirm again");
        vm.CancelOperation();
    }

    [Test]
    public async Task EditorRequiresExplicitConfirmationAndNeverReplacesOnCancel()
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        var node = Node();
        node.SetProtocolExtra(new());
        var vm = new AddSshServerViewModel(node) { HostKey = TestKey };
        using (vm.ConfirmHostKeyInteraction.RegisterHandler(context => context.SetOutput(false)))
            await vm.TrustCmd.Execute();
        Check(!vm.Status.Contains("SHA256:"), "cancel must leave key untrusted");
        using (vm.ConfirmHostKeyInteraction.RegisterHandler(context => context.SetOutput(true)))
            await vm.TrustCmd.Execute();
        Check(vm.Status.Contains("SHA256:"), "accept must pin key");
        using (vm.ConfirmHostKeyInteraction.RegisterHandler(context => context.SetOutput(false)))
            await vm.TrustCmd.Execute();
        Check(vm.Status.Contains("SHA256:"), "cancel must preserve existing pin");
        vm.HostKey = TestKey + " comment";
        Check(!vm.Status.Contains("SHA256:"), "manual edit needs confirmation");
        vm.CancelOperation();
    }
}
