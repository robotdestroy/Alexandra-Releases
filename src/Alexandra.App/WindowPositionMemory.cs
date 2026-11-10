using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Alexandra.App;

// Native screen coordinates throughout: do not mix WPF DIPs with monitor pixels.
public sealed record SavedWindowPosition(int X, int Y, string? MonitorName = null,
    int WorkAreaX = 0, int WorkAreaY = 0);

public readonly record struct MonitorWorkArea(string Name, int Left, int Top, int Right, int Bottom);

public static class WindowPositioning
{
    public static (int X, int Y) Resolve(SavedWindowPosition saved, MonitorWorkArea monitor, int width, int height)
    {
        // Keep the same offset on a known display even if Windows rearranged the displays.
        var sameMonitor = !string.IsNullOrEmpty(saved.MonitorName) &&
            string.Equals(saved.MonitorName, monitor.Name, StringComparison.OrdinalIgnoreCase);
        var x = sameMonitor ? (long)monitor.Left + saved.X - saved.WorkAreaX : saved.X;
        var y = sameMonitor ? (long)monitor.Top + saved.Y - saved.WorkAreaY : saved.Y;
        // An oversized window stays anchored at the top-left so the menu remains accessible.
        return ((int)Math.Clamp(x, monitor.Left, Math.Max((long)monitor.Left, (long)monitor.Right - Math.Max(1, width))),
            (int)Math.Clamp(y, monitor.Top, Math.Max((long)monitor.Top, (long)monitor.Bottom - Math.Max(1, height))));
    }

    public static SavedWindowPosition? Capture(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || window.WindowState != WindowState.Normal ||
            !GetWindowRect(handle, out var bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
            return null;
        var monitor = ReadMonitor(MonitorFromWindow(handle, 2));
        return monitor is { } area ? new(bounds.Left, bounds.Top, area.Name, area.Left, area.Top) : null;
    }

    public static bool Restore(Window window, SavedWindowPosition saved)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || window.WindowState != WindowState.Normal) return false;
        MonitorWorkArea? match = null;
        MonitorCallback callback = (IntPtr monitor, IntPtr dc, ref WindowSizing.NativeRect rect, IntPtr data) =>
        {
            var area = ReadMonitor(monitor);
            if (area is { } found && string.Equals(found.Name, saved.MonitorName, StringComparison.OrdinalIgnoreCase))
                match = found;
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        var target = match ?? ReadMonitor(MonitorFromPoint(new NativePoint { X = saved.X, Y = saved.Y }, 2));
        if (target is not { } workArea) return false;
        // Crossing displays can change WPF's DPI and physical size. Re-read the resulting
        // rectangle and clamp again, without changing the user's chosen scale or focus.
        for (var pass = 0; pass < 2; pass++)
        {
            if (!GetWindowRect(handle, out var bounds)) return false;
            var point = Resolve(saved, workArea, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            if (!SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, 0, 0, 0x0001 | 0x0004 | 0x0010)) return false;
        }
        return true;
    }

    private static MonitorWorkArea? ReadMonitor(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) &&
            info.Work.Right > info.Work.Left && info.Work.Bottom > info.Work.Top
            ? new(info.Device, info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public WindowSizing.NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref WindowSizing.NativeRect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowSizing.NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

// Owns only placement preferences. It never changes voice, credentials, game input or focus.
public sealed class WindowPositionMemory
{
    private readonly Window _window;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _saveTimer;
    private bool _ready;

    public WindowPositionMemory(Window window, SettingsStore settings)
    {
        _window = window;
        _settings = settings;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += SaveAfterMove;
        window.Loaded += Loaded;
        window.LocationChanged += LocationChanged;
        window.SizeChanged += SizeChanged;
        window.Closing += Closing;
        window.Closed += Closed;
    }

    private void Loaded(object sender, RoutedEventArgs e)
    {
        _window.Loaded -= Loaded;
        if (_settings.Value.WindowPosition is { } saved) WindowPositioning.Restore(_window, saved);
        _ready = true;
    }
    private void LocationChanged(object? sender, EventArgs e) => QueueSave();
    private void SizeChanged(object sender, SizeChangedEventArgs e) => QueueSave();
    private void QueueSave()
    {
        if (!_ready || _window.WindowState != WindowState.Normal) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }
    private void SaveAfterMove(object? sender, EventArgs e) => Save();
    private void Closing(object? sender, CancelEventArgs e) => Save();
    private void Save()
    {
        _saveTimer.Stop();
        if (!_ready || WindowPositioning.Capture(_window) is not { } position ||
            position == _settings.Value.WindowPosition) return;
        var previous = _settings.Value.WindowPosition;
        _settings.Value.WindowPosition = position;
        try { _settings.Save(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Do not interrupt voice or closing over a nonessential window preference.
            _settings.Value.WindowPosition = previous;
        }
    }
    private void Closed(object? sender, EventArgs e)
    {
        _ready = false;
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveAfterMove;
        _window.Loaded -= Loaded;
        _window.LocationChanged -= LocationChanged;
        _window.SizeChanged -= SizeChanged;
        _window.Closing -= Closing;
        _window.Closed -= Closed;
    }
}
