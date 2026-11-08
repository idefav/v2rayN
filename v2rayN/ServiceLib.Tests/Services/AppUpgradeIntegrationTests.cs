using System.IO.Compression;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class AppUpgradeIntegrationTests
{
    [Test]
    public async Task RealApplicationHandoffOnDisposableMacRunner()
    {
        var install = Environment.GetEnvironmentVariable("V2RAYN_TEST_REAL_UPGRADE_DIR");
        if (string.IsNullOrEmpty(install)) { Skip.Test("CI-only real application handoff"); return; }
        Check(OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
            "Real GUI upgrade fixture is only allowed on macOS CI");
        var archive = Environment.GetEnvironmentVariable("V2RAYN_TEST_REAL_UPGRADE_ZIP")!;
        var tag = Environment.GetEnvironmentVariable("V2RAYN_TEST_REAL_UPGRADE_TAG") ?? "v7.25.6-ssh-preview.999";
        Check(UpdateSources.TryVersion(tag, out var version), "Invalid fixture release tag");
        await AppUpdateLauncher.PrepareAsync(new UpdateResult(true, version)
            { Repository = "idefav/v2rayN", Tag = tag },
            archive, Path.Combine(install, "AmazToolV2"), Path.Combine(install, "v2rayN"),
            Path.Combine(install, "guiConfigs"), install, CancellationToken.None);
        // Returning lets this exact test host exit. The real helper then owns the
        // transaction; the outer fixture requires the new GUI's hash confirmation.
    }

    [Test]
    public async Task PreparationFailureIsReportedBeforeApplicationExit()
    {
        var helper = Environment.GetEnvironmentVariable("V2RAYN_TEST_BIND_HELPER");
        if (string.IsNullOrEmpty(helper)) { Skip.Test("Requires packaged updater"); return; }
        var root = Directory.CreateTempSubdirectory("upgrade-preflight-").FullName;
        try
        {
            var executable = Path.Combine(root, OperatingSystem.IsWindows() ? "v2rayN.exe" : "v2rayN");
            File.WriteAllText(executable, "existing");
            var archivePath = Path.Combine(root, "invalid.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("package/" + Path.GetFileName(executable)).Open()))
                    writer.Write("candidate");
                archive.CreateEntry("package/../escape");
            }
            var reported = false;
            try
            {
                UpdateSources.TryVersion("v7.25.6-ssh-preview.999", out var version);
                await AppUpdateLauncher.PrepareAsync(new UpdateResult(true, version) { Repository = "idefav/v2rayN",
                    Tag = "v7.25.6-ssh-preview.999" },
                    archivePath, helper, executable, root, root, CancellationToken.None);
            }
            catch (IOException ex) { reported = ex.Message.Contains("preparation failed"); }
            Check(reported && File.ReadAllText(executable) == "existing", "preflight failure did not preserve application");
            Check(!File.Exists(Path.Combine(root, "pending-app-update.json")), "failed handoff left pending record");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task PackagedRebootCommandStillStartsApplication()
    {
        var helper = Environment.GetEnvironmentVariable("V2RAYN_TEST_BIND_HELPER");
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(helper))
        { Skip.Test("Requires packaged Unix updater"); return; }
        var root = Directory.CreateTempSubdirectory("reboot space-").FullName;
        try
        {
            var executable = Path.Combine(root, "v2rayN");
            File.WriteAllText(executable, "#!/bin/sh\nprintf ok > started\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var worker = Path.Combine(root, "AmazToolV2");
            File.Copy(helper, worker);
            File.SetUnixFileMode(worker, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            using var child = Process.Start(new ProcessStartInfo(worker, "rebootas") { UseShellExecute = false })!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync(deadline.Token);
            while (!File.Exists(Path.Combine(root, "started"))) await Task.Delay(20, deadline.Token);
            Check(child.ExitCode == 0, "reboot failed");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    [Arguments("success")]
    [Arguments("rollback")]
    [Arguments("unsafe-zip")]
    [Arguments("parent-timeout")]
    [Arguments("launch-failure")]
    [Arguments("identity-mismatch")]
    public async Task PackagedHelperUsesStagingAndRestoresFailedReplacement(string scenario)
    {
        var helper = Environment.GetEnvironmentVariable("V2RAYN_TEST_BIND_HELPER");
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(helper))
        { Skip.Test("Requires published Unix updater helper"); return; }
        var root = Directory.CreateTempSubdirectory("升级 space-").FullName;
        var install = Path.Combine(root, "程序 app");
        var operation = Path.Combine(root, "v2rayn-upgrade-test");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(operation);
        var executable = Path.Combine(install, "v2rayN");
        var marker = Path.Combine(install, "started");
        var oldApp = "#!/bin/sh\nprintf old > started\n";
        var newApp = scenario == "launch-failure" ? "invalid executable" : "#!/bin/sh\nprintf new > started\n";
        File.WriteAllText(executable, oldApp);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(install, "AmazToolV2"), "old-helper");
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        File.WriteAllText(Path.Combine(install, "bin", "core"), "existing-core");
        Directory.CreateDirectory(Path.Combine(install, "guiConfigs"));
        File.WriteAllText(Path.Combine(install, "guiConfigs", "settings"), "existing-settings");
        var zip = Path.Combine(root, "更新.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in new[] { ("v2rayN", newApp), ("AmazToolV2", "new-helper"),
                ("bin/core", "new-core"), ("guiConfigs/settings", "new-settings"), ("last-file", "last") })
            {
                using var writer = new StreamWriter(archive.CreateEntry("package/" + name).Open());
                writer.Write(content);
            }
            if (scenario == "unsafe-zip") archive.CreateEntry("package/../outside");
        }
        var worker = Path.Combine(operation, "AmazToolV2");
        File.Copy(helper, worker);
        File.SetUnixFileMode(worker, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var digestBefore = SHA256.HashData(File.ReadAllBytes(worker));
        using var parent = Process.Start(new ProcessStartInfo("/bin/sleep", "90") { UseShellExecute = false })!;
        var request = new AppUpgradeRequest(zip, executable, parent.Id,
            AppUpgradeRequest.StartIdentity(parent) + (scenario == "identity-mismatch" ? 1 : 0),
            operation, Path.Combine(root, "upgrade.log"), Path.Combine(install, "guiConfigs", "pending-app-update.json"),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(newApp))));
        var start = new ProcessStartInfo(worker) { UseShellExecute = false };
        foreach (var arg in request.Arguments()) start.ArgumentList.Add(arg);
        using var updater = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(80));
        try
        {
            while (!File.Exists(request.ReadyPath) && !updater.HasExited)
                await Task.Delay(50, deadline.Token);
            if (scenario is not ("unsafe-zip" or "identity-mismatch"))
            {
                Check(File.Exists(request.ReadyPath), "preparation failed: " +
                    (File.Exists(request.ErrorPath) ? File.ReadAllText(request.ErrorPath) : "no error report"));
                Check(File.ReadAllText(executable) == oldApp, "installed before parent exit");
                if (scenario == "rollback") Directory.CreateDirectory(Path.Combine(install, "last-file"));
                File.WriteAllText(request.CommitPath, "");
                if (scenario != "parent-timeout") { parent.Kill(); await parent.WaitForExitAsync(deadline.Token); }
            }
            await updater.WaitForExitAsync(deadline.Token);
            Check(digestBefore.SequenceEqual(SHA256.HashData(File.ReadAllBytes(worker))), "running helper overwritten");
            Check(File.ReadAllText(Path.Combine(install, "bin", "core")) == "existing-core", "existing core replaced");
            Check(File.ReadAllText(Path.Combine(install, "guiConfigs", "settings")) == "existing-settings", "settings replaced");
            if (scenario is "success" or "rollback" or "launch-failure")
            {
                while (!File.Exists(marker)) await Task.Delay(50, deadline.Token);
                Check(File.ReadAllText(marker) == (scenario == "success" ? "new" : "old"), "wrong program restarted");
            }
            Check(updater.ExitCode == (scenario == "success" ? 0 : 1), "wrong update result");
            Check(File.ReadAllText(executable) == (scenario == "success" ? newApp : oldApp), "rollback did not restore application");
            Check(File.ReadAllText(Path.Combine(install, "AmazToolV2")) == (scenario == "success" ? "new-helper" : "old-helper"), "helper replacement not restored");
            if (scenario != "success") Check(File.Exists(request.ErrorPath), "failure was not persisted");
        }
        finally
        {
            if (!updater.HasExited) { updater.Kill(); await updater.WaitForExitAsync(); }
            if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); }
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
