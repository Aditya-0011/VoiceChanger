using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace VoiceChanger.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    public static Window? MainWindowInstance { get; private set; }
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        UnhandledException += (s, e) =>
        {
            try
            {
                System.IO.File.WriteAllText(@"crash.txt", $"UnhandledException: {e.Message}\n{e.Exception}\nStackTrace:\n{e.Exception.StackTrace}");
            }
            catch { }
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                System.IO.File.WriteAllText(@"crash.txt", $"AppDomain UnhandledException: {e.ExceptionObject}");
            }
            catch { }
        };

        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        // Attach console to caller terminal if launched with telemetry flags or environment variable
        if (IsTelemetryFlagEnabled())
        {
            AttachConsoleToParent();
        }

        InitializeComponent();
    }

    /// <summary>
    /// Checks if telemetry logging was requested via CLI flags, raw command line,
    /// packaged AppLifecycle activation args, or environment variable.
    /// </summary>
    public static bool IsTelemetryFlagEnabled()
    {
        // 1. Environment variable: $env:VOICECHANGER_TELEMETRY="1"
        if (Environment.GetEnvironmentVariable("VOICECHANGER_TELEMETRY") is "1" or "true" or "True")
        {
            return true;
        }

        // 2. Command-line args array (supports both bare words like 'telemetry' and flags like '--telemetry')
        if (Environment.GetCommandLineArgs().Any(a =>
            a.Equals("telemetry", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--telemetry", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("log-telemetry", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--log-telemetry", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-t", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 3. Raw command line string
        string cmdLine = Environment.CommandLine;
        if (cmdLine.Contains("telemetry", StringComparison.OrdinalIgnoreCase) ||
            cmdLine.Contains(" -t", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 4. Windows App SDK AppInstance activation args (packaged MSIX launch)
        try
        {
            var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch &&
                activatedArgs.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs)
            {
                if (!string.IsNullOrEmpty(launchArgs.Arguments) &&
                    (launchArgs.Arguments.Contains("telemetry", StringComparison.OrdinalIgnoreCase) ||
                     launchArgs.Arguments.Contains("-t", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fallback gracefully
        }

        return false;
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);

    private const uint ATTACH_PARENT_PROCESS = unchecked((uint)-1);

    private static void AttachConsoleToParent()
    {
        try
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput(), System.Text.Encoding.UTF8) { AutoFlush = true };
                Console.SetOut(stdout);
                Console.WriteLine();
                Console.WriteLine("================================================================================");
                Console.WriteLine("[VoiceChanger] Telemetry logging enabled. Streaming live metrics & parameters...");
                Console.WriteLine("================================================================================");
                Console.WriteLine();
            }
        }
        catch
        {
            // Suppress if no parent console available
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindowInstance = _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(@"crash.txt", $"OnLaunched Exception: {ex.Message}\n{ex}\n{ex.StackTrace}");
            throw;
        }
    }
}
