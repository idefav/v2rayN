using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Handler.SysProxy;

internal static class SystemProxyCommand
{
    public static async Task Run(string file, IEnumerable<string> arguments, Action<string> onFailure)
    {
        try
        {
            var result = await Cli.Wrap(file).WithArguments(arguments).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
            if (result.ExitCode == 0) return;
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            var error = $"{ResUI.menuSystemproxy}: {result.ExitCode} · {detail.Trim()}";
            Logging.SaveLog(error);
            onFailure(error);
        }
        catch (Exception ex) { Logging.SaveLog("SystemProxyCommand", ex); onFailure(ex.Message); }
    }
}
