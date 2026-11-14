using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Alexandra.App;

internal static class SessionLogSearchProbe
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "Alexandra-Search-Probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var newest = Write(root, "13", "Mandalay", "Planning to buy a Federal Corvette.");
            Write(root, "12", @"federation\_corvette", "Combat in Exphiay.");
            var oldest = Write(root, "11", "Python", "Exploration.", "Federal Corvette; Python");
            var viewer = new SessionLogsWindow(root);
            var search = (TextBox)viewer.FindName("SearchBox");
            var list = (ListBox)viewer.FindName("LogList");
            var count = (TextBlock)viewer.FindName("SearchCount");
            var clear = (Button)viewer.FindName("ClearSearch");
            var save = (Button)viewer.FindName("SaveCopy");
            string Body() { var doc = ((RichTextBox)viewer.FindName("LogContent")).Document; return new TextRange(doc.ContentStart, doc.ContentEnd).Text; }
            check(list.Items.Count == 3 && count.Text == "3 sessions" && !clear.IsEnabled, "search begins with every session, newest first");
            search.Text = "corvette"; Wait(viewer.SearchFinished);
            check(list.Items.Count == 2 && count.Text == "2 of 3 sessions" && Body().Contains("Combat in Exphiay"), "typing Corvette filters to recorded ship usage and selects a matching log");
            check(clear.IsEnabled && save.IsEnabled, "matching sessions remain selectable and exportable");
            var selected = list.SelectedItem;
            search.Text = "combat exphiay"; Wait(viewer.SearchFinished);
            check(list.Items.Count == 1 && ReferenceEquals(list.SelectedItem, selected), "multi-word search preserves a still-matching selection");
            search.Text = "zzzz-no-session"; Wait(viewer.SearchFinished);
            check(list.Items.Count == 0 && !save.IsEnabled && Body().Contains("No matching sessions") &&
                ((FrameworkElement)viewer.FindName("PhotoSection")).Visibility == Visibility.Collapsed, "no results clears stale details and disables exporting");
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(viewer.SearchFinished);
            check(search.Text == "" && list.Items.Count == 3 && !clear.IsEnabled && save.IsEnabled, "Clear restores the session list and details");
            search.Text = "Corvette"; var first = viewer.SearchFinished;
            search.Text = "Mandalay"; Wait(viewer.SearchFinished); Wait(first);
            check(list.Items.Count == 1 && Body().Contains("MANDALAY") && count.Text == "1 of 3 sessions", "rapid typing cannot show stale search results");
            File.WriteAllText(oldest, Document("Mandalay", "Freshly updated session."));
            ((Button)viewer.FindName("RefreshLogs")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(viewer.SearchFinished);
            check(search.Text == "Mandalay" && list.Items.Count == 2 && count.Text == "2 of 3 sessions", "Refresh retains the query and searches new file contents");
            foreach (var width in new[] { 700d, 1060d })
            {
                // Measure the content directly: the probe never shows a real window.
                var surface = (FrameworkElement)viewer.Content;
                surface.Measure(new Size(width - 16, 720)); surface.Arrange(new Rect(0, 0, width - 16, 720)); surface.UpdateLayout();
                check(search.ActualWidth >= 100 && clear.ActualWidth > 30 && list.ActualHeight > 200,
                    $"search and list fit at window width {width} (search {search.ActualWidth:0}, clear {clear.ActualWidth:0}, list {list.ActualHeight:0})");
            }
            check(File.ReadAllText(newest).Contains("Planning to buy"), "search never rewrites session files");
            search.Text = "Corvette"; var pending = viewer.SearchFinished;
            viewer.Close(); Wait(pending);
            check(true, "closing the viewer cancels a pending search safely");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
            Directory.Delete(root, true); // Only these uniquely named synthetic fixtures.
        }
    }
    private static string Document(string ship, string body, string? used = null) =>
        "# Gameplay session — 2026-11-13 10:00 UTC\n\n- Ship: " + ship +
        (used is null ? "" : "\n- Ships used: " + used) + "\n\n## Summary\n\n" + body;
    private static string Write(string root, string day, string ship, string body, string? used = null)
    {
        var path = Path.Combine(root, $"Session-2026-11-{day}-100000-ABCDEF0123456789ABCDEF01.md");
        File.WriteAllText(path, Document(ship, body, used)); return path;
    }
    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!task.IsCompleted) throw new TimeoutException("Session search did not finish.");
        task.GetAwaiter().GetResult();
    }
}
