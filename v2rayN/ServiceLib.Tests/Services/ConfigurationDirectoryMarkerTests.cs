namespace ServiceLib.Tests.Services;

[NotInParallel]
public class ConfigurationDirectoryMarkerTests
{
    [Test]
    public async Task InstalledMarkerSelectsExistingUserDirectoryWithoutOpeningIt()
    {
        // Only write inside the disposable test executable directory.
        var marker = Utils.GetBaseDirectory("NotStoreConfigHere.txt");
        var previous = File.Exists(marker) ? await File.ReadAllBytesAsync(marker) : null;
        var setting = Environment.GetEnvironmentVariable(Global.LocalAppData);
        try
        {
            await File.WriteAllTextAsync(marker, "Do not store configuration in the app bundle.");
            if (Utils.HasWritePermission()) throw new Exception("Installed package marker was ignored");
            Environment.SetEnvironmentVariable(Global.LocalAppData, "1");
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "v2rayN");
            if (Utils.StartupPath() != expected) throw new Exception("Existing user directory was not selected");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Global.LocalAppData, setting);
            if (previous == null) File.Delete(marker); else await File.WriteAllBytesAsync(marker, previous);
        }
    }
}
