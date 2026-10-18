using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Handler.SysProxy;

internal static class SystemProxyCommand
{
    public static async Task<bool> Run(string file, IEnumerable<string> arguments, Action<string>? onFailure, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await Cli.Wrap(file).WithArguments(arguments).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync(timeout.Token);
            if (result.ExitCode == 0) return true;
            var error = $"{ResUI.menuSystemproxy}: command exited with code {result.ExitCode}";
            Logging.SaveLog(error);
            onFailure?.Invoke(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var error = "System proxy command failed: " + ex.GetType().Name;
            Logging.SaveLog(error);
            onFailure?.Invoke(error);
        }
        return false;
    }
}
