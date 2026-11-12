using System.Text.Json;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

// One persistent speech-to-speech conversation. Only explicit PTT submissions create user turns.
public sealed class RealtimeVoiceSession(
    Func<IRealtimeTransport> transportFactory, Func<string?> key, Func<string> voice,
    Func<VoiceToolCall, CancellationToken, Task<string>> tool, IDiagnosticLog? diagnostics = null,
    TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null) : IVoiceSession
{
    private readonly IDiagnosticLog _log = diagnostics ?? NullDiagnosticLog.Instance;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _connect = new(1);
    private readonly SemaphoreSlim _sequence = new(1);
    private readonly Dictionary<string, int> _responseTurns = [];
    private readonly Dictionary<string, int> _inputTurns = [];
    private readonly Dictionary<int, TaskCompletionSource<string>> _transcripts = [];
    private readonly Dictionary<string, int> _generatedBytes = [];
    private readonly Queue<int> _commits = [];
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, int> _toolItems = [];
    private readonly Dictionary<string, string> _pendingPrunes = [];
    private readonly Dictionary<string, int> _responseRequests = [];
    private readonly RealtimeCooldown _cooldown = new(clock);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private string? _pendingResponseEvent;
    private int _rateRetries;
    private double _rateWaitSeconds;
    private readonly RealtimeReplyBuffer _replies = new();
    private IRealtimeTransport? _transport;
    private CancellationTokenSource? _connection;
    private CancellationTokenSource _turn = new();
    private TaskCompletionSource? _configured;
    private Task? _receiver;
    private DateTimeOffset _connectedAt;
    private int _generation, _toolCount, _retryCount;
    private bool _responsePending, _disposed, _responseAudioEmitted;
    public event Action<VoiceFrame>? AudioReceived;
    public event Action<VoiceStatus>? StatusChanged;
    public event Action<VoiceFault>? Faulted;
    public event Action<int, string, string>? TranscriptReceived;
    public int CurrentTurn { get { lock (_sync) return _generation; } }
    public IReadOnlyList<Turn> History
    {
        get
        {
            lock (_sync) return _entries.GroupBy(e => e.Turn).OrderBy(g => g.Key).Select(g =>
                new Turn(string.Join(" ", g.Where(e => e.Role == "user").Select(e => e.Text)),
                    string.Join(" ", g.Where(e => e.Role == "assistant").Select(e => e.Text))))
                .Where(t => t.Question.Length > 0).TakeLast(12).ToArray();
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connect.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealtimeVoiceSession));
            if (_transport?.IsConnected == true && _configured?.Task.IsCompletedSuccessfully == true &&
                DateTimeOffset.UtcNow - _connectedAt < TimeSpan.FromMinutes(55)) return;
            if (_connection is not null) { _connection.Cancel(); _connection.Dispose(); }
            if (_transport is not null) await _transport.DisposeAsync();
            var apiKey = key();
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Add an API key in Setup before talking.");
            _connection = new CancellationTokenSource();
            _transport = transportFactory();
            _configured = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) {
                _responseTurns.Clear(); _inputTurns.Clear(); _commits.Clear(); _generatedBytes.Clear(); _responsePending = false;
                _toolItems.Clear(); _pendingPrunes.Clear(); _responseRequests.Clear(); _pendingResponseEvent = null;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            await _transport.ConnectAsync(RealtimeProtocol.Model, apiKey, timeout.Token);
            _receiver = ReceiveLoopAsync(_transport, _connection.Token);
            await SendAsync(RealtimeProtocol.SessionUpdate(voice()), timeout.Token);
            await _configured.Task.WaitAsync(timeout.Token);
            // Reconnects keep a bounded text history; raw audio is not retained.
            Entry[] history;
            lock (_sync) history = _entries.TakeLast(24).ToArray();
            foreach (var entry in history)
                await SendAsync(new { type = "conversation.item.create", item = new {
                    type = "message", role = entry.Role, content = new[] { new {
                        type = entry.Role == "user" ? "input_text" : "output_text", text = entry.Text
                    } }
                } }, timeout.Token);
            _connectedAt = DateTimeOffset.UtcNow;
            _log.Write("voice.connected", RealtimeProtocol.Model + " / " + RealtimeProtocol.NormalizeVoice(voice()));
            _log.Write("voice.context_policy", "16000 conversation-token limit; retired tool results removed between turns.");
        }
        catch
        {
            _connection?.Cancel();
            if (_transport is not null) await _transport.DisposeAsync();
            throw;
        }
        finally { _connect.Release(); }
    }
    public async Task<int> BeginTurnAsync(IReadOnlyDictionary<string, int> playedMilliseconds)
    {
        await InterruptAsync(playedMilliseconds);
        var turn = CurrentTurn;
        await ConnectAsync();
        lock (_sync)
        {
            if (turn != _generation) throw new OperationCanceledException();
            _transcripts[turn] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _toolCount = 0;
            _retryCount = 0;
            _rateRetries = 0;
            _rateWaitSeconds = 0;
        }
        return turn;
    }
    public async Task InterruptAsync(IReadOnlyDictionary<string, int> playedMilliseconds)
    {
        _log.Write("voice.interrupt", "New push-to-talk, stop, or setup; unheard speech and previous work cancelled.", CurrentTurn);
        bool cancel;
        KeyValuePair<string, int>[] generated;
        lock (_sync)
        {
            _generation++;
            _replies.Reset();
            _turn.Cancel(); _turn.Dispose(); _turn = new();
            cancel = _responsePending;
            _responsePending = false;
            _pendingResponseEvent = null;
            generated = _generatedBytes.ToArray();
            _generatedBytes.Clear();
            foreach (var item in generated)
            {
                var played = playedMilliseconds.GetValueOrDefault(item.Key);
                if (played * 48 < item.Value) _entries.RemoveAll(e => e.ItemId == item.Key);
            }
        }
        await _sequence.WaitAsync();
        try
        {
            if (_transport?.IsConnected != true) return;
            if (cancel) await SendAsync(new { type = "response.cancel" }, CancellationToken.None);
            foreach (var item in generated)
            {
                var played = Math.Clamp(playedMilliseconds.GetValueOrDefault(item.Key), 0, item.Value / 48);
                if (played * 48 < item.Value)
                    await SendAsync(new { type = "conversation.item.truncate", item_id = item.Key,
                        content_index = 0, audio_end_ms = played }, CancellationToken.None);
            }
            await SendAsync(new { type = "input_audio_buffer.clear" }, CancellationToken.None);
            await PruneToolContextAsync(CurrentTurn, CancellationToken.None);
        }
        finally { _sequence.Release(); }
    }
    public async Task SubmitAudioAsync(int turn, byte[] pcm)
    {
        if (pcm.Length < 4800 || pcm.Length > 24000 * 2 * 61 || pcm.Length % 2 != 0)
            throw new InvalidOperationException("Please hold push-to-talk while speaking.");
        var token = Token(turn);
        await _sequence.WaitAsync(token);
        try
        {
            for (var offset = 0; offset < pcm.Length; offset += 24000)
                await SendAsync(new { type = "input_audio_buffer.append",
                    audio = Convert.ToBase64String(pcm, offset, Math.Min(24000, pcm.Length - offset)) }, token);
            lock (_sync) _commits.Enqueue(turn);
            _log.Write("voice.submit", $"Audio duration {pcm.Length / 48} ms (audio not logged)", turn);
            await SendAsync(new { type = "input_audio_buffer.commit" }, token);
            await RespondAsync(turn, token);
        }
        finally { _sequence.Release(); }
    }
    // Text input is useful for bounded protocol smoke tests; the app's main interface remains voice-only.
    public async Task SubmitTextAsync(int turn, string text)
    {
        var token = Token(turn);
        await _sequence.WaitAsync(token);
        try
        {
            lock (_sync) { _entries.Add(new(turn, "user", "text-" + turn, text)); _transcripts[turn].TrySetResult(text); }
            _log.Write("transcript.user", text, turn);
            await SendAsync(new { type = "conversation.item.create", item = new {
                type = "message", role = "user", content = new[] { new { type = "input_text", text } }
            } }, token);
            await RespondAsync(turn, token);
        }
        finally { _sequence.Release(); }
    }
    public async Task<string> GetUserTranscriptAsync(int turn, CancellationToken cancellationToken)
    {
        Task<string>? task;
        lock (_sync) task = _transcripts.GetValueOrDefault(turn)?.Task;
        if (task is null) return "";
        try { return await task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken); }
        catch (TimeoutException) { return ""; }
    }
    private CancellationToken Token(int turn)
    {
        lock (_sync)
        {
            if (_disposed || turn != _generation) throw new OperationCanceledException();
            return _turn.Token;
        }
    }
    private bool Current(int turn) { lock (_sync) return !_disposed && turn == _generation && !_turn.IsCancellationRequested; }
    private Task SendAsync(object value, CancellationToken token) =>
        (_transport ?? throw new InvalidOperationException("Voice is not connected.")).SendAsync(JsonSerializer.Serialize(value), token);
    private async Task RespondAsync(int turn, CancellationToken token, int? outputLimit = null)
    {
        token.ThrowIfCancellationRequested();
        await WaitForCooldownAsync(turn, token);
        var eventId = "response_" + Guid.NewGuid().ToString("N");
        lock (_sync) {
            _responsePending = true; _responseAudioEmitted = false; _pendingResponseEvent = eventId;
            _responseRequests[eventId] = turn;
            foreach (var old in _responseRequests.Keys.Take(Math.Max(0, _responseRequests.Count - 64)).ToArray()) _responseRequests.Remove(old);
        }
        StatusChanged?.Invoke(new(turn, "Thinking", true));
        await SendAsync(RealtimeProtocol.CreateResponse(turn, outputLimit, eventId), token);
    }
    private async Task WaitForCooldownAsync(int turn, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            TimeSpan remaining;
            lock (_sync) remaining = _cooldown.Remaining;
            if (remaining <= TimeSpan.Zero) return;
            if (remaining > RealtimeCooldown.MaximumAutomaticWait)
                throw new InvalidOperationException("Voice is temporarily busy. Please try again in a minute.");
            StatusChanged?.Invoke(new(turn, $"Voice busy · retrying in {Math.Ceiling(remaining.TotalSeconds):0}s", true, true));
            await _delay(remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining, token);
        }
    }
    private async Task PruneToolContextAsync(int turn, CancellationToken token)
    {
        string[] retired;
        lock (_sync) retired = _toolItems.Where(x => x.Value < turn).Select(x => x.Key).ToArray();
        foreach (var id in retired)
        {
            var eventId = "prune_" + Guid.NewGuid().ToString("N");
            lock (_sync) {
                _pendingPrunes[eventId] = id;
                foreach (var old in _pendingPrunes.Keys.Take(Math.Max(0, _pendingPrunes.Count - 128)).ToArray()) _pendingPrunes.Remove(old);
            }
            await SendAsync(new { type = "conversation.item.delete", event_id = eventId, item_id = id }, token);
            lock (_sync) _toolItems.Remove(id);
        }
        if (retired.Length > 0) _log.Write("voice.context_pruned", $"Removed {retired.Length} old tool items; spoken dialogue retained.", turn);
    }
    private async Task ReceiveLoopAsync(IRealtimeTransport transport, CancellationToken token)
    {
        try
        {
            await foreach (var json in transport.ReceiveAsync(token))
            {
                using var document = JsonDocument.Parse(json);
                if (token.IsCancellationRequested || !ReferenceEquals(transport, _transport)) return;
                Handle(document.RootElement);
            }
            if (!token.IsCancellationRequested && !_disposed) throw new IOException("Voice connection closed.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (token.IsCancellationRequested || _disposed) return;
            _log.Write("voice.connection_error", ex.GetType().Name + ": " + ex.Message, CurrentTurn);
            _configured?.TrySetException(new InvalidOperationException("Couldn't connect the conversational voice session."));
            lock (_sync) { _turn.Cancel(); _responsePending = false; }
            var errorTurn = CurrentTurn;
            await transport.DisposeAsync(); // A failed receiver must never be reused on the next press.
            Faulted?.Invoke(new(errorTurn, "Voice connection interrupted. Hold push-to-talk to reconnect."));
        }
    }
    private void Handle(JsonElement e)
    {
        var kind = e.GetProperty("type").GetString();
        if (kind == "session.updated") { _configured?.TrySetResult(); return; }
        if (kind == "rate_limits.updated") { lock (_sync) _cooldown.Observe(e); return; }
        if (kind == "conversation.item.deleted") {
            var id = e.GetProperty("item_id").GetString();
            lock (_sync) foreach (var key in _pendingPrunes.Where(x => x.Value == id).Select(x => x.Key).ToArray()) _pendingPrunes.Remove(key);
            return;
        }
        if (kind == "error")
        {
            var error = e.GetProperty("error");
            var code = error.TryGetProperty("code", out var c) ? c.GetString() : "";
            if (code == "response_cancel_not_active") return;
            var eventId = error.TryGetProperty("event_id", out var request) && request.ValueKind == JsonValueKind.String ? request.GetString() : null;
            lock (_sync)
            {
                // Server-side truncation may already have dropped an item we are retiring.
                if (eventId is not null && _pendingPrunes.Remove(eventId) &&
                    code is "conversation_item_not_found" or "item_not_found") return;
                if (eventId is not null && _responseRequests.TryGetValue(eventId, out var requestedTurn))
                {
                    if (!Current(requestedTurn) || eventId != _pendingResponseEvent) return;
                    if (code == "rate_limit_exceeded")
                    {
                        _responsePending = false; _pendingResponseEvent = null;
                        var failure = new RealtimeFailure("failed", null, code,
                            error.TryGetProperty("type", out var errorType) ? errorType.GetString() : null,
                            error.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() : null);
                        RecoverFailure(requestedTurn, failure, []);
                        return;
                    }
                }
            }
            _log.Write("voice.api_error", error.GetRawText(), CurrentTurn);
            var message = code == "rate_limit_exceeded"
                ? "Voice is temporarily busy. Please wait a moment, then try again."
                : code == "insufficient_quota" ? "API credit or billing limit reached. Check the project's billing settings."
                : "The voice session could not complete that turn. Check Setup and try again.";
            _configured?.TrySetException(new InvalidOperationException(message));
            lock (_sync) { _turn.Cancel(); _responsePending = false; }
            Faulted?.Invoke(new(CurrentTurn, message));
            return;
        }
        if (kind == "input_audio_buffer.committed")
        {
            lock (_sync) if (_commits.TryDequeue(out var turn)) _inputTurns[e.GetProperty("item_id").GetString()!] = turn;
            return;
        }
        if (kind == "conversation.item.input_audio_transcription.completed")
        {
            var id = e.GetProperty("item_id").GetString()!;
            var text = e.GetProperty("transcript").GetString() ?? "";
            int turn;
            lock (_sync)
            {
                if (!_inputTurns.TryGetValue(id, out turn)) return;
                _entries.Add(new(turn, "user", id, text));
                _transcripts.GetValueOrDefault(turn)?.TrySetResult(text);
                TrimHistory();
            }
            TranscriptReceived?.Invoke(turn, "user", text);
            _log.Write("transcript.user", text, turn);
            return;
        }
        if (kind == "response.created")
        {
            var response = e.GetProperty("response");
            lock (_sync)
            {
                var turn = RealtimeProtocol.ResponseTurn(response);
                _responseTurns[response.GetProperty("id").GetString()!] = turn;
                if (Current(turn)) { _replies.Reset(); _responseAudioEmitted = false; }
            }
            return;
        }
        var responseId = kind == "response.done" ? e.GetProperty("response").GetProperty("id").GetString()
            : e.TryGetProperty("response_id", out var r) ? r.GetString() : null;
        int epoch;
        lock (_sync) if (responseId is null || !_responseTurns.TryGetValue(responseId, out epoch)) return;
        if (!Current(epoch)) return;
        if (kind is "response.output_item.added" or "response.output_item.done")
        {
            if (e.TryGetProperty("item", out var item))
            {
                if (item.TryGetProperty("phase", out var phase))
                    _log.Write("voice.output_phase", phase.GetString() ?? "unknown", epoch);
                Publish(_replies.Observe(item));
            }
        }
        else if (kind == "response.output_audio.delta")
        {
            var id = e.GetProperty("item_id").GetString()!;
            var bytes = Convert.FromBase64String(e.GetProperty("delta").GetString()!);
            lock (_sync) _generatedBytes[id] = _generatedBytes.GetValueOrDefault(id) + bytes.Length;
            Publish(_replies.Audio(new(epoch, id, bytes)));
        }
        else if (kind == "response.output_audio_transcript.done")
        {
            var id = e.GetProperty("item_id").GetString()!;
            var text = e.GetProperty("transcript").GetString() ?? "";
            Publish(_replies.Transcript(epoch, id, text));
        }
        else if (kind == "response.done")
        {
            var response = e.GetProperty("response");
            lock (_sync) { _responsePending = false; _pendingResponseEvent = null; _responseTurns.Remove(responseId!); }
            var status = response.GetProperty("status").GetString();
            _log.Write("voice.response_done", status ?? "unknown", epoch);
            if (response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                _log.Write("voice.usage", JsonSerializer.Serialize(new {
                    inputTokens = usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var inputCount) ? inputCount : 0,
                    outputTokens = usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var outputCount) ? outputCount : 0
                }), epoch);
            if (status == "cancelled") { _replies.Reset(); return; }
            if (status != "completed")
            {
                _replies.Reset();
                var failure = RealtimeFailure.Parse(response);
                _log.Write("voice.response_failure", failure.Diagnostic(response), epoch);
                var outputs = response.TryGetProperty("output", out var failedOutput) && failedOutput.ValueKind == JsonValueKind.Array
                    ? failedOutput.EnumerateArray().ToArray() : [];
                var incompleteIds = outputs.Where(o => o.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(o => o.GetProperty("id").GetString()!).ToArray();
                RecoverFailure(epoch, failure, incompleteIds);
                return;
            }
            Publish(_replies.Complete(response.GetProperty("output")));
            var calls = response.GetProperty("output").EnumerateArray()
                .Where(o => o.TryGetProperty("type", out var t) && t.GetString() == "function_call").Select(o => o.Clone()).ToArray();
            lock (_sync) foreach (var call in calls)
                if (call.TryGetProperty("id", out var callItemId) && callItemId.ValueKind == JsonValueKind.String)
                    _toolItems[callItemId.GetString()!] = epoch;
            if (calls.Length > 0) _ = RunToolsAsync(epoch, calls);
            else StatusChanged?.Invoke(new(epoch, "Conversation ready", false));
        }
    }
    private void RecoverFailure(int turn, RealtimeFailure failure, string[] incompleteIds)
    {
        bool retry;
        lock (_sync)
        {
            if (!Current(turn)) return;
            _entries.RemoveAll(e => incompleteIds.Contains(e.ItemId));
            if (failure.IsRateLimit)
            {
                var wait = _cooldown.Record(failure, _rateRetries);
                retry = !_responseAudioEmitted && _rateRetries < 2 &&
                    wait <= RealtimeCooldown.MaximumAutomaticWait && _rateWaitSeconds + wait.TotalSeconds <= 90;
                if (retry) { _rateRetries++; _rateWaitSeconds += wait.TotalSeconds; }
                _log.Write("voice.rate_limit", $"Cooldown {Math.Ceiling(wait.TotalSeconds):0}s; automatic retry={retry}.", turn);
            }
            else
            {
                retry = failure.CanRetry(_responseAudioEmitted, _retryCount);
                if (retry) _retryCount++;
            }
        }
        if (retry) _ = RetryResponseAsync(turn, incompleteIds, failure.RetryOutputLimit, failure.IsRateLimit);
        else Faulted?.Invoke(new(turn, failure.UserMessage));
    }
    private void Publish(ReplyOutput output)
    {
        foreach (var frame in output.Audio)
        {
            if (!Current(frame.Turn)) continue;
            lock (_sync) _responseAudioEmitted = true;
            AudioReceived?.Invoke(frame);
            StatusChanged?.Invoke(new(frame.Turn, "Speaking", false));
        }
        foreach (var text in output.Transcripts)
        {
            lock (_sync)
            {
                if (!Current(text.Turn)) continue;
                _entries.Add(new(text.Turn, "assistant", text.ItemId, text.Text)); TrimHistory();
            }
            TranscriptReceived?.Invoke(text.Turn, "assistant", text.Text);
            _log.Write("transcript.assistant_generated", text.Text, text.Turn);
        }
    }
    private async Task RetryResponseAsync(int turn, string[] incompleteIds, int? outputLimit, bool rateLimited = false)
    {
        try
        {
            var token = Token(turn);
            _log.Write("voice.response_retry", "One retry in the existing voice session; no microphone recording or reconnect.", turn);
            if (!rateLimited) {
                StatusChanged?.Invoke(new(turn, "Retrying reply", true, true));
                await _delay(TimeSpan.FromMilliseconds(400), token);
            }
            await _sequence.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                // Remove only this failed response's output. Preserve the user request and completed tool results.
                foreach (var id in incompleteIds)
                {
                    lock (_sync) _generatedBytes.Remove(id);
                    await SendAsync(new { type = "conversation.item.delete", item_id = id }, token);
                }
                await RespondAsync(turn, token, outputLimit);
            }
            finally { _sequence.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Write("voice.retry_error", ex.GetType().Name + ": " + ex.Message, turn);
            if (Current(turn)) Faulted?.Invoke(new(turn, "Couldn't retry the voice reply. Hold push-to-talk to try again."));
        }
    }
    private async Task RunToolsAsync(int turn, JsonElement[] calls)
    {
        try
        {
            var token = Token(turn);
            StatusChanged?.Invoke(new(turn, "Checking", true));
            foreach (var call in calls)
            {
                bool lookupLimit;
                lock (_sync) lookupLimit = ++_toolCount > 6;
                if (lookupLimit)
                {
                    _log.Write("voice.tool_limit", "Stopped after six tool calls; no further response request.", turn);
                    if (Current(turn)) Faulted?.Invoke(new(turn, "This question reached its lookup limit. Please narrow it and try again."));
                    return;
                }
                var name = call.GetProperty("name").GetString() ?? "";
                var callId = call.GetProperty("call_id").GetString();
                string result;
                try
                {
                    if (name is not ("research_elite" or "get_game_context" or "get_combat_context" or "copy_system" or "paste_destination" or "save_screenshot" or "inspect_game" or "read_memory" or "update_memory" or "lookup_game_records")) throw new InvalidOperationException("That tool isn't available.");
                    using var args = JsonDocument.Parse(call.GetProperty("arguments").GetString() ?? "{}");
                    if (name == "paste_destination" && (args.RootElement.ValueKind != JsonValueKind.Object || args.RootElement.EnumerateObject().Any()))
                        throw new InvalidOperationException("Paste accepts no text, keys or destination arguments; it uses only the app's verified copy.");
                    var question = args.RootElement.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
                    if (name is "research_elite" or "inspect_game" or "lookup_game_records" or "get_combat_context" && (string.IsNullOrWhiteSpace(question) || question.Length > 6000))
                        throw new InvalidOperationException("Ask a brief follow-up so I can research the right question.");
                    CombatQueryKind? combat = null;
                    if (name == "get_combat_context")
                    {
                        combat = args.RootElement.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String
                            ? mode.GetString() switch { "live" => CombatQueryKind.Live, "review" => CombatQueryKind.Review, _ => null }
                            : null;
                        if (combat is null || args.RootElement.EnumerateObject().Any(p => p.Name is not ("mode" or "question")))
                            throw new InvalidOperationException("Choose live or review combat context with a brief question.");
                    }
                    var system = args.RootElement.TryGetProperty("system", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    if (name == "copy_system" && !AnswerPolicy.IsSystemName(system))
                        throw new InvalidOperationException("Copy requires one exact system name, not a station, URL or sentence.");
                    MemoryEdit? memory = null;
                    if (name == "update_memory")
                    {
                        string? Arg(string key) => args.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                        memory = new(Arg("action") ?? "", Arg("kind") ?? "note", Arg("id"), Arg("text"));
                    }
                    GameRecordTopic? records = name == "lookup_game_records"
                        ? GameRecordTopics.Parse(args.RootElement.TryGetProperty("topic", out var topic) && topic.ValueKind == JsonValueKind.String ? topic.GetString() : null)
                        : null;
                    long? recordShip = null;
                    if (name == "lookup_game_records" && args.RootElement.TryGetProperty("ship_id", out var shipArg))
                    {
                        if (shipArg.ValueKind != JsonValueKind.Number || !shipArg.TryGetInt64(out var number) || number < 0 || records != GameRecordTopic.Modules)
                            throw new InvalidOperationException("A module lookup needs an exact non-negative ShipID from the ships lookup.");
                        recordShip = number;
                    }
                    _log.Write("voice.tool_start", name + " " + (name == "copy_system" ? system : name.Contains("memory") || records is not null ? "" : question), turn);
                    result = await tool(new(name, question, turn, system, memory, records, recordShip, combat), token);
                    _log.Write("voice.tool_done", name, turn);
                }
                catch (OperationCanceledException)
                {
                    _log.Write("voice.tool_cancelled", name, turn);
                    // New PTT prunes this abandoned call. Do not append stale results into its successor.
                    return;
                }
                catch (Exception ex)
                {
                    _log.Write("voice.tool_error", name + " " + ex.GetType().Name + ": " + ex.Message, turn);
                    result = JsonSerializer.Serialize(new { error = ex is InvalidOperationException ? ex.Message : "The lookup was unavailable. Be transparent and do not guess." });
                }
                token.ThrowIfCancellationRequested();
                await _sequence.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var itemId = Guid.NewGuid().ToString("N"); // Realtime item IDs have a 32-character limit.
                    lock (_sync) _toolItems[itemId] = turn;
                    await SendAsync(new { type = "conversation.item.create", item = new {
                        id = itemId, type = "function_call_output", call_id = callId, output = result
                    } }, token);
                    _log.Write("voice.tool_size", $"{name}: {result.Length} characters (content not logged).", turn);
                }
                finally { _sequence.Release(); }
            }
            await _sequence.WaitAsync(token);
            try { if (Current(turn)) await RespondAsync(turn, token); }
            finally { _sequence.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (Current(turn)) Faulted?.Invoke(new(turn, "The lookup connection failed. Please try again.")); }
    }
    private void TrimHistory()
    {
        if (_entries.Count > 48) _entries.RemoveRange(0, _entries.Count - 48);
        foreach (var old in _transcripts.Keys.Where(k => k < _generation - 24).ToArray()) _transcripts.Remove(old);
    }
    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _turn.Cancel(); }
        _connection?.Cancel();
        if (_transport is not null) await _transport.DisposeAsync();
        if (_receiver is not null) { try { await _receiver; } catch (OperationCanceledException) { } }
        _turn.Dispose(); _connection?.Dispose();
    }
    private sealed record Entry(int Turn, string Role, string ItemId, string Text);
}

