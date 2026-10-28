using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Services;

public static class TunSettingsPreflight
{
    public static async Task CheckAsync(CoreConfigContext context, CancellationToken token = default)
    {
        var info = CoreInfoManager.Instance.GetCoreInfo(context.RunCoreType)
            ?? throw new TunConfigurationException(TunSettingsText.T("找不到 TUN 内核信息。", "TUN core metadata is unavailable."));
        var executable = CoreInfoManager.Instance.GetCoreExecFile(info, out var message);
        if (string.IsNullOrEmpty(executable)) throw new TunConfigurationException(message);
        var path = Utils.GetBinConfigPath($"tun-check-{Guid.NewGuid():N}.json");
        try
        {
            var generated = await CoreConfigHandler.GenerateClientConfig(context, path);
            if (!generated.Success) throw new TunConfigurationException(generated.Msg);
            string[] arguments = context.RunCoreType switch
            {
                ECoreType.sing_box => ["check", "-c", path, "--disable-color"],
                ECoreType.Xray => ["run", "-test", "-c", path],
                ECoreType.mihomo => ["-t", "-f", path, "-d", Utils.GetBinPath("")],
                _ => throw new TunConfigurationException(TunSettingsText.T("当前内核不支持 TUN 预检。", "The current core does not support TUN preflight.")),
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var environment = info.Environment.ToDictionary(kv => kv.Key, kv => (string?)string.Format(kv.Value, path));
            var result = await Cli.Wrap(executable).WithArguments(arguments)
                .WithWorkingDirectory(Utils.GetBinConfigPath())
                .WithEnvironmentVariables(environment).WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeout.Token);
            if (result.ExitCode != 0)
                throw new TunConfigurationException(TunSettingsText.T("内核配置预检失败：", "Core preflight failed:") + "\n"
                    + (result.StandardError + "\n" + result.StandardOutput).Trim());
        }
        finally { File.Delete(path); }
    }
}
