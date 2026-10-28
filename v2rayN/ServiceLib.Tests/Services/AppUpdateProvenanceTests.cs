using System.IO.Compression;

namespace ServiceLib.Tests.Services;

public class AppUpdateProvenanceTests
{
    [Test]
    public async Task DownloadDoesNotConfirmAppUpdateUntilExpectedExecutableRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "v2rayn-app-update-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "v2rayN");
            await File.WriteAllTextAsync(executable, "old application");
            var archive = Path.Combine(root, "app.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("v2rayN-platform/v2rayN").Open());
                writer.Write("new application");
            }
            var update = new UpdateResult(true, new SemanticVersion("7.25.6-preview.1"))
                { Repository = "custom/v2rayN", Tag = "v7.25.6-preview.1" };
            AppUpdateProvenance.Stage(update, archive, root, executable);
            AppUpdateProvenance.ConfirmStartup(root, executable);
            var confirmed = Path.Combine(root, "installed-app-update.json");
            await File.Exists(confirmed).Should().BeFalse();
            using (var zip = ZipFile.OpenRead(archive)) zip.Entries[0].ExtractToFile(executable, true);
            AppUpdateProvenance.ConfirmStartup(root, executable);
            var record = JsonSerializer.Deserialize<InstalledUpdate>(await File.ReadAllTextAsync(confirmed))!;
            await record.Repository.Should().BeEqualTo("custom/v2rayN");
            await record.Tag.Should().BeEqualTo("v7.25.6-preview.1");
            await File.Exists(Path.Combine(root, "pending-app-update.json")).Should().BeFalse();
        }
        finally { Directory.Delete(root, true); }
    }
}
