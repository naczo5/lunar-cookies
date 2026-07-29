using System.Linq;
using System.IO;
using Microsoft.UI.Xaml;

namespace LunarCookies;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        UnhandledException += (_, e) => WriteCrashLog("Unhandled WinUI exception", e.Exception);
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            WriteCrashLog("App resource initialization failed", ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Any(a => a == "--smoke-parser"))
        {
            int code = SmokeTests.RunParserSmoke();
            Environment.Exit(code);
            return;
        }
        int cookieAuthIndex = Array.FindIndex(commandLine,
            a => string.Equals(a, "--smoke-cookie-auth", StringComparison.OrdinalIgnoreCase));
        if (cookieAuthIndex >= 0 && cookieAuthIndex + 1 < commandLine.Length)
        {
            int code = SmokeTests.RunCookieAuthSmoke(commandLine[cookieAuthIndex + 1]);
            Environment.Exit(code);
            return;
        }

        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            WriteCrashLog("Main window startup failed", ex);
            throw;
        }
    }

    private static void WriteCrashLog(string context, Exception? exception)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "lunar_cookies_ui_crash.log");
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // A crash logger must never hide the original failure.
        }
    }
}
