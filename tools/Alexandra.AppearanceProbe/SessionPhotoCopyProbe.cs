using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Alexandra.App;

internal static class SessionPhotoCopyProbe
{
    public static void Run(Action<bool, string> check)
    {
        // Synthetic images and a fake clipboard: never overwrite the user's actual clipboard.
        var root = Path.Combine(Path.GetTempPath(), "Alexandra-PhotoCopy-Probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var first = Path.Combine(root, "wide.png"); WritePhoto(first, 5120, 80);
            var second = Path.Combine(root, "second.png"); WritePhoto(second, 100, 60);
            var missing = Path.Combine(root, "missing.png");
            var clipboard = new FakeClipboard();
            var viewer = new SessionPhotoWindow([new(first, "Wide photo"), new(second, "Second photo"), new(missing, "Missing photo")], 0, clipboard);
            var copy = (Button)viewer.FindName("CopyPhoto");
            var notice = (TextBlock)viewer.FindName("CopyNotice");
            check(!copy.IsEnabled, "photo Copy is disabled until image loading finishes");
            Wait(viewer.PhotoLoaded);
            check(copy.IsEnabled && clipboard.Images.Count == 0, "opening a photo does not copy anything automatically");
            check(((BitmapSource)((Image)viewer.FindName("PhotoImage")).Source).PixelWidth == 4096, "large photo display stays bounded independently of clipboard resolution");
            Click(viewer, "ZoomIn"); Click(viewer, "CopyPhoto");
            check(!copy.IsEnabled && notice.Text == "Copying photo…", "copy action reports progress and prevents duplicate clicks");
            Wait(viewer.PhotoCopied);
            check(clipboard.Images is [{ PixelWidth: 5120, PixelHeight: 80, IsFrozen: true }] && notice.Text == "Photo copied." && copy.IsEnabled,
                "Copy uses original pixels rather than the scaled/zoomed preview and reports success");
            using (var png = new MemoryStream())
            {
                var data = WindowsPhotoClipboard.CreateData(clipboard.Images[0], png);
                check(data.GetDataPresent(DataFormats.Bitmap) && data.GetDataPresent("PNG") &&
                    !data.GetDataPresent(DataFormats.UnicodeText) && !data.GetDataPresent(DataFormats.FileDrop),
                    "Windows clipboard payload contains image formats, not a path or navigation text");
                var decoded = BitmapDecoder.Create((Stream)data.GetData("PNG"), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                check(decoded.Frames[0].PixelWidth == 5120 && decoded.Frames[0].PixelHeight == 80,
                    "PNG clipboard representation preserves the original image dimensions");
            }

            clipboard.Busy = true; Click(viewer, "CopyPhoto"); Wait(viewer.PhotoCopied);
            check(notice.Text.Contains("Clipboard is busy") && copy.IsEnabled && clipboard.Images.Count == 1, "busy clipboard is recoverable and never claims success");
            clipboard.Busy = false; Click(viewer, "CopyPhoto"); Wait(viewer.PhotoCopied);
            check(notice.Text == "Photo copied." && clipboard.Images.Count == 2, "Copy can be retried after a clipboard failure");

            Click(viewer, "CopyPhoto"); var cancelledCopy = viewer.PhotoCopied;
            Click(viewer, "Next"); Wait(viewer.PhotoLoaded); Wait(cancelledCopy);
            check(clipboard.Images.Count == 2 && notice.Visibility == Visibility.Collapsed && copy.IsEnabled,
                "switching photos cancels an in-flight copy and clears stale feedback");
            Click(viewer, "CopyPhoto"); Wait(viewer.PhotoCopied);
            check(clipboard.Images[^1].PixelWidth == 100 && clipboard.Images[^1].PixelHeight == 60, "Copy follows the currently selected photo");

            foreach (var width in new[] { 680d, 1120d })
            {
                var surface = (FrameworkElement)viewer.Content;
                surface.Measure(new Size(width - 16, 700)); surface.Arrange(new Rect(0, 0, width - 16, 700)); surface.UpdateLayout();
                var next = (Button)viewer.FindName("Next");
                check(copy.ActualWidth > 40 && next.TranslatePoint(new Point(next.ActualWidth, 0), surface).X <=
                    copy.TranslatePoint(new Point(0, 0), surface).X, $"Copy fits beside navigation and zoom at window width {width}");
            }

            Click(viewer, "Next"); Wait(viewer.PhotoLoaded);
            check(!copy.IsEnabled, "missing photos cannot be copied");
            Click(viewer, "Previous"); Wait(viewer.PhotoLoaded);
            File.Delete(second); // Only this probe's synthetic image.
            Click(viewer, "CopyPhoto"); Wait(viewer.PhotoCopied);
            check(notice.Text.Contains("original file") && copy.IsEnabled && clipboard.Images.Count == 3,
                "a file removed after preview loading produces a clear message without copying stale pixels");
            Click(viewer, "Previous"); Wait(viewer.PhotoLoaded);
            Click(viewer, "CopyPhoto"); var closingCopy = viewer.PhotoCopied;
            viewer.Close(); Wait(closingCopy);
            check(clipboard.Images.Count == 3, "closing the photo viewer prevents a pending clipboard write");
            check(File.Exists(first), "Copy never modifies the source photo");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
            Directory.Delete(root, true); // Only the unique synthetic-fixture directory above.
        }
    }
    private sealed class FakeClipboard : IPhotoClipboard
    {
        public bool Busy { get; set; }
        public List<BitmapSource> Images { get; } = [];
        public void Copy(BitmapSource image)
        {
            if (Busy) throw new ExternalException("Synthetic clipboard contention");
            Images.Add(image);
        }
    }
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!task.IsCompleted) throw new TimeoutException("Photo copy test did not finish.");
        task.GetAwaiter().GetResult();
    }
    private static void WritePhoto(string path, int width, int height)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) dc.DrawRectangle(Brushes.DarkOrange, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
