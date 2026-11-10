using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Alexandra.App;

internal static class WindowPositionProbe
{
    public static void Run(Action<bool, string> check)
    {
        var primary = new MonitorWorkArea("primary", 0, 0, 1920, 1040);
        var left = new MonitorWorkArea("left", -1920, -200, 0, 840);
        check(WindowPositioning.Resolve(new(150, 80, "primary"), primary, 460, 250) == (150, 80),
            "unchanged display restores exact window position");
        check(WindowPositioning.Resolve(new(-1800, -100, "left", -1920, -200), left, 460, 250) == (-1800, -100),
            "negative screen coordinates remain valid");
        var moved = new MonitorWorkArea("left", 1920, 0, 3840, 1040);
        check(WindowPositioning.Resolve(new(-1800, -100, "left", -1920, -200), moved, 460, 250) == (2040, 100),
            "rearranged display preserves the window's offset on that display");
        check(WindowPositioning.Resolve(new(-1800, -100, "removed", -1920, -200), primary, 460, 250) == (0, 0),
            "disconnected display recovers inside an available display");
        check(WindowPositioning.Resolve(new(1800, 1000, "primary"), primary, 460, 250) == (1460, 790),
            "changed resolution and taskbar leave the entire window visible");
        check(WindowPositioning.Resolve(new(1800, 1000, "primary"), primary, 920, 500) == (1000, 540),
            "clamping uses the resulting physical size after a DPI change");
        check(WindowPositioning.Resolve(new(int.MaxValue, int.MinValue, "primary", int.MinValue, int.MaxValue), primary, 460, 250) == (1460, 0),
            "extreme saved coordinates cannot overflow monitor clamping");
        check(WindowPositioning.Resolve(new(800, 900), new("tiny", 100, 200, 400, 400), 920, 500) == (100, 200),
            "oversized windows keep their top-left accessible without altering scale");

        var directory = Path.Combine(Path.GetTempPath(), "Alexandra-PositionProbe-" + Guid.NewGuid().ToString("N"));
        var windows = new List<Window>();
        try
        {
            var store = new SettingsStore(directory);
            check(store.Value.WindowPosition is null && !Directory.Exists(directory), "new profiles retain centered startup without writing preferences");
            var saved = new SavedWindowPosition(-1650, -50, "left", -1920, -200);
            store.Value.WindowPosition = saved;
            store.Value.Voice = "coral";
            store.Value.WindowScale = .75;
            store.SaveKey("synthetic-position-probe-key");
            store.Save();
            var restored = new SettingsStore(directory);
            check(restored.Value.WindowPosition == saved, "position and monitor survive settings round-trip");
            check(restored.Value.Voice == "coral" && restored.Value.WindowScale == .75 && restored.ReadKey() == "synthetic-position-probe-key",
                "position saving preserves voice, size and encrypted credentials");

            Window CreateWindow(SettingsStore settings, bool minimal)
            {
                // Invisible, non-activating synthetic WPF windows; no app services or real profile.
                var window = new Window { Width = 460, Height = 250, AllowsTransparency = true,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Opacity = 0,
                    ResizeMode = minimal ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen };
                windows.Add(window);
                _ = new WindowPositionMemory(window, settings);
                window.Show();
                Pump(50);
                return window;
            }
            // Real HWND lifecycle checks catch coordinate conversion and startup-centering regressions.
            store.Value.WindowPosition = null;
            var first = CreateWindow(store, minimal: true);
            var start = WindowPositioning.Capture(first)!;
            var requested = start with { X = start.WorkAreaX + 45, Y = start.WorkAreaY + 55 };
            check(WindowPositioning.Restore(first, requested), "native placement works for the minimal window");
            var expected = WindowPositioning.Capture(first);
            Pump(650);
            check(new SettingsStore(directory).Value.WindowPosition == expected, "moving automatically saves without opening Setup or closing");
            first.Close();
            var secondStore = new SettingsStore(directory);
            var second = CreateWindow(secondStore, minimal: false);
            check(WindowPositioning.Capture(second) == expected, "next window launch restores the saved position rather than recentering");
            WindowPositioning.Restore(second, expected! with { X = expected.X + 30, Y = expected.Y + 20 });
            expected = WindowPositioning.Capture(second);
            second.Close();
            check(new SettingsStore(directory).Value.WindowPosition == expected, "immediate close saves normal-mode movement before debounce fires");
            var third = CreateWindow(new SettingsStore(directory), minimal: true);
            check(WindowPositioning.Capture(third) == expected, "normal to minimal launch preserves the same position");
            third.WindowState = WindowState.Minimized;
            check(WindowPositioning.Capture(third) is null, "minimized sentinel coordinates are never saved");
            third.Close();
            check(new SettingsStore(directory).Value.WindowPosition == expected, "closing while minimized preserves last normal position");
            // Older profiles have no position, and retain other preferences.
            File.WriteAllText(Path.Combine(directory, "settings.json"), """{"Voice":"coral","WindowScale":0.75}""");
            var old = new SettingsStore(directory);
            check(old.Value.WindowPosition is null && old.Value.Voice == "coral" && old.Value.WindowScale == .75,
                "older settings remain compatible without a position preference");
        }
        finally
        {
            foreach (var window in windows) if (new WindowInteropHelper(window).Handle != IntPtr.Zero) window.Close();
            foreach (var name in new[] { "settings.json", "settings.json.tmp", "openai.key" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
