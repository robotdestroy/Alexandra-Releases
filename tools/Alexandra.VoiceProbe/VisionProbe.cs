using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Alexandra.Core;
using Alexandra.Infrastructure;

internal static class VisionProbe
{
    // Synthetic in-memory panel only; no desktop/game capture or files.
    public static async Task RunAsync(string key)
    {
        var rendered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 800, 400));
                    dc.DrawText(new FormattedText("FUEL LOW", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 70, Brushes.Orange, 1), new Point(45, 60));
                    dc.DrawText(new FormattedText("12% REMAINING", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 48, Brushes.White, 1), new Point(45, 190));
                }
                var image = new RenderTargetBitmap(800, 400, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
                var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var memory = new MemoryStream(); encoder.Save(memory); rendered.TrySetResult(memory.ToArray());
            }
            catch (Exception ex) { rendered.TrySetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var jpeg = await rendered.Task;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var helper = new OpenAiCompanion(http, () => key, () => "gpt-5.4", () => "marin");
        var captures = 0;
        var answer = await helper.AnswerAsync("Read this panel: what warning and exact percentage does it show? Only read the image; no web lookup needed.",
            new(null, "{}", "Synthetic image test, no personal data"), [], _ => { captures++; return Task.FromResult(jpeg); }, default);
        Console.WriteLine("ANSWER: " + answer.Speech);
        if (captures != 1 || !answer.Speech.Contains("fuel", StringComparison.OrdinalIgnoreCase)
            || !(answer.Speech.Contains("12") || answer.Speech.Contains("twelve", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The image contents were not correctly read.");
        Console.WriteLine("PASS: real research-model image analysis recognized the synthetic warning and percentage. No game capture/files.");
    }
}
