namespace ServiceLib.Manager;

public class CoreAdminManager
{
    private static readonly Lazy<CoreAdminManager> _instance = new(() => new());
    public static CoreAdminManager Instance => _instance.Value;
    private Func<bool, string, Task>? _updateFunc;

    public Task Init(Config config, Func<bool, string, Task>? updateFunc)
    {
        _updateFunc = updateFunc;
        return Task.CompletedTask;
    }

    public async Task<ProcessService> RunProcessAsLinuxSudo(string fileName, CoreInfo coreInfo, string configPath,
        Action<ProcessService>? onCreated = null)
    {
        var fullConfig = Utils.GetBinConfigPath(configPath);
        var process = new ProcessService("/usr/bin/sudo", "", Utils.GetBinConfigPath(), true, true, null, _updateFunc)
        {
            IsElevated = true, ExpectedExecutable = fileName, ConfigPath = fullConfig,
        };
        process.IdentityMarker = "__V2RAYN_CORE_" + process.LaunchId;
        var command = UnixProcessControl.Quote(fileName) + " " +
            string.Format(coreInfo.Arguments, UnixProcessControl.Quote(fullConfig));
        var environment = string.Join(" ", coreInfo.Environment.Where(kv => kv.Value.IsNotEmpty())
            .Select(kv => kv.Key + "=" + UnixProcessControl.Quote(string.Format(kv.Value, fullConfig))));
        var script = "#!/bin/bash\nexport LC_ALL=C\n" +
            "birth=$(ps -p $$ -o lstart=) || exit 23\n" +
            $"printf '{process.IdentityMarker}\\t%s\\t%s\\n' \"$$\" \"$birth\"\n" +
            $"exec env {environment} {command}\n";
        process.LaunchScriptPath = await FileUtils.CreateLinuxShellFile("run_as_sudo-" + process.LaunchId + ".sh", script, true);
        process.SetArguments(["-S", "-p", "", "--", "/bin/bash", process.LaunchScriptPath]);
        // Retain the owner before starting: even a startup exception may leave a child.
        onCreated?.Invoke(process);
        await process.StartAsync(AppManager.Instance.LinuxSudoPwd);
        return process;
    }

    public Task<ProcessStopResult> KillProcessAsLinuxSudo(ProcessService process) => process.StopAndWaitAsync();
}
