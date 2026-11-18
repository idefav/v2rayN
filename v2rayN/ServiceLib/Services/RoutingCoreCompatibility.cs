using System.Globalization;

namespace ServiceLib.Services;

public static class RoutingCoreCompatibility
{
    public static string T(string zh, string en)
        => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;

    internal static async Task<string?> CheckExecutableAsync(string executable, ECoreType core, string path,
        IDictionary<string, string?> environment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = new ProcessService(executable, "", Utils.GetBinConfigPath(), true, false,
            environment.ToDictionary(p => p.Key, p => p.Value == null ? null! : string.Format(p.Value, path)), null) { ConfigPath = path };
        process.SetArguments(core == ECoreType.sing_box
            ? new[] { "check", "-c", path, "--disable-color" } : new[] { "run", "-test", "-config", path });
        try
        {
            try
            {
                await process.StartAsync(token: timeout.Token);
                await process.WaitForWrapperExitAsync(timeout.Token);
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var stopped = await process.StopAndWaitAsync(cleanup.Token);
                if (!stopped.Success) throw new IOException("配置检查进程退出未确认 / Configuration checker exit unconfirmed: " + stopped.Error);
            }
            return process.ExitCode == 0 ? null
                : T("核心配置检查失败：", "Core configuration check failed: ") + process.RecentError;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return T("核心配置检查超时。", "Core configuration check timed out."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return T("核心配置检查失败：", "Core configuration check failed: ") + ex.Message; }
    }
}
