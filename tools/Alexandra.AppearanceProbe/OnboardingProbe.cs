using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Alexandra.App;
using Alexandra.Core;
using Alexandra.Infrastructure;

internal static class OnboardingProbe
{
    public static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Alexandra-OnboardingProbe-" + Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(directory, "settings.json");
        var keyPath = Path.Combine(directory, "openai.key");
        var store = new SettingsStore(directory);
        var source = new NoNetworkSource();
        var autoStart = new AutoStart();
        var log = new LocalDiagnosticLog(Path.Combine(directory, "logs"), () => false);
        var windows = new List<SetupWindow>();
        var shutdownMode = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SetupWindow Create(SetupMode mode)
        {
            var window = new SetupWindow(store, log, source, new SilentPlayer(), autoStart, mode, sessionLogError: () => "Synthetic session save warning")
            { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            windows.Add(window);
            return window;
        }
        try
        {
            var defaults = store.Value;
            check(defaults.MinimalMode && defaults.KeepOnTop && defaults.ThinkingSoundEnabled &&
                defaults.ResearchBrowserEnabled && defaults.MemoryEnabled && defaults.SessionLogsEnabled && defaults.Voice == "marin" &&
                defaults.CombatCalloutsEnabled && !defaults.DiagnosticLoggingEnabled && !defaults.AllowCloud,
                "new defaults match onboarding without enabling cloud before Save and start");
            var firstRun = Create(SetupMode.FirstRun);
            check(((TextBlock)firstRun.FindName("SetupHeading")).Text == "WELCOME" &&
                ((FrameworkElement)firstRun.FindName("WelcomePanel")).Visibility == Visibility.Visible &&
                ((FrameworkElement)firstRun.FindName("StartupSection")).Visibility == Visibility.Collapsed &&
                ((FrameworkElement)firstRun.FindName("AdditionalSettingsSection")).Visibility == Visibility.Collapsed,
                "onboarding hides startup, voice, cloud toggle, browser, memory, logs and advanced controls");
            check(firstRun.FindName("MinimalBox") is null && firstRun.FindName("TopmostBox") is null &&
                firstRun.FindName("ThinkingSoundBox") is null && firstRun.FindName("SimulateOnboardingButton") is null,
                "duplicate menu toggles and onboarding simulation are removed from Setup");
            var notice = (TextBlock)firstRun.FindName("OnboardingCloudNotice");
            check(notice.Visibility == Visibility.Visible && notice.Text.Contains("OpenAI") &&
                notice.Text.Contains("AI-generated") && (string)((Button)firstRun.FindName("SaveButton")).Content == "Save and start",
                "onboarding explains cloud processing beside the explicit start button");
            var content = (FrameworkElement)firstRun.Content;
            foreach (var height in new[] { 350d, 650d })
            {
                content.Measure(new Size(512, height));
                content.Arrange(new Rect(0, 0, 512, height)); content.UpdateLayout();
                var sections = new[] { "MicrophoneSection", "PushToTalkSection", "ApiKeySection" }
                    .Select(name => (FrameworkElement)firstRun.FindName(name)).ToArray();
                var tops = sections.Select(section => section.TransformToAncestor(content).Transform(new Point()).Y).ToArray();
                check(sections.All(section => section.Visibility == Visibility.Visible) &&
                    tops[0] < tops[1] && tops[1] < tops[2],
                    "onboarding orders microphone, push to talk and API key at " + height + "px");
                ((ScrollViewer)firstRun.FindName("SettingsScroll")).ScrollToEnd(); content.UpdateLayout();
                var save = (Button)firstRun.FindName("SaveButton");
                var bounds = save.TransformToAncestor(content).TransformBounds(new Rect(save.RenderSize));
                check(bounds.Top >= 0 && bounds.Bottom <= height && bounds.Right <= 512,
                    "onboarding start button remains visible at " + height + "px");
            }
            firstRun.Close();
            check(!Directory.Exists(directory) && !store.Value.AllowCloud && !store.Value.SetupCompleted,
                "closing onboarding leaves cloud off and does not create settings or credentials");

            firstRun = Create(SetupMode.FirstRun);
            // Prevent environment-key fallback using an unreadable synthetic saved key.
            Directory.CreateDirectory(directory);
            byte[] unreadableKey = [11, 22, 33, 44];
            File.WriteAllBytes(keyPath, unreadableKey);
            var original = JsonSerializer.Serialize(store.Value);
            ((Button)firstRun.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(((TextBlock)firstRun.FindName("ErrorText")).Text.Contains("Re-enter your API key") &&
                JsonSerializer.Serialize(store.Value) == original && !File.Exists(settingsPath) &&
                File.ReadAllBytes(keyPath).SequenceEqual(unreadableKey),
                "an unreadable key cannot finish onboarding or enable cloud access");
            ((PasswordBox)firstRun.FindName("KeyBox")).Password = "synthetic-onboarding-probe-key";
            ((Button)firstRun.FindName("HotasButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            firstRun.ReceiveButton(new ControllerButtonEvent("synthetic-controller", 8, true, DateTimeOffset.UtcNow));
            // Hidden controls must not accidentally overwrite defaults on a first-run save.
            ((CheckBox)firstRun.FindName("LoggingBox")).IsChecked = true;
            ((CheckBox)firstRun.FindName("MemoryBox")).IsChecked = false;
            ((CheckBox)firstRun.FindName("SessionLogsBox")).IsChecked = false;
            ((CheckBox)firstRun.FindName("CombatCalloutsBox")).IsChecked = false;
            ((CheckBox)firstRun.FindName("BrowserBox")).IsChecked = false;
            ((CheckBox)firstRun.FindName("OpenWithEliteBox")).IsChecked = true;
            firstRun.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            ((ComboBox)firstRun.FindName("VoiceBox")).SelectedItem = "cedar";
            ((Button)firstRun.FindName("PreviewVoiceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)firstRun.FindName("ViewLogsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SaveDialog(firstRun, check);
            var restored = new SettingsStore(directory);
            check(restored.Value.SetupCompleted && restored.Value.AllowCloud &&
                restored.ReadKey() == "synthetic-onboarding-probe-key" &&
                restored.Value.ControllerId == "synthetic-controller" && restored.Value.ControllerButton == 8,
                "Save and start stores the chosen inputs/key and explicitly enables cloud voice");
            check(restored.Value.MinimalMode && restored.Value.KeepOnTop && restored.Value.ThinkingSoundEnabled &&
                restored.Value.ResearchBrowserEnabled && restored.Value.MemoryEnabled && restored.Value.SessionLogsEnabled && restored.Value.CombatCalloutsEnabled &&
                restored.Value.Voice == "marin" && !restored.Value.DiagnosticLoggingEnabled &&
                !restored.Value.OpenWithElite && autoStart.Calls == 0 && source.Calls == 0,
                "onboarding preserves hidden defaults without samples or startup changes");
            check(!Directory.Exists(Path.Combine(directory, "logs")), "onboarding does not write conversation logs");

            store.Value.MinimalMode = false; store.Value.KeepOnTop = false;
            store.Value.ThinkingSoundEnabled = false; store.Value.Voice = "coral";
            store.Value.ResearchBrowserEnabled = false; store.Value.MemoryEnabled = false;
            store.Value.DiagnosticLoggingEnabled = true;
            store.Save();
            firstRun = Create(SetupMode.FirstRun);
            SaveDialog(firstRun, check);
            restored = new SettingsStore(directory);
            check(!restored.Value.MinimalMode && !restored.Value.KeepOnTop && !restored.Value.ThinkingSoundEnabled &&
                restored.Value.Voice == "coral" && !restored.Value.ResearchBrowserEnabled &&
                !restored.Value.MemoryEnabled && restored.Value.DiagnosticLoggingEnabled,
                "first-run key recovery does not reset existing preferences");
            var settings = Create(SetupMode.Settings);
            check(((CheckBox)settings.FindName("SessionLogsBox")).IsChecked == true &&
                ((TextBlock)settings.FindName("SessionLogPath")).Text.EndsWith(Path.Combine("AlexandraData", "Session Logs")) &&
                ((TextBlock)settings.FindName("SessionLogStatus")).Text == "Synthetic session save warning",
                "Setup exposes enabled local session journals, their folder and storage failures");
            ((CheckBox)settings.FindName("SessionLogsBox")).IsChecked = false;
            check(((CheckBox)settings.FindName("CombatCalloutsBox")).IsChecked == true,
                "Setup exposes enabled optional combat callouts");
            ((CheckBox)settings.FindName("CombatCalloutsBox")).IsChecked = false;
            check(new SettingsStore(directory).Value.CombatCalloutsEnabled, "combat-callout preference waits for Save");
            check(new SettingsStore(directory).Value.SessionLogsEnabled, "session-journal preference waits for Save");
            check(((FrameworkElement)settings.FindName("WelcomePanel")).Visibility == Visibility.Collapsed &&
                ((FrameworkElement)settings.FindName("StartupSection")).Visibility == Visibility.Visible &&
                ((FrameworkElement)settings.FindName("AdditionalSettingsSection")).Visibility == Visibility.Visible &&
                ((TextBlock)settings.FindName("OnboardingCloudNotice")).Visibility == Visibility.Collapsed &&
                settings.FindName("SimulateOnboardingButton") is null,
                "regular Setup retains advanced options without onboarding simulation");
            ((TextBox)settings.FindName("ModelBox")).Text = "synthetic-model";
            SaveDialog(settings, check);
            restored = new SettingsStore(directory);
            check(!restored.Value.SessionLogsEnabled, "session-journal opt-out persists after Save");
            check(!restored.Value.CombatCalloutsEnabled, "combat-callout opt-out persists after Save and restart");
            check(!restored.Value.MinimalMode && !restored.Value.KeepOnTop && !restored.Value.ThinkingSoundEnabled &&
                restored.Value.Model == "synthetic-model" && autoStart.Calls == 1,
                "regular Setup saves without resetting menu-only preferences");
        }
        finally
        {
            foreach (var window in windows) window.Close();
            Application.Current.ShutdownMode = shutdownMode;
            foreach (var path in new[] { keyPath, settingsPath, settingsPath + ".tmp", keyPath + ".tmp" })
                if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
    private static void SaveDialog(SetupWindow window, Action<bool, string> check)
    {
        // Real modal Save lifecycle, without a visible window or any real services.
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!string.IsNullOrEmpty(((TextBlock)window.FindName("ErrorText")).Text)) window.Close();
        }), DispatcherPriority.Loaded);
        check(window.ShowDialog() == true, "onboarding/settings Save closes successfully");
    }
    private sealed class NoNetworkSource : IVoicePreviewSource
    {
        public int Calls;
        public Task<byte[]> GenerateAsync(string voice, string key, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Onboarding must not request voice audio."); }
    }
    private sealed class AutoStart : IGameAutoStartService
    {
        public int Calls;
        public void Apply(bool enabled) => Calls++;
    }
    private sealed class SilentPlayer : IStreamingAudioPlayer
    {
        public event Action<float>? Level { add { } remove { } }
        public bool HasUnplayedAudio => false;
        public void Append(string itemId, byte[] pcm) => throw new InvalidOperationException("Onboarding must not play audio.");
        public IReadOnlyDictionary<string, int> Stop() => new Dictionary<string, int>();
        public void Dispose() { }
    }
}
