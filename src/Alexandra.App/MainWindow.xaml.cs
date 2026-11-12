using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Alexandra.Core;
using Alexandra.Infrastructure;
using NAudio.Wave;

namespace Alexandra.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settings = new();
    private readonly WindowPositionMemory _windowPosition;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(150) };
    private readonly IAudioDevice _audio = new AudioDevice();
    private readonly IStreamingAudioPlayer _speaker = new StreamingAudioPlayer();
    private readonly ThinkingSoundCue _thinkingCue;
    private readonly IThinkingSoundPlayer _failureSound = new ThinkingSoundPlayer(AttentionPulse.CreatePcm());
    private string? _voiceNotice;
    private readonly AppUpdateCoordinator _updates = new(new VelopackUpdateBackend());
    private readonly CancellationTokenSource _lifetime = new();
    private UpdatesWindow? _updatesWindow;
    private readonly GameScreenActions _screens;
    private readonly string _screenshotFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Alexandra");
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SavedScreenshot> _savedScreenshots = new();
    private readonly RawInputControllerSource _controller = new();
    private readonly SemaphoreSlim _recordGate = new(1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly List<Rectangle> _bars = [];
    private readonly ICompanion _research;
    private readonly IGameContextSource _game;
    private readonly IGameRecordSource _records;
    private (int Turn, string Scope, string Json)? _lastRecords;
    private readonly ICompanionMemory _memory;
    private readonly ICombatContextSource _combat;
    private readonly CombatReplyGuard _combatReplies = new();
    private readonly CombatCalloutPolicy _calloutPolicy = new();
    private readonly IStreamingAudioPlayer _calloutAudio = new StreamingAudioPlayer();
    private readonly CombatCalloutPlayer _callouts;
    private DateTimeOffset _calloutQuietUntil;
    private int _discardingCombatTurn = -1;
    private readonly ISystemResolver _systems;
    private readonly CopiedDestinationMemory _copiedDestination = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<Task<DestinationPasteResult>>> _pasteTurns = new();
    private readonly LocalDiagnosticLog _log;
    private IVoiceSession _voice = null!;
    private GlobalKeyboardPushToTalkTrigger? _keyboard;
    private HwndSource? _windowSource;
    private IReadOnlyList<Source> _sources = [];
    private string? _recordOwner;
    private DateTimeOffset _recordStarted;
    private Task<int>? _beginTurn;
    private int _uiTurn;
    private bool _closed, _setupOpen, _busy;
    private float _level;
    private double _phase;
    private double _resizeStartScale = 1;
    private SetupWindow? _setup;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"Alexandra · {_updates.CurrentVersion}";
        VersionLabel.Text = _updates.CurrentVersion;
        ApplyWindowScale(_settings.Value.WindowScale, persist: false);
        ApplyAppearance();
        _windowPosition = new WindowPositionMemory(this, _settings);
        _log = new LocalDiagnosticLog(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Alexandra", "Logs"), () => _settings.Value.DiagnosticLoggingEnabled);
        _log.Write("app.start", "Alexandra " + _updates.CurrentVersion + "; browser=" + _settings.Value.ResearchBrowserEnabled);
        _thinkingCue = new ThinkingSoundCue(new ThinkingSoundPlayer(), onFailure: ex =>
            _log.Write("thinking_sound.unavailable", ex.GetType().Name + "; cue disabled for this launch; voice unchanged."));
        _screens = new GameScreenActions(new EliteCapture(), new GameScreenshotStore(_screenshotFolder));
        _research = new OpenAiCompanion(_http, _settings.ReadKey, () => _settings.Value.Model, () => _settings.Value.Voice,
            () => new ResearchBrowser(_log), () => _settings.Value.ResearchBrowserEnabled, _log);
        _memory = new CompanionMemory(() => _settings.Value.JournalDirectory,
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alexandra", "Memory"),
            () => _settings.Value.MemoryEnabled, sessionLogs: new MarkdownSessionLogWriter(SettingsStore.SessionLogDirectory),
            sessionLogsEnabled: () => _settings.Value.SessionLogsEnabled);
        _game = new JournalContextSource(() => _settings.Value.JournalDirectory); // Read-only fallback if the memory file is unavailable.
        _records = new OnDemandGameRecords(() => _settings.Value.JournalDirectory);
        _combat = new CombatContextSource(() => _settings.Value.JournalDirectory);
        _callouts = new CombatCalloutPlayer(new RealtimeCombatCalloutSource(() => new RealtimeWebSocketTransport()), _calloutAudio);
        _callouts.Failed += () => _log.Write("combat.callout_unavailable", "Skipped; automatic audio paused for two minutes. Conversation is unchanged.");
        _systems = new EdsmSystemResolver(_http);
        CreateVoiceSession();
        for (var i = 0; i < 43; i++)
        {
            var bar = new Rectangle { Width = 3, Height = 3,
                Fill = (Brush)FindResource("AccentBrush"), Margin = new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center };
            _bars.Add(bar); Waveform.Children.Add(bar);
        }
        _audio.Level += value => _level = value;
        _speaker.Level += value => { if (_recordOwner is null) _level = value; };
        _calloutAudio.Level += value => { if (_recordOwner is null && !_speaker.HasUnplayedAudio) _level = value; };
        _timer.Tick += Animate; _timer.Start();
        _controller.ButtonChanged += ControllerButton;
        _controller.DeviceDisconnected += (_, id) => Ui(() => {
            if (_recordOwner == "hotas:" + id) _ = EndRecordingAsync(_recordOwner, true);
            if (_settings.Value.ControllerId == id) StatusText.Text = "HOTAS disconnected · keyboard still available";
        });
        _controller.InputError += (_, _) => Ui(() => StatusText.Text = "Controller input unavailable · use the keyboard");
        SourceInitialized += InitializeInputs;
        Closed += OnClosed;
        Loaded += FirstLoaded;
        _updates.Changed += state => Ui(() => UpdatesMenu.Header = state.Phase == UpdatePhase.Ready ? "Update ready…" : "Updates…");
        UpdateHint();
        StatusText.Text = _settings.Value.AllowCloud ? "Connecting conversation" : "Open menu → Setup to get started";
    }
    private void Ui(Action action)
    {
        if (!_closed && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => { if (!_closed) action(); }));
    }
    private async void FirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= FirstLoaded;
        _ = WatchMemoryAsync(_lifetime.Token);
        _ = WatchCombatAsync(_lifetime.Token);
        var needsSetup = !_settings.Value.SetupCompleted && !_settings.Value.AllowCloud;
        try { needsSetup |= string.IsNullOrWhiteSpace(_settings.ReadKey()); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { needsSetup = true; }
        if (needsSetup) await OpenSetupAsync(SetupMode.FirstRun);
        if (!_closed) _ = WatchUpdatesAsync(_lifetime.Token);
    }
    private async Task WatchUpdatesAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            while (!token.IsCancellationRequested)
            {
                if (_settings.Value.AutoUpdateEnabled)
                {
                    using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
                    check.CancelAfter(TimeSpan.FromMinutes(5));
                    await _updates.CheckAndDownloadAsync(check.Token);
                }
                await Task.Delay(TimeSpan.FromHours(6), token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task WatchMemoryAsync(CancellationToken token)
    {
        var failed = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Run(() => _memory.SynchronizeAsync(token), token);
                    if (failed) _log.Write("memory.ready", "Local journal synchronization recovered.");
                    failed = false;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (!failed) _log.Write("memory.unavailable", ex.GetType().Name + "; saved file preserved; current journal reads remain available.");
                    failed = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task WatchCombatAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(4));
                    await Task.Run(() => _combat.ReadAsync(deadline.Token), deadline.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
                { /* Optional telemetry must never interrupt the normal voice connection. */ }
                Ui(() => {
                    if (_speaker.HasUnplayedAudio && !_setupOpen && _recordOwner is null &&
                        !_combatReplies.CanPlay(_voice.CurrentTurn, _combat.Latest, DateTimeOffset.UtcNow))
                        DiscardStaleCombatReply(_voice, _voice.CurrentTurn);
                    CheckCombatCallout();
                });
                await Task.Delay(750, token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private bool QuietForCallout() => !_closed && !_setupOpen && _recordOwner is null && !_busy &&
        !_speaker.HasUnplayedAudio && DateTimeOffset.UtcNow >= _calloutQuietUntil;
    private void CheckCombatCallout()
    {
        var allowed = _settings.Value.CombatCalloutsEnabled && _settings.Value.AllowCloud;
        var callout = _calloutPolicy.Observe(_combat.Latest, DateTimeOffset.UtcNow, allowed,
            QuietForCallout() && !_callouts.IsActive);
        if (!allowed) { _callouts.Cancel(); return; }
        if (callout is null) return;
        try
        {
            var voice = _settings.Value.Voice;
            _ = _callouts.TryPlayAsync(callout, voice, _settings.ReadKey(), allowed, () =>
                QuietForCallout() && _settings.Value.CombatCalloutsEnabled && _settings.Value.AllowCloud &&
                _settings.Value.Voice == voice && CombatCalloutPolicy.IsRelevant(callout, _combat.Latest, DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { _callouts.Cancel(); } // No fallback voice or error announcement for an optional callout.
    }
    private void DiscardStaleCombatReply(IVoiceSession session, int turn)
    {
        if (session != _voice || turn != session.CurrentTurn || _discardingCombatTurn == turn) return;
        _discardingCombatTurn = turn;
        _log.Write("combat.reply_discarded", "Cockpit evidence changed or aged before playback completed.", turn);
        _ = StopConversationTurnAsync(); // Stops queued audio and invalidates the turn synchronously before awaiting.
        StatusText.Text = "Combat changed · ask again";
        SetVoiceNotice(StatusText.Text); // Also visible in minimal mode, without an unsolicited voice or sound.
    }
    private void UpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_updatesWindow is not null) { _updatesWindow.Activate(); return; }
        _updatesWindow = new UpdatesWindow(_settings, _updates, RestartForUpdateAsync, _lifetime.Token) { Owner = this };
        _updatesWindow.Closed += (_, _) => _updatesWindow = null;
        _updatesWindow.Show();
    }
    private async Task RestartForUpdateAsync()
    {
        _setupOpen = true; // Block new PTT before stopping current speech/input.
        try
        {
            await StopConversationTurnAsync();
            await EndRecordingAsync(_recordOwner, true);
            _updates.ScheduleApplyAndRestart();
            Application.Current.Shutdown();
        }
        finally { _setupOpen = false; }
    }
    private void CreateVoiceSession()
    {
        _callouts.Cancel(); _calloutPolicy.Reset(); // Fixed clips are keyed by voice and contain no conversation or personal data.
        _combatReplies.Clear(); _discardingCombatTurn = -1;
        _savedScreenshots.Clear();
        _pasteTurns.Clear(); // Turn numbers restart when settings replace the voice session.
        var session = new RealtimeVoiceSession(() => new RealtimeWebSocketTransport(),
            _settings.ReadKey, () => _settings.Value.Voice, RunToolAsync, _log);
        _voice = session;
        session.AudioReceived += frame => Ui(() => {
            if (session != _voice || frame.Turn != session.CurrentTurn || _recordOwner is not null || _setupOpen) return;
            _callouts.Cancel();
            _calloutQuietUntil = DateTimeOffset.UtcNow.AddSeconds(3);
            if (!_combatReplies.CanPlay(frame.Turn, _combat.Latest, DateTimeOffset.UtcNow))
            { DiscardStaleCombatReply(session, frame.Turn); return; }
            _thinkingCue.Reset(); // Silence the cue before queuing any reply, not after the next UI tick.
            try { _speaker.Append(frame.ItemId, frame.Pcm); }
            catch (Exception ex) { ShowError(ex); _ = StopConversationTurnAsync(); }
        });
        session.StatusChanged += status => Ui(() => {
            if (session != _voice || status.Turn != session.CurrentTurn || _recordOwner is not null || _setupOpen) return;
            _busy = status.Busy;
            if (_busy) _callouts.Cancel();
            var speaking = _speaker.HasUnplayedAudio;
            UpdateThinkingSound(speaking);
            StatusText.Text = speaking ? "Speaking" : status.Text;
            SetVoiceNotice(status.Attention ? status.Text : null);
        });
        session.Faulted += failure => Ui(() => {
            if (session != _voice || failure.Turn != session.CurrentTurn || _recordOwner is not null || _setupOpen) return;
            _speaker.Stop(); _busy = false; ShowError(new InvalidOperationException(failure.Message));
        });
    }
    private async void InitializeInputs(object? sender, EventArgs e)
    {
        try
        {
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(WindowMessage);
            _controller.Start(new WindowInteropHelper(this).Handle);
            await ConfigureKeyboardAsync();
            if (_settings.Value.AllowCloud)
            {
                await _voice.ConnectAsync();
                if (_recordOwner is null && !_busy) StatusText.Text = "Conversation ready";
            }
        }
        catch (Exception ex) { if (!_closed) ShowError(ex); }
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WindowSizing.EnterSizeMove) _resizeStartScale = CurrentWindowScale;
        else if (message == WindowSizing.Sizing && lParam != IntPtr.Zero && !_settings.Value.MinimalMode)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bounds = WindowSizing.Constrain(Marshal.PtrToStructure<WindowSizing.NativeRect>(lParam),
                wParam.ToInt32(), dpi.DpiScaleX, dpi.DpiScaleY, _resizeStartScale);
            Marshal.StructureToPtr(bounds, lParam, false);
            handled = true;
            return new IntPtr(1);
        }
        else if (message == WindowSizing.ExitSizeMove) SaveWindowScale(CurrentWindowScale);
        _controller.ProcessWindowMessage(message, wParam, lParam);
        return IntPtr.Zero;
    }
    private async Task ConfigureKeyboardAsync()
    {
        if (_keyboard is not null) { await _keyboard.StopAsync(); _keyboard.Dispose(); }
        _keyboard = new GlobalKeyboardPushToTalkTrigger(_settings.Value.KeyboardKey);
        _keyboard.Pressed += (_, _) => Ui(() => _ = BeginRecordingAsync("keyboard"));
        _keyboard.Released += (_, _) => Ui(() => _ = EndRecordingAsync("keyboard"));
        await _keyboard.StartAsync(); UpdateHint();
    }
    private void ControllerButton(object? sender, ControllerButtonEvent e) => Ui(() => {
        if (_setupOpen) { if (e.IsDown) _setup?.ReceiveButton(e); return; }
        if (e.DeviceId != _settings.Value.ControllerId || e.ButtonIndex != _settings.Value.ControllerButton) return;
        var owner = "hotas:" + e.DeviceId;
        if (e.IsDown) _ = BeginRecordingAsync(owner); else _ = EndRecordingAsync(owner);
    });
    private async Task BeginRecordingAsync(string owner)
    {
        await _recordGate.WaitAsync();
        try
        {
            if (_closed || _setupOpen || _recordOwner is not null) return;
            if (!_settings.Value.AllowCloud) { StatusText.Text = "Open Setup to allow cloud voice"; return; }
            if (string.IsNullOrWhiteSpace(_settings.ReadKey())) throw new InvalidOperationException("Add an API key in Setup before talking.");
            _uiTurn++; _busy = false;
            _callouts.Cancel();
            _calloutQuietUntil = DateTimeOffset.UtcNow.AddSeconds(3);
            _combatReplies.Clear();
            SetVoiceNotice(null);
            StopFailureSound();
            _thinkingCue.Reset(); // Must stop before opening the microphone.
            var heard = _speaker.Stop();
            _audio.Start(_settings.Value.Microphone);
            _recordOwner = owner; _recordStarted = DateTimeOffset.UtcNow;
            _log.Write("ptt.down", owner.StartsWith("hotas:") ? "HOTAS" : owner);
            StatusText.Text = "Listening";
            // Do not block button-up behind network connection work.
            _beginTurn = _voice.BeginTurnAsync(heard);
            _ = _beginTurn.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) { if (!_closed) ShowError(ex); }
        finally { _recordGate.Release(); }
    }
    private async Task EndRecordingAsync(string? owner, bool discard = false)
    {
        await _recordGate.WaitAsync();
        try
        {
            if (owner is null || _recordOwner != owner) return;
            _calloutQuietUntil = DateTimeOffset.UtcNow.AddSeconds(3);
            _recordOwner = null;
            var uiTurn = _uiTurn;
            var duration = DateTimeOffset.UtcNow - _recordStarted;
            var wav = await _audio.StopAsync();
            _log.Write("ptt.up", $"Duration {(int)duration.TotalMilliseconds} ms; discarded={discard}");
            if (_closed || discard) return;
            if (duration.TotalMilliseconds < 250 || wav.Length < 4800)
            { StatusText.Text = "Hold the button while you talk"; return; }
            if (_beginTurn is null) return;
            using var memory = new MemoryStream(wav);
            using var reader = new WaveFileReader(memory);
            var pcm = new byte[checked((int)reader.Length)];
            var read = reader.Read(pcm, 0, pcm.Length);
            if (read != pcm.Length) throw new InvalidOperationException("The recording was incomplete. Please try again.");
            _busy = true; StatusText.Text = "Thinking";
            UpdateThinkingSound(_speaker.HasUnplayedAudio);
            _ = SubmitRecordingAsync(_beginTurn, pcm, uiTurn);
        }
        catch (Exception ex) { if (!_closed) ShowError(ex); }
        finally { _recordGate.Release(); }
    }
    private async Task SubmitRecordingAsync(Task<int> begin, byte[] pcm, int uiTurn)
    {
        try
        {
            var voiceTurn = await begin;
            if (_closed || uiTurn != _uiTurn) return;
            await _voice.SubmitAudioAsync(voiceTurn, pcm);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && uiTurn == _uiTurn) { _busy = false; ShowError(ex); } }
    }
    private async Task<string> RunToolAsync(VoiceToolCall call, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (call.Name == "get_combat_context")
        {
            var voice = _voice;
            var kind = call.Combat ?? throw new InvalidOperationException("Choose live or review combat context.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            CombatContext combatContext;
            try { combatContext = await Task.Run(() => _combat.ReadAsync(deadline.Token), deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new InvalidOperationException("Combat data is still catching up. Don't reuse older cockpit readings."); }
            await Dispatcher.InvokeAsync(() => {
                token.ThrowIfCancellationRequested();
                if (_closed || voice != _voice || call.Turn != voice.CurrentTurn) throw new OperationCanceledException();
                _combatReplies.Arm(call.Turn, combatContext, kind);
            });
            _log.Write("combat.context", kind + "; fresh cockpit=" + combatContext.LiveAvailable, call.Turn);
            return CombatContextSource.ToolResult(combatContext, kind);
        }
        if (call.Name == "paste_destination")
        {
            var voice = _voice;
            var utterance = await voice.GetUserTranscriptAsync(call.Turn, token);
            token.ThrowIfCancellationRequested();
            if (_closed || voice != _voice || call.Turn != voice.CurrentTurn) throw new OperationCanceledException();
            foreach (var old in _pasteTurns.Keys.Where(t => t < call.Turn - 10)) _pasteTurns.TryRemove(old, out _);
            var once = _pasteTurns.GetOrAdd(call.Turn, _ => new Lazy<Task<DestinationPasteResult>>(() =>
                new DestinationPasteService(_copiedDestination, new WindowsDestinationPaste(Dispatcher,
                    () => _settings.Value.JournalDirectory,
                    () => !_closed && voice == _voice && call.Turn == voice.CurrentTurn && !_setupOpen && _recordOwner is null))
                    .PasteAsync(utterance, token)));
            var result = await once.Value; // One physical gesture per user turn, even if the model repeats the call.
            _log.Write("clipboard.paste", result.Status, call.Turn); // Never read/log unrelated clipboard contents.
            return JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        if (call.Name == "lookup_game_records")
        {
            // These lookups still work if persistent memory is disabled or its checkpoint is unavailable.
            async Task<string?> Scope()
            {
                try { return await _memory.GetScopeAsync(token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
                { return null; }
            }
            var scope = await Scope();
            var records = await Task.Run(() => _records.LookupAsync(call.Records ??
                throw new InvalidOperationException("Choose a game record topic."), token, call.RecordShipId), token);
            token.ThrowIfCancellationRequested();
            if (_closed || call.Turn != _voice.CurrentTurn) throw new OperationCanceledException();
            var currentScope = await Scope();
            if (scope is not null && currentScope is not null && scope != currentScope)
                throw new InvalidOperationException("The commander changed during this lookup. Please ask again.");
            token.ThrowIfCancellationRequested();
            if (scope is not null) _lastRecords = (call.Turn, scope, records.Json);
            _log.Write("game.records", records.Availability, call.Turn); // No raw records or identifiers in logs.
            return records.Json;
        }
        if (call.Name == "update_memory")
        {
            var scope = await _memory.GetScopeAsync(token);
            var utterance = await _voice.GetUserTranscriptAsync(call.Turn, token);
            if (_closed || call.Turn != _voice.CurrentTurn) throw new OperationCanceledException();
            var edited = await _memory.EditAsync(call.Memory ?? throw new InvalidOperationException("No memory change specified."), utterance, token, scope);
            return JsonSerializer.Serialize(edited, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        if (call.Name == "read_memory")
        {
            if (!_settings.Value.MemoryEnabled) throw new InvalidOperationException("Persistent memory is off in Setup.");
            return (await Task.Run(() => _memory.RecallAsync(call.Question, token), token)).Json;
        }
        var clipboard = new WindowsSystemClipboard(Dispatcher, () => !_closed && call.Turn == _voice.CurrentTurn && !_setupOpen && _recordOwner is null, _copiedDestination);
        var copying = new DestinationCopyService(_systems, clipboard);
        if (call.Name == "copy_system")
        {
            var result = await copying.CopySystemAsync(call.System, token);
            if (!result.ClipboardCopied) _copiedDestination.Clear(); // Don't paste an older destination after a failed new copy.
            _log.Write("clipboard.result", JsonSerializer.Serialize(result), call.Turn);
            return JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        if (call.Name == "save_screenshot")
        {
            var utterance = await _voice.GetUserTranscriptAsync(call.Turn, token);
            if (!ScreenRequestPolicy.CanSave(utterance))
                throw new InvalidOperationException("Ask me to take or save a screenshot of Elite before I save an image.");
            if (!_savedScreenshots.TryGetValue(call.Turn, out var saved))
            {
                saved = await _screens.SaveAsync(utterance, token);
                token.ThrowIfCancellationRequested();
                _savedScreenshots[call.Turn] = saved; // Do not save duplicates if the voice response retries this tool.
                foreach (var old in _savedScreenshots.Keys.Where(t => t < call.Turn - 12)) _savedScreenshots.TryRemove(old, out _);
            }
            _log.Write("screenshot.saved", saved.FileName, call.Turn); // Never log image bytes or a personal folder path.
            return JsonSerializer.Serialize(new { screenshotSaved = true, fileName = saved.FileName, folder = saved.Folder,
                note = "Saved locally. The image was not uploaded for analysis." });
        }
        if (call.Name is not ("get_game_context" or "research_elite" or "inspect_game"))
            throw new InvalidOperationException("That tool isn't available.");
        GameContext context;
        string? memoryScope = null;
        try
        {
            var query = call.Question.Length > 0 ? call.Question : await _voice.GetUserTranscriptAsync(call.Turn, token);
            context = await Task.Run(() => _memory.RecallAsync(query, token), token);
            memoryScope = context.MemoryScope;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            context = await Task.Run(() => _game.ReadAsync(token), token);
            context = context with { Availability = context.Availability + " Persistent memory unavailable; don't invent saved goals or history." };
        }
        _log.Write("game.context", context.Availability, call.Turn); // Never dump raw journal data.
        if (call.Name == "get_game_context")
            return JsonSerializer.Serialize(new { context.Availability, gameData = JsonSerializer.Deserialize<JsonElement>(context.Json) });
        var question = call.Question;
        var actualUtterance = await _voice.GetUserTranscriptAsync(call.Turn, token);
        var visualRequest = ScreenRequestPolicy.CanInspect(actualUtterance);
        if ((call.Name == "inspect_game" || AnswerPolicy.WantsVisualContext(question)) && !visualRequest)
            throw new InvalidOperationException("Ask about what is visible in Elite, for example 'explain this warning', before I inspect the game.");
        if (visualRequest && !AnswerPolicy.WantsVisualContext(question)) question = "On my screen: " + question;
        var history = _voice.History.Where(t => t.Answer.Length > 0).ToArray();
        if (_lastRecords is { } local && local.Turn == call.Turn && local.Scope == context.MemoryScope)
            context = context with { Json = JsonSerializer.Serialize(new {
                gameContext = JsonSerializer.Deserialize<JsonElement>(context.Json),
                onDemandRecords = JsonSerializer.Deserialize<JsonElement>(local.Json) }) };
        var answer = await _research.AnswerAsync(question, context, history, async ct => {
            if (!visualRequest) throw new InvalidOperationException("No screenshot was requested by the user.");
            var image = await _screens.InspectAsync(actualUtterance, ct);
            _log.Write("screen.inspected", "Game-only image captured in memory; not saved.", call.Turn);
            return image;
        }, token);
        token.ThrowIfCancellationRequested();
        var copy = await copying.CopyResearchAsync(answer, token);
        if (!copy.ClipboardCopied && copy.ClipboardStatus != "no_destination") _copiedDestination.Clear();
        if (memoryScope is not null && !visualRequest)
        {
            try { await _memory.RememberResearchAsync(question, answer, memoryScope, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            { _log.Write("memory.research_not_saved", ex.GetType().Name, call.Turn); }
        }
        _log.Write("clipboard.result", JsonSerializer.Serialize(copy), call.Turn);
        await Dispatcher.InvokeAsync(() => {
            token.ThrowIfCancellationRequested();
            if (_closed || call.Turn != _voice.CurrentTurn) throw new OperationCanceledException();
            _sources = answer.Sources;
        });
        _log.Write("research.result", JsonSerializer.Serialize(new { copied = copy.ClipboardCopied, system = copy.CopiedSystem,
            clipboardStatus = copy.ClipboardStatus, sources = answer.Sources.Select(s => s.Url) }), call.Turn);
        return JsonSerializer.Serialize(new { answer = answer.Speech, clipboardCopied = copy.ClipboardCopied,
            copiedSystem = copy.CopiedSystem, clipboardStatus = copy.ClipboardStatus, clipboardNote = copy.ClipboardNote,
            sources = answer.Sources });
    }
    private async Task StopConversationTurnAsync()
    {
        _callouts.Cancel();
        _calloutQuietUntil = DateTimeOffset.UtcNow.AddSeconds(3);
        _combatReplies.Clear();
        _uiTurn++; _busy = false;
        SetVoiceNotice(null);
        StopFailureSound();
        _thinkingCue.Reset();
        var heard = _speaker.Stop();
        try { await _voice.InterruptAsync(heard); }
        catch (Exception) { }
    }
    private void ShowError(Exception ex)
    {
        if (_closed) return;
        _busy = false;
        _thinkingCue.Reset();
        _log.Write("app.error", ex.GetType().Name + ": " + ex.Message, _voice.CurrentTurn);
        var message = ex switch {
            InvalidOperationException => ex.Message,
            HttpRequestException => "I couldn't connect. Check your internet connection and try again.",
            _ => "I couldn't connect the conversation. Check Setup and try again."
        };
        StatusText.Text = message;
        SetVoiceNotice(message);
        if (_settings.Value.ThinkingSoundEnabled && _recordOwner is null && !_setupOpen)
        {
            try { _failureSound.PlayPulse(); }
            catch (Exception) { /* An optional notification must not prevent voice recovery. */ }
        }
        // If the voice service is unavailable, keep the error visible. Never switch to a different speaker.
    }
    private void StopFailureSound()
    {
        try { _failureSound.Stop(); } catch (Exception) { }
    }
    private void SetVoiceNotice(string? message)
    {
        _voiceNotice = message;
        WindowAppearance.ApplyNotice(VoiceNotice, VoiceNoticeText, _settings.Value.MinimalMode, message);
    }
    private void Animate(object? sender, EventArgs e)
    {
        _phase += .12;
        var speaking = _speaker.HasUnplayedAudio || _calloutAudio.HasUnplayedAudio;
        UpdateThinkingSound(speaking);
        var active = _recordOwner is not null || speaking;
        for (var i = 0; i < _bars.Count; i++)
        {
            var envelope = Math.Sin(Math.PI * (i + 1) / (_bars.Count + 1));
            var ripple = .35 + .65 * Math.Abs(Math.Sin(i * .62 + _phase));
            var height = active ? 3 + Math.Min(1, _level * 3) * 62 * envelope * ripple
                : _busy ? 3 + 12 * envelope * (.5 + .5 * Math.Sin(i * .2 - _phase)) : 3;
            _bars[i].Height += (height - _bars[i].Height) * .4;
            _bars[i].Opacity = active || _busy ? 1 : .4;
        }
        if (_recordOwner is null && !_busy && !speaking && StatusText.Text == "Speaking") StatusText.Text = "Conversation ready";
        if (_recordOwner is { } owner && DateTimeOffset.UtcNow - _recordStarted > TimeSpan.FromSeconds(60)) _ = EndRecordingAsync(owner);
    }
    private void UpdateHint()
    {
        var key = KeyInterop.KeyFromVirtualKey(_settings.Value.KeyboardKey);
        var hotas = _settings.Value.ControllerId is not null ? " / HOTAS" : "";
        HintText.Text = $"Hold {key}{hotas} · Talk naturally · AI voice";
    }
    private void UpdateThinkingSound(bool speaking) => _thinkingCue.Update(_uiTurn, _busy, speaking,
        _settings.Value.ThinkingSoundEnabled, _closed || _setupOpen || _recordOwner is not null || !_settings.Value.AllowCloud);
    private void MenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        MoreMenu.PlacementTarget = button;
        MoreMenu.Placement = PlacementMode.Bottom;
        MoreMenu.HorizontalOffset = button.TransformToAncestor(this).TransformBounds(new Rect(button.RenderSize)).Width - MoreMenu.Width;
        MoreMenu.VerticalOffset = 0;
        MoreMenu.IsOpen = true;
    }
    private void MoreMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // The same menu is reachable from the waveform when all window chrome is hidden.
        MoreMenu.PlacementTarget = RootSurface;
        MoreMenu.Placement = PlacementMode.MousePoint;
        MoreMenu.HorizontalOffset = MoreMenu.VerticalOffset = 0;
    }
    private double CurrentWindowScale => WindowSizing.ScaleForSize(ActualWidth > 0 ? ActualWidth : Width,
        ActualHeight > 0 ? ActualHeight : Height);
    private void MoreMenuOpened(object sender, RoutedEventArgs e)
    {
        var scale = CurrentWindowScale;
        SmallerMenu.IsEnabled = scale > WindowSizing.MinimumScale + .001;
        LargerMenu.IsEnabled = scale < WindowSizing.MaximumScale - .001;
        ResetSizeMenu.IsEnabled = Math.Abs(scale - 1) > .001;
    }
    private void ApplyWindowScale(double scale, bool persist = true)
    {
        scale = WindowSizing.ClampScale(scale);
        var size = WindowSizing.SizeForScale(scale);
        Width = size.Width; Height = size.Height;
        if (persist) SaveWindowScale(scale);
    }
    private void SaveWindowScale(double scale)
    {
        scale = Math.Round(WindowSizing.ClampScale(scale), 4);
        var previous = _settings.Value.WindowScale;
        if (Math.Abs(scale - previous) < .0001) return;
        _settings.Value.WindowScale = scale;
        try { _settings.Save(); }
        catch (Exception)
        {
            _settings.Value.WindowScale = previous;
            StatusText.Text = "The window size could not be saved. Check access to the settings folder.";
        }
    }
    private void SmallerClick(object sender, RoutedEventArgs e) => ApplyWindowScale(CurrentWindowScale - .1);
    private void LargerClick(object sender, RoutedEventArgs e) => ApplyWindowScale(CurrentWindowScale + .1);
    private void ResetSizeClick(object sender, RoutedEventArgs e) => ApplyWindowScale(1);
    private void ApplyAppearance()
    {
        WindowAppearance.Apply(this, RootSurface, HeaderArea, StatusArea, WaveformArea,
            _settings.Value.MinimalMode, _settings.Value.KeepOnTop);
        MinimalMenu.IsChecked = _settings.Value.MinimalMode;
        TopmostMenu.IsChecked = _settings.Value.KeepOnTop;
        ThinkingSoundMenu.IsChecked = _settings.Value.ThinkingSoundEnabled;
        SetVoiceNotice(_voiceNotice);
    }
    private void SaveAppearance(bool minimal, bool keepOnTop)
    {
        var previous = (_settings.Value.MinimalMode, _settings.Value.KeepOnTop);
        _settings.Value.MinimalMode = minimal;
        _settings.Value.KeepOnTop = keepOnTop;
        try { _settings.Save(); }
        catch (Exception)
        {
            (_settings.Value.MinimalMode, _settings.Value.KeepOnTop) = previous;
            StatusText.Text = "Appearance could not be saved. Check access to the settings folder.";
        }
        ApplyAppearance(); // No voice reconnection or changes to input bindings.
    }
    private void MinimalClick(object sender, RoutedEventArgs e) =>
        SaveAppearance(MinimalMenu.IsChecked, _settings.Value.KeepOnTop);
    private void TopmostClick(object sender, RoutedEventArgs e) =>
        SaveAppearance(_settings.Value.MinimalMode, TopmostMenu.IsChecked);
    private void ThinkingSoundClick(object sender, RoutedEventArgs e)
    {
        var previous = _settings.Value.ThinkingSoundEnabled;
        _settings.Value.ThinkingSoundEnabled = ThinkingSoundMenu.IsChecked;
        _thinkingCue.Reset();
        StopFailureSound();
        try { _settings.Save(); }
        catch (Exception)
        {
            _settings.Value.ThinkingSoundEnabled = previous;
            StatusText.Text = "Thinking sound preference could not be saved.";
        }
        ThinkingSoundMenu.IsChecked = _settings.Value.ThinkingSoundEnabled;
        // No reconnect or changes to voice/input. Enabling during a wait starts a fresh ten-second delay.
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MinimalMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Intercept before the waveform's mouse PTT handler: dragging must never open the microphone.
        if (_settings.Value.MinimalMode) DragTitle(sender, e);
    }
    private void DragTitle(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
    private async void SetupClick(object sender, RoutedEventArgs e) => await OpenSetupAsync();
    private async Task OpenSetupAsync(SetupMode mode = SetupMode.Settings)
    {
        if (_setupOpen) return;
        _setupOpen = true;
        try
        {
            await StopConversationTurnAsync(); await EndRecordingAsync(_recordOwner, true);
            _setup = new SetupWindow(_settings, _log, mode: mode, sessionLogError: () => _memory.SessionLogError) { Owner = this };
            if (_setup.ShowDialog() == true)
            {
                ApplyAppearance();
                await ConfigureKeyboardAsync();
                await _voice.DisposeAsync(); CreateVoiceSession();
                if (_settings.Value.AllowCloud) await _voice.ConnectAsync();
            }
            StatusText.Text = _settings.Value.AllowCloud ? "Conversation ready" : "Open Setup to get started";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _setup = null; _setupOpen = false; }
    }
    private void SourcesClick(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Sources from the last lookup", FontSize = 21, Margin = new Thickness(0,0,0,12) });
        if (_sources.Count == 0) panel.Children.Add(new TextBlock { Text = "No research sources yet." });
        foreach (var source in _sources)
        {
            var label = new TextBlock { Text = source.Title + "\n" + source.Url, TextWrapping = TextWrapping.Wrap };
            label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            var button = new Button { Content = label,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10), Margin = new Thickness(0,0,0,8) };
            button.Click += (_, _) => {
                if (!AnswerPolicy.IsPublicLink(source.Url)) return;
                try { Process.Start(new ProcessStartInfo(source.Url) { UseShellExecute = true }); }
                catch (Exception ex) { ShowError(ex); }
            };
            panel.Children.Add(button);
        }
        new Window { Owner = this, Title = "Alexandra · Sources", Width = 600, Height = 450,
            Background = (Brush)FindResource("WindowBrush"), Foreground = Foreground, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.ShowDialog();
    }
    private async void StopClick(object sender, RoutedEventArgs e)
    { await StopConversationTurnAsync(); await EndRecordingAsync(_recordOwner, true); StatusText.Text = "Conversation ready"; }
    private async void ClearClick(object sender, RoutedEventArgs e)
    {
        await StopConversationTurnAsync(); await EndRecordingAsync(_recordOwner, true);
        await _voice.DisposeAsync(); CreateVoiceSession(); _sources = [];
        StatusText.Text = "Conversation cleared · hold to talk";
    }
    private void LogsClick(object sender, RoutedEventArgs e) => new DiagnosticsWindow(_log) { Owner = this }.ShowDialog();
    private void ScreenshotsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_screenshotFolder)) { StatusText.Text = "No saved screenshots yet · ask me to take one"; return; }
            Process.Start(new ProcessStartInfo(_screenshotFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ExitClick(object sender, RoutedEventArgs e) => Close();
    private void VoiceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Value.MinimalMode) return;
        ((UIElement)sender).CaptureMouse(); _ = BeginRecordingAsync("mouse"); e.Handled = true;
    }
    private void VoiceMouseUp(object sender, MouseButtonEventArgs e)
    { ((UIElement)sender).ReleaseMouseCapture(); _ = EndRecordingAsync("mouse"); e.Handled = true; }
    private void VoiceMouseLost(object sender, MouseEventArgs e) => _ = EndRecordingAsync("mouse");
    private async void OnClosed(object? sender, EventArgs e)
    {
        _closed = true; _uiTurn++; _timer.Stop(); _windowSource?.RemoveHook(WindowMessage);
        _lifetime.Cancel();
        _callouts.Dispose();
        _thinkingCue.Dispose();
        try { _failureSound.Dispose(); } catch (Exception) { }
        _log.Write("app.stop", "Conversation closed");
        _keyboard?.Dispose(); _controller.Dispose(); _audio.Dispose(); _speaker.Dispose();
        await _voice.DisposeAsync(); _http.Dispose();
    }
}

