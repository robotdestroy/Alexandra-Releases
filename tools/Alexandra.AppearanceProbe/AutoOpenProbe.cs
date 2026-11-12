using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Alexandra.App;

internal static class AutoOpenProbe
{
    public static void Run(Action<bool, string> check)
    {
        // Unique IPC objects: never touches the user's running companion/watcher.
        var name = "Local\\Alexandra.Probe." + Guid.NewGuid().ToString("N");
        using (var instance = new SingleInstanceCoordinator(name))
        {
            check(instance.IsPrimary && !SingleInstanceCoordinator.IsReady(name), "process lock is not a ready window");
            instance.SetReady(true);
            check(SingleInstanceCoordinator.IsReady(name), "window readiness is acknowledged explicitly");
            var secondary = Task.Run(() => { using var other = new SingleInstanceCoordinator(name); return !other.IsPrimary; }).GetAwaiter().GetResult();
            check(secondary && SingleInstanceCoordinator.IsReady(name), "secondary launch cannot clear primary readiness");
            using var restored = new ManualResetEventSlim(); using var activated = new ManualResetEventSlim();
            instance.Listen(activated.Set, restored.Set);
            SingleInstanceCoordinator.RestorePrimary(name);
            check(restored.Wait(TimeSpan.FromSeconds(2)) && !activated.IsSet, "automatic restore uses separate non-activation IPC");
            instance.SetReady(false); check(!SingleInstanceCoordinator.IsReady(name), "closing window clears readiness before process exit");
        }
        check(!SingleInstanceCoordinator.IsReady(name), "disposed companion leaves no ready signal");
        var window = new Window { Width = 100, Height = 80, Left = -32000, Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show(); window.WindowState = WindowState.Minimized; Pump();
            var handle = new WindowInteropHelper(window).Handle;
            var foreground = GetForegroundWindow();
            check(IsIconic(handle), "synthetic window begins minimized");
            AutoOpenWindow.RestoreWithoutFocus(window); Pump();
            check(IsWindowVisible(handle) && !IsIconic(handle), "automatic restore reveals a minimized window");
            check(GetForegroundWindow() == foreground, "automatic restore preserves foreground keyboard focus");
        }
        finally { window.Close(); }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
