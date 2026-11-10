using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Alexandra.App;

public static class WindowAppearance
{
    public static void ApplyNotice(Border panel, TextBlock label, bool minimal, string? message)
    {
        label.Text = message ?? "";
        panel.Visibility = minimal && !string.IsNullOrWhiteSpace(message) ? Visibility.Visible : Visibility.Collapsed;
    }
    public static void Apply(Window window, Panel surface, FrameworkElement header, FrameworkElement status,
        FrameworkElement waveformArea, bool minimal, bool keepOnTop)
    {
        // Only the backing fades. The waveform stays fully opaque and remains centered as chrome collapses.
        surface.Background = minimal
            ? CreateMinimalBackground()
            : (Brush)window.FindResource("WindowBrush");
        header.Visibility = status.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        window.ResizeMode = minimal ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        surface.Cursor = minimal ? Cursors.SizeAll : null;
        ToolTipService.SetIsEnabled(waveformArea, !minimal);
        window.Topmost = keepOnTop;
    }

    public static RadialGradientBrush CreateMinimalBackground()
    {
        // The outer ellipse ends inside the window, leaving a fully clear margin on every side.
        var brush = new RadialGradientBrush
        {
            Center = new Point(.5, .5), GradientOrigin = new Point(.5, .5), RadiusX = .46, RadiusY = .40,
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(153, 0, 0, 0), 0),
                new(Color.FromArgb(153, 0, 0, 0), .58),
                new(Color.FromArgb(90, 0, 0, 0), .78),
                new(Color.FromArgb(24, 0, 0, 0), .93),
                new(Color.FromArgb(0, 0, 0, 0), 1)
            }
        };
        brush.Freeze();
        return brush;
    }
}
