using System.Windows;
using Velopack;
namespace Alexandra.App;
public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The install hook rescues legacy personal data from the installer's
        // rollback directory before it is removed. No audio, WPF or cloud work.
        VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ => AppDataPaths.Initialize())
            .OnBeforeUpdateFastCallback(_ => GameAutoStartService.StopForUpdate())
            .OnBeforeUninstallFastCallback(_ => GameAutoStartService.RemoveForUninstall())
            .Run();
        try { AppDataPaths.Initialize(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            if (!args.Contains(GameAutoStartService.WatchArgument, StringComparer.Ordinal))
                MessageBox.Show("Alexandra could not safely migrate your saved data. Nothing was deleted. Close older copies of Alexandra and try again. Your data belongs in %LOCALAPPDATA%\\AlexandraData. If this continues, ask for help before reinstalling.",
                    "Alexandra — saved data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Headless watcher: no WPF application, audio, game journals, cloud clients or updater polling.
        if (args.Length == 1 && args[0] == GameAutoStartService.WatchArgument)
        { GameWatcherHost.Run(); return; }
        using var instance = new SingleInstanceCoordinator();
        if (!instance.IsPrimary)
        {
            if (!args.Contains("--auto-open", StringComparer.Ordinal)) instance.SignalPrimary();
            return;
        }
        // A second launch must only focus the existing conversation, never apply an update over it.
        // Only a genuinely new session may install a previously downloaded package.
        var startupUpdates = new VelopackUpdateBackend();
        if (startupUpdates.PendingVersion is not null)
        {
            try { startupUpdates.ScheduleApplyAndRestart(); return; }
            catch (Exception) { /* Keep the current version usable if the updater cannot start. */ }
        }
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow { ShowActivated = !args.Contains("--auto-open", StringComparer.Ordinal) };
        window.Loaded += (_, _) => { instance.SetReady(true); _ = Task.Run(GameAutoStartService.ResumeIfEnabled); };
        window.Closing += (_, _) => instance.SetReady(false);
        instance.Listen(() => app.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!window.IsLoaded) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        })), () => app.Dispatcher.BeginInvoke(new Action(() => AutoOpenWindow.RestoreWithoutFocus(window))));
        app.Run(window);
    }
}

