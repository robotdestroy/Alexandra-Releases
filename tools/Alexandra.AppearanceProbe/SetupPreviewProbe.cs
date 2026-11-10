using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Alexandra.App;
using Alexandra.Core;
using Alexandra.Infrastructure;

internal static class SetupPreviewProbe
{
    public static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Alexandra-SetupPreviewProbe-" + Guid.NewGuid().ToString("N"));
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        SetupWindow? setup = null;
        try
        {
            var store = new SettingsStore(directory);
            check(store.Value.Voice == "marin", "new profiles default to Marin");
            check(System.Text.Json.JsonSerializer.Deserialize<Settings>("{}")!.Voice == "marin",
                "profiles with no voice preference inherit Marin");
            store.Value.Voice = "coral";
            store.Save();
            check(new SettingsStore(directory).Value.Voice == "coral", "existing saved voice choices are preserved");
            store.Value.Voice = "marin";
            store.Value.AllowCloud = false;
            store.Save();
            var source = new Source();
            var player = new Player();
            setup = new SetupWindow(store, new LocalDiagnosticLog(Path.Combine(directory, "logs"), () => false), source, player);
            var voices = (ComboBox)setup.FindName("VoiceBox");
            var preview = (Button)setup.FindName("PreviewVoiceButton");
            var status = (TextBlock)setup.FindName("VoicePreviewStatus");
            var consent = (CheckBox)setup.FindName("ConsentBox");
            var openWithElite = (CheckBox)setup.FindName("OpenWithEliteBox");
            check(openWithElite.IsChecked == false && (string)openWithElite.Content == "Open Alexandra when Elite starts",
                "Setup exposes the optional game auto-open setting");
            openWithElite.IsChecked = true;
            check(!new SettingsStore(directory).Value.OpenWithElite, "changing auto-open is not applied before Save");
            var key = (PasswordBox)setup.FindName("KeyBox");
            setup.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            check((string)voices.SelectedItem == "marin" && source.Voices.Count == 0 && player.Plays == 0,
                "Setup restores voice silently, without an API request or playback");
            var content = (FrameworkElement)setup.Content;
            content.Measure(new Size(512, 650)); content.Arrange(new Rect(0, 0, 512, 650)); content.UpdateLayout();
            check(voices.ActualWidth > 250 && preview.ActualWidth >= 70 && (string)preview.Content == "Preview",
                "voice selector and Preview button fit Setup width");
            var scroll = (ScrollViewer)setup.FindName("SettingsScroll");
            var save = (Button)setup.FindName("SaveButton");
            var error = (TextBlock)setup.FindName("ErrorText");
            foreach (var height in new[] { 350d, 650d, 900d })
            {
                content.Measure(new Size(512, height));
                content.Arrange(new Rect(0, 0, 512, height)); content.UpdateLayout();
                scroll.ScrollToHome(); content.UpdateLayout();
                var before = save.TransformToAncestor(content).TransformBounds(new Rect(save.RenderSize));
                scroll.ScrollToEnd(); content.UpdateLayout();
                var after = save.TransformToAncestor(content).TransformBounds(new Rect(save.RenderSize));
                check(scroll.ScrollableHeight > 0 && before == after && after.Top >= 0 && after.Bottom <= height &&
                    after.Left >= 0 && after.Right <= 512 && after.Height >= 44,
                    $"Save stays visible and stationary at {height}px content height when scrolling");
            }
            var model = (TextBox)setup.FindName("ModelBox");
            var originalModel = model.Text;
            model.Text = "";
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            content.Measure(new Size(512, 350)); content.Arrange(new Rect(0, 0, 512, 350)); content.UpdateLayout();
            var errorBounds = error.TransformToAncestor(content).TransformBounds(new Rect(error.RenderSize));
            var saveBounds = save.TransformToAncestor(content).TransformBounds(new Rect(save.RenderSize));
            check(error.Text.Contains("research model") && errorBounds.Top >= 0 && errorBounds.Bottom <= saveBounds.Top &&
                saveBounds.Bottom <= 350 && new SettingsStore(directory).Value.Model == originalModel,
                "validation stays visible with Save and does not persist invalid settings");
            model.Text = originalModel; error.Text = "";
            content.Measure(new Size(512, 650)); content.Arrange(new Rect(0, 0, 512, 650)); content.UpdateLayout();
            key.Password = "synthetic-preview-not-a-key";
            voices.SelectedItem = "cedar";
            check(source.Voices.Count == 0 && status.Text.Contains("Enable cloud voice"),
                "Setup does not preview without explicit cloud consent");
            consent.IsChecked = true;
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => status.Text.Contains("preview finished"));
            check(source.Voices.SequenceEqual(new[] { "cedar" }) && player.Plays == 1,
                "Preview button uses the selected voice and unsaved typed key");
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => player.Plays == 2);
            check(source.Voices.Count == 1, "replaying the selected voice uses the in-memory sample");
            voices.SelectedItem = "alloy";
            voices.SelectedItem = "coral";
            PumpUntil(() => player.Plays == 3);
            check(source.Voices.SequenceEqual(new[] { "cedar", "coral" }),
                "fast voice selection only previews the last choice");
            check(new SettingsStore(directory).Value.Voice == "marin",
                "previewing does not save or overwrite the configured voice");
            player.Hold = true;
            voices.SelectedItem = "marin";
            PumpUntil(() => player.HasUnplayedAudio);
            consent.IsChecked = false;
            check(!player.HasUnplayedAudio && status.Text == "", "revoking consent immediately stops preview audio");
            consent.IsChecked = true;
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => player.HasUnplayedAudio);
            key.Password = "synthetic-replacement-key";
            check(!player.HasUnplayedAudio, "editing the API key immediately stops preview audio");
            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => player.HasUnplayedAudio);
            setup.Close();
            PumpUntil(() => player.Disposed);
            check(!player.HasUnplayedAudio && new SettingsStore(directory).Value.Voice == "marin",
                "closing Setup stops output and retains the saved voice");
            check(!File.Exists(Path.Combine(directory, "openai.key")) && !Directory.Exists(Path.Combine(directory, "logs")),
                "preview key and sample are never persisted or logged");
        }
        finally
        {
            setup?.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            var settingsFile = Path.Combine(directory, "settings.json");
            if (File.Exists(settingsFile)) File.Delete(settingsFile);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
    private static void PumpUntil(Func<bool> ready)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(4);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!ready()) throw new InvalidOperationException("Setup preview check timed out.");
    }
    private sealed class Source : IVoicePreviewSource
    {
        public List<string> Voices { get; } = [];
        public Task<byte[]> GenerateAsync(string voice, string key, CancellationToken token)
        {
            if (!key.StartsWith("synthetic-")) throw new InvalidOperationException("Only synthetic probe keys allowed.");
            Voices.Add(voice);
            return Task.FromResult(new byte[480]);
        }
    }
    private sealed class Player : IStreamingAudioPlayer
    {
        public event Action<float>? Level { add { } remove { } }
        public int Plays; public bool Hold, Disposed;
        public bool HasUnplayedAudio { get; private set; }
        public void Append(string itemId, byte[] pcm) { Plays++; HasUnplayedAudio = Hold; }
        public IReadOnlyDictionary<string, int> Stop() { HasUnplayedAudio = false; return new Dictionary<string, int>(); }
        public void Dispose() { Disposed = true; }
    }
}
