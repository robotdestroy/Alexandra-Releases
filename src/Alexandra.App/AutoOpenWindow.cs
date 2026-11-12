using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Alexandra.App;

public static class AutoOpenWindow
{
    public static void RestoreWithoutFocus(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!window.IsLoaded || handle == IntPtr.Zero) return;
        // SW_SHOWNOACTIVATE restores a minimized window without taking keyboard focus.
        ShowWindow(handle, 4);
        SetWindowPos(handle, window.Topmost ? new IntPtr(-1) : IntPtr.Zero, 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0010 | 0x0040); // NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int width, int height, uint flags);
}
