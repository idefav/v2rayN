using System.IO.Compression;

namespace ServiceLib.Tests.Services;

public class InstalledUpdateTests
{
    [Test]
    public async Task ActiveUpdateSurvivesRestartButModifiedBinaryIsRejected()
    {
        using var fixture = new Fixture();
        var record = fixture.Create("first", "1.2.3");
        fixture.Store.Write(record);
        await new InstalledUpdateStore(fixture.Root).Read()!.Repository.Should().BeEqualTo("custom/core");
        File.AppendAllText(fixture.Store.Resolve(record.Executable), "modified");
        await fixture.Store.Read().Should().BeNull();
    }

    [Test]
    public async Task FailedLaunchRestoresPreviousRecordAndRestartsIt()
    {
        using var fixture = new Fixture();
        fixture.Store.Write(fixture.Create("previous", "1.2.3"));
        var candidate = fixture.Create("next", "1.2.4");
        var calls = new List<string>();
        var failed = false;
        try
        {
            await fixture.Store.ActivateAsync(candidate, () =>
            {
                calls.Add(fixture.Store.Read()!.Version);
                // The candidate must not be persisted before it successfully starts.
                var persisted = JsonSerializer.Deserialize<InstalledUpdate>(File.ReadAllText(Path.Combine(fixture.Root, "active.json")))!;
                if (persisted.Version != "1.2.3") throw new Exception("Unconfirmed candidate was persisted");
                return Task.FromResult(calls.Count > 1);
            });
        }
        catch (InvalidOperationException) { failed = true; }
        await failed.Should().BeTrue();
        await string.Join(",", calls).Should().BeEqualTo("1.2.4,1.2.3");
        await fixture.Store.Read()!.Version.Should().BeEqualTo("1.2.3");
    }

    [Test]
    public async Task FirstUpdateFailureRestoresBundledSelection()
    {
        using var fixture = new Fixture();
        var calls = 0;
        try { await fixture.Store.ActivateAsync(fixture.Create("next", "1.2.4"), () => Task.FromResult(++calls > 1)); }
        catch (InvalidOperationException) { }
        await fixture.Store.Read().Should().BeNull();
        await calls.Should().BeEqualTo(2);
    }

    [Test]
    public async Task ArchiveKeepsLibrariesAndSubdirectories()
    {
        using var fixture = new Fixture();
        var archive = Path.Combine(fixture.Root, "core.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "package/sing-box", "package/libcronet.so", "package/data/license" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }
        }
        var destination = Path.Combine(fixture.Root, "extracted");
        CoreUpdateInstaller.Extract(archive, destination, "unused");
        await File.Exists(Path.Combine(destination, "package/libcronet.so")).Should().BeTrue();
        await File.Exists(Path.Combine(destination, "package/data/license")).Should().BeTrue();
    }

    [Test]
    [Arguments(".tar.gz")]
    [Arguments(".gz")]
    public async Task CompressedCoreFormatsExtractSuccessfully(string extension)
    {
        using var fixture = new Fixture();
        var archive = Path.Combine(fixture.Root, "core" + extension);
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
        {
            if (extension == ".tar.gz")
            {
                using var writer = new System.Formats.Tar.TarWriter(gzip, leaveOpen: true);
                using var data = new MemoryStream(Encoding.UTF8.GetBytes("core"));
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "package/sing-box") { DataStream = data });
            }
            else gzip.Write(Encoding.UTF8.GetBytes("core"));
        }
        var destination = Path.Combine(fixture.Root, "extracted");
        CoreUpdateInstaller.Extract(archive, destination, "mihomo");
        var executable = Path.Combine(destination, extension == ".gz" ? "mihomo" : "package/sing-box");
        await File.ReadAllText(executable).Should().BeEqualTo("core");
    }

    [Test]
    public async Task ArchiveTraversalIsRejected()
    {
        using var fixture = new Fixture();
        var archive = Path.Combine(fixture.Root, "core.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../escape").Open());
            writer.Write("bad");
        }
        var rejected = false;
        try { CoreUpdateInstaller.Extract(archive, Path.Combine(fixture.Root, "extracted"), "unused"); }
        catch (InvalidDataException) { rejected = true; }
        await rejected.Should().BeTrue();
        await File.Exists(Path.Combine(fixture.Root, "escape")).Should().BeFalse();
    }

    [Test]
    [Arguments("{bad json")]
    [Arguments("{\"SchemaVersion\":1,\"Sha256\":null}")]
    [Arguments("{\"SchemaVersion\":2}")]
    public async Task CorruptRecordDoesNotPreventFallback(string json)
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "active.json"), json);
        await fixture.Store.Read().Should().BeNull();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "v2rayn-update-test-" + Guid.NewGuid());
        public InstalledUpdateStore Store { get; }
        public Fixture() { Directory.CreateDirectory(Root); Store = new(Root); }
        public InstalledUpdate Create(string name, string version)
        {
            File.WriteAllText(Path.Combine(Root, name), name);
            return new() { Repository = "custom/core", Tag = "v" + version, Version = version, Executable = name, Sha256 = InstalledUpdateStore.Digest(Path.Combine(Root, name)) };
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
