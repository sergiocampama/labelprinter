using System.Diagnostics;
using Velopack;

namespace LabelPrinter;

/// <summary>
/// Entry point. Velopack runs first: on install/update/uninstall Windows starts
/// the app with hook arguments that VelopackApp.Run() handles and exits on.
/// Only a normal launch falls through to the tray app.
/// </summary>
internal static class Program
{
    private const string InstanceId = "LabelPrinter-7C3E91A4";

    /// <summary>Launch argument: start in the tray without opening the window.</summary>
    internal const string BackgroundArg = "--background";

    // Held for the process lifetime to keep a single instance per user session.
    private static Mutex? _instanceMutex;

    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            // Start right after install (and open the window), instead of only at
            // the next logon via the Startup shortcut.
            .OnAfterInstallFastCallback(_ => Launch())
            .Run();

        _instanceMutex = new Mutex(initiallyOwned: true, $@"Local\{InstanceId}-Instance", out bool isFirst);
        if (!isFirst)
        {
            // Already running: ask that instance to open its window (e.g. the user
            // clicked the Start menu shortcut while the app sits in the tray).
            try
            {
                using var show = EventWaitHandle.OpenExisting($@"Local\{InstanceId}-Show");
                show.Set();
            }
            catch { /* the other instance is starting or exiting */ }
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{InstanceId}-Show");

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(showEvent, openWindow: !StartInBackground(args)));
    }

    /// <summary>
    /// Stay in the tray when restarted by an update, or when started by the
    /// Startup shortcut at logon. A launch by the user opens the window.
    /// </summary>
    private static bool StartInBackground(string[] args)
    {
        if (args.Contains(BackgroundArg, StringComparer.OrdinalIgnoreCase))
            return true;

        // Startup-folder items are launched by Explorer right after it starts.
        // Velopack shortcuts can't carry arguments, so use that as the signal.
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            var explorerStart = Process.GetProcessesByName("explorer")
                .Where(p => p.SessionId == session)
                .Select(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } })
                .DefaultIfEmpty(DateTime.MaxValue)
                .Min();
            return DateTime.Now - explorerStart < TimeSpan.FromMinutes(3);
        }
        catch
        {
            return false;
        }
    }

    private static void Launch()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { /* it still starts at the next logon */ }
    }
}
