using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Alexandra.Core;

namespace Alexandra.App;

// Game-only pixels. No focus stealing, full-desktop fallback, key presses or persistence here.
public sealed class EliteCapture : IGameCapture
{
    public Task<byte[]> CaptureAsync(CancellationToken cancellationToken)
        => Task.Run(() => Encode(CaptureBitmap(cancellationToken), false, cancellationToken), cancellationToken);

    public Task<byte[]> CapturePngAsync(CancellationToken cancellationToken)
        => Task.Run(() => Encode(CaptureBitmap(cancellationToken), true, cancellationToken), cancellationToken);

    private static byte[] Encode(Bitmap bitmap, bool fullResolution, CancellationToken token)
    {
        using (bitmap)
        using (var data = new MemoryStream())
        {
            token.ThrowIfCancellationRequested();
            if (fullResolution) bitmap.Save(data, ImageFormat.Png);
            else
            {
                var scale = Math.Min(1, 1920d / Math.Max(bitmap.Width, bitmap.Height));
                using var reduced = new Bitmap(bitmap, new Size(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))));
                reduced.Save(data, ImageFormat.Jpeg);
            }
            token.ThrowIfCancellationRequested();
            return data.ToArray();
        }
    }

    private static Bitmap CaptureBitmap(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.GetProcessesByName("EliteDangerous64").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
            ?? throw new InvalidOperationException("Elite is not open. I cannot see the game.");
        var window = process.MainWindowHandle;
        if (GetForegroundWindow() != window || IsIconic(window))
            throw new InvalidOperationException("Put Elite in front, then hold your HOTAS button and ask again. I only capture the game.");
        if (!GetClientRect(window, out var rect)) throw new InvalidOperationException("Cannot measure Elite's window.");
        var origin = new Point();
        if (!ClientToScreen(window, ref origin)) throw new InvalidOperationException("Cannot locate Elite's window.");
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 12000 || height > 8000) throw new InvalidOperationException("Elite's window is not capturable.");
        var bitmap = new Bitmap(width, height);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(origin, Point.Empty, bitmap.Size);
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != window) throw new InvalidOperationException("Focus changed during capture. Please try again.");
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
}

