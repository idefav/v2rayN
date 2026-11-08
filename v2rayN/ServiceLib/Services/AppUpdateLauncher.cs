namespace ServiceLib.Services;

internal static class AppUpdateLauncher
{
    internal static Task PrepareAsync(UpdateResult update, string archive, string helper)
        => PrepareAsync(update, archive, helper, Utils.GetExePath(), Utils.GetConfigPath(), Utils.GetLogPath(), CancellationToken.None);

    internal static async Task PrepareAsync(UpdateResult update, string archive, string helper, string executable,
        string configDirectory, string logDirectory, CancellationToken token)
    {
        var operation = Directory.CreateTempSubdirectory("v2rayn-upgrade-");
        var worker = Path.Combine(operation.FullName, Path.GetFileName(helper));
        var log = Path.Combine(logDirectory, operation.Name + ".log");
        using var process = new Process();
        AppUpgradeRequest? request = null;
        var started = false;
        try
        {
            var record = AppUpdateProvenance.Stage(update, archive, configDirectory, executable);
            File.Copy(helper, worker);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(worker, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            using var parent = Process.GetCurrentProcess();
            request = new AppUpgradeRequest(archive, executable, parent.Id, AppUpgradeRequest.StartIdentity(parent),
                operation.FullName, log, Path.Combine(configDirectory, "pending-app-update.json"), record.Sha256);
            process.StartInfo = new(worker) { WorkingDirectory = operation.FullName, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in request.Arguments()) process.StartInfo.ArgumentList.Add(argument);
            started = process.Start();
            if (!started) throw new IOException("Could not start the application updater.");

            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
            preparation.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                while (true)
                {
                    preparation.Token.ThrowIfCancellationRequested();
                    if (File.Exists(request.ErrorPath)) throw new IOException(File.ReadAllText(request.ErrorPath));
                    if (process.HasExited) throw new IOException($"Application updater exited ({process.ExitCode}).");
                    if (File.Exists(request.ReadyPath)) break;
                    await Task.Delay(50, preparation.Token);
                }
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Application update preparation timed out.", ex);
            }

            // A prepared helper cannot commit merely because its parent disappeared.
            // The application authorizes this particular handoff after reading ready.
            Logging.SaveLog($"[App update] Prepared {update.Tag}. Updater log: {log}");
            File.WriteAllText(request.CommitPath + ".tmp", "");
            File.Move(request.CommitPath + ".tmp", request.CommitPath);
        }
        catch (Exception ex)
        {
            // The helper has not received a commit signal, even if cleanup fails.
            try
            {
                if (started && !process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                if (request != null && Directory.Exists(request.Workspace)) Directory.Delete(request.Workspace, true);
                Directory.Delete(operation.FullName, true);
            }
            catch (Exception cleanup) { Logging.SaveLog("[App update cleanup]", cleanup); }
            try { File.Delete(Path.Combine(configDirectory, "pending-app-update.json")); }
            catch (Exception cleanup) { Logging.SaveLog("[App update pending record]", cleanup); }
            throw new IOException($"应用更新准备失败 / Application update preparation failed. {ex.Message}\n{log}", ex);
        }
    }
}
