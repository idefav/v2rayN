using CliWrap;
using CliWrap.Buffered;

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
            (OperatingSystem.IsLinux() ? UnixProcessControl.LinuxStartTimeShell + "\n"
                : "birth=$(ps -p $$ -o lstart=) || exit 23\n") +
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

    internal static async Task<SocketError> CheckListenerAsSudoAsync(CoreListener endpoint, CancellationToken token,
        string? helperPath = null)
    {
        if (helperPath == null && !Utils.UpgradeAppExists(out helperPath))
            throw new IOException("缺少端口检查帮助程序 / Listener probe helper is missing.");
        var result = await Cli.Wrap("/usr/bin/sudo")
            .WithArguments(["-S", "-p", "", "--", helperPath!, ListenerBindProbe.Command,
                endpoint.Protocol == ProtocolType.Tcp ? "tcp" : "udp", endpoint.Address.ToString(), endpoint.Port.ToString()])
            .WithStandardInputPipe(PipeSource.FromString((AppManager.Instance.LinuxSudoPwd ?? "") + "\n"))
            .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync(token);
        if (result.ExitCode != 0)
            throw new IOException($"端口检查提权失败，已中止启动 / Elevated listener probe failed (exit {result.ExitCode}).");
        return ListenerBindProbe.ParseResult(result.StandardOutput);
    }
}
