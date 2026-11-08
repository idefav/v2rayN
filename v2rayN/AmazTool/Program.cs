using ServiceLib.Services;

namespace AmazTool;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // This internal command must never enter updater/restart handling or wait
        // for keyboard input, including malformed requests and probe failures.
        if (args.FirstOrDefault() == ListenerBindProbe.Command)
        {
            try { Environment.ExitCode = ListenerBindProbe.Run(args, Console.Out); }
            catch { Environment.ExitCode = 2; }
            return;
        }
        if (args.FirstOrDefault() == AppUpgradeRequest.Command)
        {
            Environment.ExitCode = UpgradeApp.Run(args);
            return;
        }
        try
        {
            // If no arguments are provided, display usage guidelines and exit
            if (args.Length == 0)
            {
                ShowHelp();
                return;
            }

            // Parse command based on first argument
            switch (args[0].ToLowerInvariant())
            {
                case "rebootas":
                    // Handle application restart
                    HandleRebootAsync();
                    break;

                case "help":
                case "--help":
                case "-h":
                case "/?":
                    // Display help information
                    ShowHelp();
                    break;

                default:
                    Console.Error.WriteLine("Invalid AmazToolV2 command.");
                    Environment.ExitCode = 2;
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"An error occurred: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Display help information and usage guidelines
    /// </summary>
    private static void ShowHelp()
    {
        Console.WriteLine(Resx.Resource.Guidelines);
        Console.WriteLine("Available commands:");
        Console.WriteLine("  rebootas             - Restart the application");
        Console.WriteLine("  help                 - Display this help information");
        Thread.Sleep(5000);
    }

    /// <summary>
    /// Handle application restart
    /// </summary>
    private static void HandleRebootAsync()
    {
        Console.WriteLine("Restarting application...");
        Thread.Sleep(1000);
        Utils.StartV2RayN();
    }

}
