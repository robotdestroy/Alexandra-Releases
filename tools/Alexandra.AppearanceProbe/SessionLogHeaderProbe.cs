using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Alexandra.App;

internal static class SessionLogHeaderProbe
{
    public static void Run(Action<bool, string> check, string? screenshotDirectory)
    {
        var root = Path.Combine(Path.GetTempPath(), "Alexandra-Header-Probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string markdown = """
                # Gameplay session — 2026-11-13 10:00 UTC

                - Status: Shutdown
                - Recorded from: 2026-11-13 10:00:00 UTC
                - Last game activity: 2026-11-13 12:18:00 UTC
                - Ended: 2026-11-13 12:18:00 UTC
                - Recorded span: 138 minutes
                - Ship: federation_corvette

                ## Summary

                ### Session stats
                - Credits received: 24,500,000 CR
                - Missions completed: 3
                - Jumps: 8

                You patrolled a conflict zone in your Federal Corvette, then returned to the station to redeem combat bonds and complete three missions.

                ## Stated session plans
                - Support the current community goal and collect outstanding rewards.

                ## Pick up next time
                - Restock and return to the conflict zone.
                """;
            var path = Path.Combine(root, "Session-2026-11-13-100000-ABCDEF0123456789ABCDEF01.md");
            File.WriteAllText(path, markdown);
            var result = new SessionLogMarkdown(path, _ => throw new InvalidOperationException()).Render(markdown);
            var header = result.Header!;
            check(header is not null && result.Document.Blocks.FirstBlock == header.Section, "generated metadata becomes the first in-document status strip");
            var section = header!.Section;
            check(section.BorderThickness == new Thickness(0, 2, 0, 2) && section.Background == Brushes.Transparent,
                "journal status strip uses orange horizontal rules without a fill or vertical dividers");
            var table = section.Blocks.OfType<Table>().Single();
            check(table.Columns.Count == 3 && table.RowGroups[0].Rows.Count == 2, "wide journals use three aligned metadata columns");
            var cells = table.RowGroups[0].Rows.SelectMany(r => r.Cells).ToArray();
            string Text(FrameworkContentElement element) => (element is FlowDocument document
                ? new TextRange(document.ContentStart, document.ContentEnd)
                : new TextRange(((TextElement)element).ContentStart, ((TextElement)element).ContentEnd)).Text.Trim();
            check(cells.Select(c => Text(c.Blocks.FirstBlock)).SequenceEqual(new[] { "STATUS", "SHIP", "RECORDED SPAN", "RECORDED FROM", "LAST GAME ACTIVITY", "ENDED" }),
                "status, ship and span lead without losing timestamps");
            check(Text(cells[1].Blocks.LastBlock) == "FEDERAL CORVETTE" &&
                cells.All(c => c.Blocks.FirstBlock.FontSize == 12 && c.Blocks.LastBlock.FontSize == 17 && c.BorderThickness == new Thickness(0)),
                "header uses canonical uppercase ships, small labels and larger readable values");
            check(Text(result.Document.Blocks.ElementAt(1)) == "Summary" && Text(result.Document).Contains("24,500,000 CR") &&
                result.Document.FontFamily.Source == "Segoe UI", "Summary and statistics retain ordinary selectable Markdown formatting");
            foreach (var (width, columns) in new[] { (450d, 2), (300d, 1), (900d, 3) })
            {
                header.UpdateWidth(width);
                check(table.Columns.Count == columns && table.RowGroups[0].Rows.SelectMany(r => r.Cells).Count() == 6,
                    $"metadata reflows to {columns} columns without losing fields");
            }
            var open = new SessionLogMarkdown(path, _ => { }).Render(markdown.Replace("Status: Shutdown", "Status: Open — game shutdown not yet observed"));
            check(Text(open.Document).Contains("OPEN — GAME SHUTDOWN NOT YET OBSERVED"), "open status remains last-known, never claims a live connection");
            check(new SessionLogMarkdown(path, _ => { }).Render("# My journal\n\n## Summary\nNotes").Header is null,
                "non-generated Markdown retains normal headings");

            var viewer = new SessionLogsWindow(root);
            var content = (FrameworkElement)viewer.Content;
            ((TextBlock)viewer.FindName("FolderPath")).Text = "LOCAL SESSION JOURNAL · PREVIEW DATA";
            var richText = (RichTextBox)viewer.FindName("LogContent");
            foreach (var width in new[] { 1060d, 700d, 1300d })
            {
                content.Measure(new Size(width, 780)); content.Arrange(new Rect(0, 0, width, 780)); content.UpdateLayout();
                richText.UpdateLayout();
                var activeTable = richText.Document.Blocks.OfType<Section>().First().Blocks.OfType<Table>().Single();
                check(activeTable.Columns.Count == (width == 700 ? 2 : 3), $"viewer resize at {width} updates header columns");
                // Check layout bounds for every header cell, including wrapped timestamp lines.
                check(activeTable.RowGroups[0].Rows.SelectMany(r => r.Cells).All(c =>
                    c.ContentStart.GetCharacterRect(LogicalDirection.Forward).Left >= 0 &&
                    c.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Right <= richText.ActualWidth),
                    $"metadata fits the reading pane at width {width}");
                if (screenshotDirectory is not null && width != 1300)
                {
                    Directory.CreateDirectory(screenshotDirectory);
                    var bitmap = new RenderTargetBitmap((int)width, 780, 96, 96, PixelFormats.Pbgra32);
                    var background = new DrawingVisual();
                    using (var drawing = background.RenderOpen()) drawing.DrawRectangle(viewer.Background, null, new Rect(0, 0, width, 780));
                    bitmap.Render(background); bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(screenshotDirectory, $"journal-header-{width:0}.png")); encoder.Save(stream);
                }
            }
            viewer.Close();
            check(File.ReadAllText(path) == markdown, "viewing and resizing never rewrite the original journal");
        }
        finally { Directory.Delete(root, true); } // Only this unique synthetic-fixture directory.
    }
}
