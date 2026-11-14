using System.Windows;
using System.Windows.Input;
using Alexandra.Core;
using Alexandra.Infrastructure;
using NAudio.Wave;

namespace Alexandra.App;

public enum SetupMode { Settings, FirstRun }

public partial class SetupWindow : Window
{
    private readonly SettingsStore _store;
    private readonly LocalDiagnosticLog _log;
    private readonly VoicePreviewController _preview;
    private readonly IGameAutoStartService _autoStart;
    private readonly SetupMode _mode;
    private bool _previewReady;
    private bool _learnKey, _learnHotas;
    private int _virtualKey, _button;
    private string? _device;
    public SetupWindow(SettingsStore store, LocalDiagnosticLog log,
        IVoicePreviewSource? previewSource = null, IStreamingAudioPlayer? previewPlayer = null,
        IGameAutoStartService? autoStart = null, SetupMode mode = SetupMode.Settings, Func<string?>? sessionLogError = null)
    {
        InitializeComponent();
        _store = store;
        _log = log;
        _mode = mode;
        _autoStart = autoStart ?? new GameAutoStartService();
        _preview = new VoicePreviewController(previewSource ?? new RealtimeVoicePreviewSource(() => new RealtimeWebSocketTransport()),
            previewPlayer ?? new StreamingAudioPlayer());
        _preview.StatusChanged += text => VoicePreviewStatus.Text = text;
        var value = store.Value;
        _virtualKey = value.KeyboardKey;
        _button = value.ControllerButton;
        _device = value.ControllerId;
        MicrophoneBox.Items.Add("Windows default microphone");
        for (var i = 0; i < WaveInEvent.DeviceCount; i++) MicrophoneBox.Items.Add(WaveInEvent.GetCapabilities(i).ProductName);
        MicrophoneBox.SelectedIndex = Math.Clamp(value.Microphone + 1, 0, MicrophoneBox.Items.Count - 1);
        foreach (var voice in VoiceCatalog.Names) VoiceBox.Items.Add(voice);
        VoiceBox.SelectedItem = value.Voice;
        if (VoiceBox.SelectedIndex < 0) VoiceBox.SelectedIndex = 0;
        JournalBox.Text = value.JournalDirectory;
        ModelBox.Text = value.Model;
        ConsentBox.IsChecked = value.AllowCloud;
        LoggingBox.IsChecked = value.DiagnosticLoggingEnabled;
        BrowserBox.IsChecked = value.ResearchBrowserEnabled;
        MemoryBox.IsChecked = value.MemoryEnabled;
        SessionLogsBox.IsChecked = value.SessionLogsEnabled;
        CombatCalloutsBox.IsChecked = value.CombatCalloutsEnabled;
        SessionLogPath.Text = SettingsStore.SessionLogDirectory;
        SessionLogStatus.Text = sessionLogError?.Invoke() ?? "";
        OpenWithEliteBox.IsChecked = value.OpenWithElite;
        if (mode == SetupMode.FirstRun)
        {
            Title = "Alexandra · Welcome";
            SetupHeading.Text = "WELCOME";
            WelcomePanel.Visibility = Visibility.Visible;
            StartupSection.Visibility = Visibility.Collapsed;
            AdditionalSettingsSection.Visibility = Visibility.Collapsed;
            OnboardingCloudNotice.Visibility = Visibility.Visible;
            SaveButton.Content = "Save and start";
            SaveHint.Text = "Other options are available later in Setup.";
        }
        UpdateLabels();
        // Wire after restoring the selected voice. Merely opening Setup must be silent and free of preview requests.
        VoiceBox.SelectionChanged += async (_, _) => await PreviewVoiceAsync();
        ConsentBox.Unchecked += (_, _) => _preview.Cancel(clearCache: true);
        KeyBox.PasswordChanged += (_, _) => _preview.Cancel(clearCache: true);
        Loaded += (_, _) => _previewReady = true;
        Closing += (_, _) => { _previewReady = false; _preview.Dispose(); KeyBox.Clear(); };
    }
    private async void PreviewVoiceClick(object sender, RoutedEventArgs e) => await PreviewVoiceAsync();
    private async Task PreviewVoiceAsync()
    {
        if (_mode == SetupMode.FirstRun) return;
        if (!_previewReady || VoiceBox.SelectedItem is not string voice) return;
        try
        {
            var consent = ConsentBox.IsChecked == true;
            var key = consent ? (string.IsNullOrWhiteSpace(KeyBox.Password) ? _store.ReadKey() : KeyBox.Password.Trim()) : null;
            await _preview.PreviewAsync(voice, key, consent);
        }
        catch (Exception)
        {
            _preview.Cancel();
            VoicePreviewStatus.Text = "The saved key couldn't be read. Re-enter it to preview a voice.";
        }
    }
    private void UpdateLabels()
    {
        KeyboardButton.Content = _learnKey ? "Press a key now…" : "Keyboard · " + KeyInterop.KeyFromVirtualKey(_virtualKey);
        HotasButton.Content = _learnHotas ? "Press your HOTAS button now…" : "Detect HOTAS button";
        HotasLabel.Text = _device is null ? "No HOTAS binding. Keyboard and mouse remain available." : $"HOTAS button {_button} assigned.";
    }
    private void LearnKeyboard(object sender, RoutedEventArgs e) { _learnKey = true; _learnHotas = false; UpdateLabels(); }
    private void ResetKeyboard(object sender, RoutedEventArgs e) { _virtualKey = 123; _learnKey = false; UpdateLabels(); }
    private void CaptureKey(object sender, KeyEventArgs e)
    {
        if (!_learnKey) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;
        e.Handled = true;
        if (key == Key.Escape) { _learnKey = false; UpdateLabels(); return; }
        _virtualKey = KeyInterop.VirtualKeyFromKey(key);
        _learnKey = false;
        UpdateLabels();
    }
    private void LearnHotas(object sender, RoutedEventArgs e) { _learnHotas = true; _learnKey = false; UpdateLabels(); }
    private void ClearHotas(object sender, RoutedEventArgs e) { _learnHotas = false; _device = null; UpdateLabels(); }
    public void ReceiveButton(ControllerButtonEvent e)
    {
        if (!_learnHotas) return;
        _device = e.DeviceId; _button = e.ButtonIndex; _learnHotas = false;
        UpdateLabels();
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        _preview.Cancel();
        ErrorText.Text = "";
        try
        {
            if (_mode == SetupMode.Settings &&
                (string.IsNullOrWhiteSpace(JournalBox.Text) || string.IsNullOrWhiteSpace(ModelBox.Text)))
            { ErrorText.Text = "Choose a journal folder and research model."; return; }
            if (_mode == SetupMode.FirstRun && string.IsNullOrWhiteSpace(KeyBox.Password))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(_store.ReadKey()))
                    { ErrorText.Text = "Add your OpenAI API key to start Alexandra."; return; }
                }
                catch (InvalidOperationException)
                { ErrorText.Text = "Re-enter your API key to start Alexandra."; return; }
            }
            _store.SaveKey(KeyBox.Password);
            var value = _store.Value;
            value.KeyboardKey = _virtualKey; value.ControllerId = _device; value.ControllerButton = _button;
            value.Microphone = MicrophoneBox.SelectedIndex - 1;
            if (_mode == SetupMode.FirstRun)
            {
                // The onboarding button explicitly enables the cloud service described beside
                // the key. Hidden preferences retain their defaults or existing saved choices.
                value.AllowCloud = true;
            }
            else
            {
                value.Model = ModelBox.Text.Trim(); value.Voice = (string)VoiceBox.SelectedItem;
                value.JournalDirectory = JournalBox.Text.Trim();
                value.AllowCloud = ConsentBox.IsChecked == true;
                value.DiagnosticLoggingEnabled = LoggingBox.IsChecked == true;
                value.ResearchBrowserEnabled = BrowserBox.IsChecked == true;
                value.MemoryEnabled = MemoryBox.IsChecked == true;
                value.SessionLogsEnabled = SessionLogsBox.IsChecked == true;
                value.CombatCalloutsEnabled = CombatCalloutsBox.IsChecked == true;
            }
            value.SetupCompleted = true;
            _store.Save();
            try
            {
                if (_mode == SetupMode.Settings)
                    GameAutoStartPreference.Save(_store, _autoStart, OpenWithEliteBox.IsChecked == true);
            }
            catch (Exception)
            {
                OpenWithEliteBox.IsChecked = value.OpenWithElite;
                ErrorText.Text = "Other settings were saved, but auto-open could not be changed. Check Windows startup permissions and try again.";
                return;
            }
            KeyBox.Clear();
            DialogResult = true;
        }
        catch (Exception) { ErrorText.Text = "Setup could not be saved. Check access to your Alexandra settings folder."; }
    }
    private void ViewLogs(object sender, RoutedEventArgs e)
    {
        if (_mode == SetupMode.Settings) new DiagnosticsWindow(_log) { Owner = this }.ShowDialog();
    }
    private void OpenSessionLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            new SessionLogsWindow(SettingsStore.SessionLogDirectory) { Owner = this }.Show();
        }
        catch (Exception) { ErrorText.Text = "Could not open the session logs folder. Check access to the path above."; }
    }
}

