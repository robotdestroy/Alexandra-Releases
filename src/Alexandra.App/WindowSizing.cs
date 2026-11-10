using System.Runtime.InteropServices;
using System.Windows;

namespace Alexandra.App;

public static class WindowSizing
{
    public const double BaseWidth = 460, BaseHeight = 250, MinimumScale = .5, MaximumScale = 2;
    public const int EnterSizeMove = 0x0231, Sizing = 0x0214, ExitSizeMove = 0x0232;

    public static double ClampScale(double scale) => double.IsFinite(scale)
        ? Math.Clamp(scale, MinimumScale, MaximumScale) : 1;
    public static Size SizeForScale(double scale)
    {
        scale = ClampScale(scale);
        return new Size(BaseWidth * scale, BaseHeight * scale);
    }
    public static double ScaleForSize(double width, double height) => ClampScale(Math.Min(width / BaseWidth, height / BaseHeight));

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    // WM_SIZING supplies screen-pixel bounds, not WPF logical units. Keep the opposite edge anchored.
    // https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-sizing
    public static NativeRect Constrain(NativeRect proposed, int edge, double dpiX, double dpiY, double initialScale)
    {
        if (edge is < 1 or > 8) return proposed;
        dpiX = double.IsFinite(dpiX) && dpiX > 0 ? dpiX : 1;
        dpiY = double.IsFinite(dpiY) && dpiY > 0 ? dpiY : 1;
        var fromWidth = (proposed.Right - proposed.Left) / (BaseWidth * dpiX);
        var fromHeight = (proposed.Bottom - proposed.Top) / (BaseHeight * dpiY);
        var scale = edge switch
        {
            1 or 2 => fromWidth,
            3 or 6 => fromHeight,
            _ => Math.Abs(fromWidth - initialScale) >= Math.Abs(fromHeight - initialScale) ? fromWidth : fromHeight
        };
        var size = SizeForScale(scale);
        var width = (int)Math.Round(size.Width * dpiX);
        var height = (int)Math.Round(size.Height * dpiY);
        if (edge is 1 or 4 or 7) proposed.Left = proposed.Right - width;
        else proposed.Right = proposed.Left + width;
        if (edge is 3 or 4 or 5) proposed.Top = proposed.Bottom - height;
        else proposed.Bottom = proposed.Top + height;
        return proposed;
    }
}
