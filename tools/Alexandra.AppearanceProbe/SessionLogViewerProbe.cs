using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Alexandra.App;

internal static class SessionLogViewerProbe
{
    public static void Run(Action<bool, string> check)
    {
        // Entirely synthetic local documents/images. No user photos, APIs or browser launches.
        var root = Path.Combine(Path.GetTempPath(), "Alexandra-Markdown-Probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var log = Path.Combine(root, "Session-2026-11-13-100000-ABCDEF0123456789ABCDEF01.md");
            var photo = Path.Combine(root, "Test photo α.png");
            var portrait = Path.Combine(root, "portrait.png");
            var missing = Path.Combine(root, "missing.png");
            var corrupt = Path.Combine(root, "corrupt.png");
            WritePhoto(photo, 800, 450); WritePhoto(portrait, 30, 900); File.WriteAllText(corrupt, "not an image");
            var text = $$"""
                # Synthetic gameplay session

                ## Session stats
                - **Credits received:** 1,000
                - *Missions:* 2
                - [x] Deliver cargo
                - [ ] Explore

                1. First stop
                2. Second stop

                > A quoted note

                | Material | Count |
                | --- | --- |
                | Iron | 12 |

                `inline code` and ~~finished~~

                ```text
                sample code
                ```

                ## Photos
                - 12:00 UTC — Docking — [View photo](<{{new Uri(photo).AbsoluteUri}}>)
                - ![Tall photo](portrait.png)
                - [Duplicate](<{{new Uri(photo).AbsoluteUri}}>)
                - [Missing photo](missing.png)
                - [Corrupt photo](corrupt.png)

                [Safe website](https://example.com) [Not executable](file:///C:/Windows/notepad.exe)
                [Not a script](javascript:alert) [Not a share](file://server/share/image.png)
                ![Remote image](https://example.com/tracker.png)
                <script>not executed</script>
                """;
            File.WriteAllText(log, text);
            var links = new List<string>();
            var rendered = new SessionLogMarkdown(log, links.Add).Render(text);
            var document = rendered.Document;
            var plain = new TextRange(document.ContentStart, document.ContentEnd).Text;
            check(plain.Contains("Credits received:") && !plain.Contains("**Credits") && !plain.Contains("## Session stats"), "Markdown markers become formatted text");
            check(document.Blocks.OfType<Paragraph>().First().FontSize == 24 && document.Blocks.OfType<Paragraph>().First().FontWeight == FontWeights.SemiBold,
                "Markdown headings use readable hierarchy");
            check(document.Blocks.OfType<System.Windows.Documents.List>().Any(l => l.MarkerStyle == TextMarkerStyle.Decimal) &&
                document.Blocks.OfType<System.Windows.Documents.List>().Any(l => l.MarkerStyle == TextMarkerStyle.Disc) && plain.Contains("☑") && plain.Contains("☐"), "ordered, unordered and task lists render");
            check(document.Blocks.OfType<Table>().Single().RowGroups[0].Rows.Count == 2, "Markdown tables have header and data rows");
            check(document.Blocks.OfType<Section>().Any() && plain.Contains("sample code") && plain.Contains("inline code"), "quotes and fenced/inline code remain readable");
            check(rendered.Photos.Count == 4 && rendered.Photos[0].Path == photo && rendered.Photos[1].Caption == "Tall photo", "file URI and relative photo links create deduplicated thumbnails");
            var hyperlinks = Descendants(document).OfType<Hyperlink>().ToArray();
            check(hyperlinks.Length == 6 && plain.Contains("[Image: Remote image]") && links.Count == 0, "remote images and unsafe links never activate or fetch content");
            hyperlinks.Single(l => l.ToolTip?.ToString() == "https://example.com").RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            check(links.SequenceEqual(new[] { "https://example.com" }), "safe links activate only after a click");

            foreach (var target in new[] { "file://server/share/a.png", "\\\\server\\share\\a.png", "https://example.com/a.png", "file:///C:/Windows/notepad.exe", "javascript:alert(1)", "C:\\photo.png:stream.png", "\\\\?\\C:\\photo.png" })
                check(!SessionLogMedia.TryPhotoPath(target, log, out _), "rejects unsafe photo target " + target);
            check(SessionLogMedia.TryPhotoPath("Test%20photo%20%CE%B1.png", log, out var decoded) && decoded == photo, "spaces and Unicode in relative photos resolve");
            var thumb = SessionLogMedia.Load(photo, 320);
            var tall = SessionLogMedia.Load(portrait, 320);
            check(thumb is { IsFrozen: true, PixelWidth: 320 } && tall is { PixelHeight: 320 }, "thumbnails are immutable and bounded on the longest edge");
            check(SessionLogMedia.Load(missing, 320) is null && SessionLogMedia.Load(corrupt, 320) is null, "missing and corrupt photos do not throw");
            using (File.Open(photo, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            check(true, "photo decoding releases file handles");

            var viewer = new SessionLogsWindow(root);
            Wait(viewer.PhotosLoaded);
            viewer.Measure(new Size(1060, 760)); viewer.Arrange(new Rect(0, 0, 1060, 760)); viewer.UpdateLayout();
            var items = (ItemsControl)viewer.FindName("PhotoThumbnails");
            var photos = items.Items.Cast<SessionPhoto>().ToArray();
            check(items.Items.Count == 4 && ((FrameworkElement)viewer.FindName("PhotoSection")).Visibility == Visibility.Visible && photos[0].Thumbnail is not null,
                "viewer shows asynchronously loaded thumbnails for the selected log");
            check(photos[2].Status == "Photo unavailable" && photos[3].Status == "Photo unavailable", "unavailable thumbnails have explanatory placeholders");
            check(((RichTextBox)viewer.FindName("LogContent")).IsDocumentEnabled, "formatted log allows clickable links and selectable text");

            var large = new SessionPhotoWindow(photos, 0);
            Wait(large.PhotoLoaded);
            large.Measure(new Size(1120, 760)); large.Arrange(new Rect(0, 0, 1120, 760)); large.UpdateLayout();
            var image = (Image)large.FindName("PhotoImage");
            check(image.Source is not null && ((TextBlock)large.FindName("PhotoCount")).Text == "1 / 4" && !((Button)large.FindName("Previous")).IsEnabled, "photo viewer fits the selected image and shows position");
            var width = image.Width;
            Click(large, "ZoomIn"); check(image.Width > width, "zoom enlarges the photo");
            Click(large, "Fit"); check(Math.Abs(image.Width - width) < 1, "Fit restores the fitted size");
            Click(large, "Next"); Wait(large.PhotoLoaded);
            check(((TextBlock)large.FindName("Caption")).Text == "Tall photo" && ((Button)large.FindName("Previous")).IsEnabled, "Next loads the following photo");
            Click(large, "Next"); Wait(large.PhotoLoaded);
            check(image.Source is null && ((TextBlock)large.FindName("PhotoStatus")).Text.Contains("unavailable") && !((Button)large.FindName("ZoomIn")).IsEnabled,
                "missing full-size photo stays navigable without a crash");
            Click(large, "Previous"); Wait(large.PhotoLoaded);
            check(image.Source is not null, "Previous recovers from a missing photo");
            large.Close();

            var list = (ListBox)viewer.FindName("LogList");
            list.SelectedIndex = -1;
            check(((FrameworkElement)viewer.FindName("PhotoSection")).Visibility == Visibility.Collapsed && items.Items.Count == 0, "changing sessions clears old thumbnails");
            viewer.Close();
            var cancelled = new SessionLogsWindow(root); cancelled.Close(); Wait(cancelled.PhotosLoaded);
            check(true, "closing while thumbnails load cancels safely");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            Directory.Delete(root, true); // Only the unique synthetic fixture directory above.
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
        if (!task.IsCompleted) throw new TimeoutException("Photo test did not finish.");
        task.GetAwaiter().GetResult();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
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
