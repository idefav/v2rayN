using System.IO.Compression;
using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Tests.Services;

public class CoreUpdateInstallerIntegrationTests
{
    // Opt-in: a copy of a real core is installed in a disposable store. No production config or proxy is touched.
    [Test]
    public async Task RealSingboxInstallLaunchAndRollback()
    {
        var binary = Environment.GetEnvironmentVariable("V2RAYN_UPDATE_TEST_CORE");
        if (string.IsNullOrEmpty(binary)) return;
        var root = Path.Combine(Path.GetTempPath(), "v2rayn-live-update-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var versionOutput = await Cli.Wrap(binary).WithArguments("version").ExecuteBufferedAsync(timeout.Token);
            var version = UpdateService.ParseCoreVersion(versionOutput.StandardOutput)!;
            var archive = Path.Combine(root, "core.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(binary, "package/sing-box", CompressionLevel.NoCompression);
            var config = Path.Combine(root, "config.json");
            var invalid = Path.Combine(root, "invalid.json");
            await File.WriteAllTextAsync(config, """{"log":{"disabled":true},"outbounds":[{"type":"direct"}]}""");
            await File.WriteAllTextAsync(invalid, """{"outbounds":[{"type":"unknown-core-type"}]}""");
            var store = new InstalledUpdateStore(Path.Combine(root, "installed"));
            var update = new UpdateResult(true, version) { Repository = "idefav/sing-box", Tag = "v" + version };
            var installer = new CoreUpdateInstaller();
            async Task<bool> Validate(string exe, CoreInfo _, CancellationToken token)
            {
                await Cli.Wrap(exe).WithArguments(["check", "-c", config]).ExecuteBufferedAsync(token);
                return true;
            }
            async Task<bool> Launch(string path)
            {
                var info = new ProcessStartInfo(store.Resolve(store.Read()!.Executable)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add("run"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(path);
                using var process = Process.Start(info)!;
                await Task.Delay(400, timeout.Token);
                var alive = !process.HasExited;
                if (alive) { process.Kill(true); await process.WaitForExitAsync(timeout.Token); }
                return alive;
            }
            await installer.InstallAsync(ECoreType.sing_box, update, archive, store, Validate, _ => Launch(config), timeout.Token);
            var previous = store.Read()!.Executable;
            var calls = 0;
            var rolledBack = false;
            try
            {
                await installer.InstallAsync(ECoreType.sing_box, update, archive, store, Validate,
                    _ => Launch(++calls == 1 ? invalid : config), timeout.Token);
            }
            catch (InvalidOperationException) { rolledBack = true; }
            await rolledBack.Should().BeTrue();
            await calls.Should().BeEqualTo(2);
            await store.Read()!.Executable.Should().BeEqualTo(previous);
            await new InstalledUpdateStore(store.Root).Read()!.Repository.Should().BeEqualTo("idefav/sing-box");
        }
        finally { Directory.Delete(root, true); }
    }
}
