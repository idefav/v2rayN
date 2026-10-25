using System.Security.Cryptography;

namespace ServiceLib.Tests.Services;

public class BundledSshCoreTests
{
    [Test]
    public async Task ValidPinnedBundle_ShouldResolveDespiteAnExistingStockCore()
    {
        using var fixture = new Fixture();
        var legacy = Path.Combine(fixture.Root, "user-bin");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, Utils.GetExeName("sing-box")), "stock core retained from a previous install");
        await BundledSshCore.TryResolve(ECoreType.sing_box, fixture.Root).Should().BeEqualTo(fixture.Core);
        await (await File.ReadAllTextAsync(Path.Combine(legacy, Utils.GetExeName("sing-box")))).Should().BeEqualTo("stock core retained from a previous install");
    }

    [Test]
    [Arguments("missing")]
    [Arguments("invalid-json")]
    [Arguments("wrong-digest")]
    [Arguments("wrong-version")]
    [Arguments("wrong-schema")]
    [Arguments("modified-core")]
    public async Task InvalidOrUnmarkedBundle_ShouldKeepNormalCoreSelection(string fault)
    {
        using var fixture = new Fixture();
        switch (fault)
        {
            case "missing": File.Delete(fixture.Manifest); break;
            case "invalid-json": File.WriteAllText(fixture.Manifest, "{"); break;
            case "wrong-digest": fixture.WriteManifest(sha256: new string('0', 64)); break;
            case "wrong-version": fixture.WriteManifest(version: "1.14.3"); break;
            case "wrong-schema": fixture.WriteManifest(schema: 2); break;
            case "modified-core": File.AppendAllText(fixture.Core, "updated"); break;
        }
        await BundledSshCore.TryResolve(ECoreType.sing_box, fixture.Root).Should().BeNull();
    }

    [Test]
    public async Task OtherCoreTypes_ShouldNotUseSshFeatureCore()
    {
        using var fixture = new Fixture();
        await BundledSshCore.TryResolve(ECoreType.Xray, fixture.Root).Should().BeNull();
        await BundledSshCore.TryResolve(null, fixture.Root).Should().BeNull();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ssh-core-selection-" + Guid.NewGuid());
        public string Core => Path.Combine(Root, "sing_box", Utils.GetExeName("sing-box"));
        public string Manifest => Path.Combine(Root, "sing_box", BundledSshCore.ManifestName);
        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Core)!);
            File.WriteAllText(Core, "test feature binary");
            WriteManifest();
        }
        public void WriteManifest(string version = "1.14.3-ssh-udp.1", int schema = 1, string? sha256 = null)
            => File.WriteAllText(Manifest, JsonSerializer.Serialize(new
            {
                SchemaVersion = schema, Version = version,
                Sha256 = sha256 ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Core)))
            }));
        public void Dispose() => Directory.Delete(Root, true);
    }
}
