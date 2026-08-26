using Microsoft.UI.Xaml;
using System.Threading.Tasks;

namespace NvimWinUIGui;

/// <summary>Pure-C# Application (no XAML). Created from Program.Main via Application.Start.</summary>
public sealed class App : Application
{
    public App() { }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Unpackaged WinUI3: install the dispatcher sync context on the UI thread so continuations after every await resume here. Without it, XAML touched post-await crashes with RPC_E_DISCONNECTED (0x8001010E). This SDK's ctor does NOT self-install; SetSynchronizationContext is required.
        LogCrash("OnLaunched ENTER");
        SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));

        // Log unhandled exceptions (full stack) so startup failures are diagnosable.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("APPDOMAIN " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash("TASK " + e.Exception); e.SetObserved(); };

        void LogCrash(string s)
        {
            try
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NvimWinUIGui");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "startup.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}\n");
            }
            catch { }
        }

        var w = new MainWindow();
        w.Activate();
    }
}
