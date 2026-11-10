using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed partial class RealtimeTests
{
    private static RealtimeVoiceSession Session(Wire wire, Func<VoiceToolCall, CancellationToken, Task<string>>? tool = null) =>
        new(() => wire, () => "test-key", () => "coral", tool ?? ((_, _) => Task.FromResult("{}")));
    private static Dictionary<string, int> Heard() => [];
    private static object Created(int turn, string id) => new { type = "response.created", response = new { id, metadata = new { turn = turn.ToString() } } };
    private static object Audio(string response, string item) => new { type = "response.output_audio.delta", response_id = response, item_id = item, delta = Convert.ToBase64String(new byte[48000]) };
    private static object Item(string response, string id, string phase) => new { type = "response.output_item.added", response_id = response, item = new { id, type = "message", phase } };
    private static object Done(string id, params object[] output) => new { type = "response.done", response = new { id, status = "completed", output } };
    private static object Failed(string id, string status = "failed", string? reason = null, string? code = "server_error", params object[] output) =>
        new { type = "response.done", response = new { id, status, status_details = new { reason, error = new { code, type = "server_error", message = "Synthetic failure" } }, output } };
    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Expected asynchronous event did not arrive.");
    }
    [Fact]
    public void ConversationConfigurationHasNoAutomaticListeningOrMandatoryResearch()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(RealtimeProtocol.SessionUpdate("nova")));
        var session = doc.RootElement.GetProperty("session");
        Assert.Equal(JsonValueKind.Null, session.GetProperty("audio").GetProperty("input").GetProperty("turn_detection").ValueKind);
        Assert.Equal("auto", session.GetProperty("tool_choice").GetString());
        Assert.Equal("marin", session.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
        Assert.Contains("NOT a command interface", session.GetProperty("instructions").GetString());
        Assert.Equal(9, session.GetProperty("tools").GetArrayLength());
    }
    [Fact]
    public void VoicePolicyRequiresBriefAnswersFreshContextAndAutomaticPublicFallback()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(RealtimeProtocol.SessionUpdate("marin")));
        var config = doc.RootElement.GetProperty("session");
        var prompt = config.GetProperty("instructions").GetString()!;
        Assert.Contains("ONE short sentence", prompt);
        Assert.Contains("45 words", prompt);
        Assert.Contains("call get_game_context THIS TURN", prompt);
        Assert.Contains("automatically call research_elite in the SAME TURN", prompt);
        Assert.Contains("Missing PRIVATE facts", prompt);
        Assert.Contains("Community-goal eligibility/rewards", prompt);
        Assert.Contains("Call tools silently", prompt);
        Assert.Contains("community report, not proof", prompt);
        Assert.Equal("marin", config.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
        Assert.All(config.GetProperty("tools").EnumerateArray(), tool =>
            Assert.Contains("research_elite", tool.GetProperty("description").GetString() + tool.GetProperty("name").GetString()));
    }
    [Fact]
    public void VoicePolicyOmitsRoutineDisclaimersAndClipboardTutorialsButKeepsEvidenceChecks()
    {
        var prompt = RealtimeProtocol.Instructions;
        Assert.Contains("Skip routine Inara/source-reliability disclaimers", prompt);
        Assert.Contains("Do not explain copy/paste mechanics unless asked", prompt);
        Assert.Contains("A successful explicit copy needs only \"Copied Sol.\"", prompt);
        Assert.Contains("say only \"Paste sent.\" Then stop", prompt);
        Assert.Contains("For paste_uncertain or failure, state the specific problem", prompt);
        Assert.Contains("Never promote stale or search-only evidence to live verification", prompt);
        Assert.Contains("THIS tool result confirms clipboardCopied=true", prompt);
        Assert.DoesNotContain("mention an old/unknown report date once", prompt);
    }
    [Fact]
    public async Task PasteToolHasNoTextArgumentsAndReachesHostAsNamedAction()
    {
        var wire = new Wire(); var count = 0;
        await using var session = Session(wire, (call, _) => {
            Assert.Equal("paste_destination", call.Name); Assert.Null(call.System);
            count++; return Task.FromResult("{\"pasteSent\":true}");
        });
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "paste that");
        wire.Emit(Created(turn, "paste"));
        wire.Emit(Done("paste", new { type = "function_call", name = "paste_destination", call_id = "paste-call", arguments = "{}" }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        Assert.Equal(1, count);
        Assert.Contains("Ctrl+V ONLY", RealtimeProtocol.Instructions);
        Assert.Contains("copy ONLY", RealtimeProtocol.Instructions);
    }
    [Theory]
    [InlineData("{\"system\":\"Sol\"}")]
    [InlineData("{\"keys\":\"Enter\"}")]
    [InlineData("[]")]
    public async Task PasteCannotAcceptArbitraryTextOrKeySequences(string arguments)
    {
        var wire = new Wire(); var count = 0;
        await using var session = Session(wire, (_, _) => { count++; return Task.FromResult("{}"); });
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "paste that");
        wire.Emit(Created(turn, "paste"));
        wire.Emit(Done("paste", new { type = "function_call", name = "paste_destination", call_id = "paste-call", arguments }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        Assert.Equal(0, count);
    }
    [Fact]
    public async Task OnDemandRecordsAreDispatchedWithTypedTopicAndOptionalShip()
    {
        var wire = new Wire(); var calls = new List<VoiceToolCall>();
        await using var session = Session(wire, (call, _) => { lock (calls) calls.Add(call); return Task.FromResult("{}"); });
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "What modules are on my other ship?");
        wire.Emit(Created(turn, "records"));
        wire.Emit(Done("records", new { type = "function_call", name = "lookup_game_records", call_id = "records-call",
            arguments = "{\"topic\":\"modules\",\"question\":\"What modules are on ship 2?\",\"ship_id\":2}" }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        lock (calls) { var call = Assert.Single(calls); Assert.Equal(GameRecordTopic.Modules, call.Records); Assert.Equal(2, call.RecordShipId); }
        Assert.Contains("call lookup_game_records THIS TURN", RealtimeProtocol.Instructions);
        Assert.Contains("A visited carrier is not", RealtimeProtocol.Instructions);
    }
    [Theory]
    [InlineData("{\"topic\":\"../settings.json\",\"question\":\"Read it\"}")]
    [InlineData("{\"topic\":\"carrier\",\"question\":\"\"}")]
    [InlineData("{\"topic\":\"modules\",\"question\":\"My ship\",\"ship_id\":-1}")]
    public async Task InvalidRecordLookupNeverReachesHost(string arguments)
    {
        var wire = new Wire(); var count = 0;
        await using var session = Session(wire, (_, _) => { count++; return Task.FromResult("{}"); });
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "Test");
        wire.Emit(Created(turn, "records"));
        wire.Emit(Done("records", new { type = "function_call", name = "lookup_game_records", call_id = "records-call", arguments }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        Assert.Equal(0, count);
    }
    [Fact]
    public async Task MissingLocalFactCanContinueToResearchWithinTheSameTurn()
    {
        var wire = new Wire(); var calls = new List<VoiceToolCall>();
        await using var session = Session(wire, (call, _) => {
            lock (calls) calls.Add(call);
            return Task.FromResult(call.Name == "get_game_context"
                ? "{\"carrier\":{\"name\":\"Test Horizon\",\"callsign\":\"QZX-123\",\"location\":null}}"
                : "{\"answer\":\"Inara last reported Test Horizon in Sol; the report is old.\",\"clipboardCopied\":false}");
        });
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "Where is my carrier?");
        wire.Emit(Created(turn, "local"));
        wire.Emit(Done("local", new { type = "function_call", name = "get_game_context", call_id = "local-call", arguments = "{}" }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        wire.Emit(Created(turn, "research"));
        wire.Emit(Done("research", new { type = "function_call", name = "research_elite", call_id = "research-call", arguments = "{\"question\":\"Find public Inara location and report date for Test Horizon QZX-123.\"}" }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 3);
        lock (calls)
        {
            Assert.Equal(new[] { "get_game_context", "research_elite" }, calls.Select(c => c.Name));
            Assert.All(calls, c => Assert.Equal(turn, c.Turn));
        }
        Assert.Single(session.History);
    }
    [Fact]
    public async Task ConnectionWaitsQuietlyUntilUserSubmits()
    {
        var wire = new Wire();
        await using var session = Session(wire);
        await session.ConnectAsync();
        Assert.Single(wire.Events);
        Assert.Equal("session.update", wire.Events.Single().GetProperty("type").GetString());
        var turn = await session.BeginTurnAsync(Heard());
        Assert.DoesNotContain(wire.Events, e => e.GetProperty("type").GetString() is "response.create" or "input_audio_buffer.append");
        await session.SubmitAudioAsync(turn, new byte[9600]);
        Assert.Contains(wire.Events, e => e.GetProperty("type").GetString() == "input_audio_buffer.commit");
        Assert.Single(wire.Events, e => e.GetProperty("type").GetString() == "response.create");
    }
    [Fact]
    public async Task GreetingsDoNotCallTheResearchHelper()
    {
        var wire = new Wire(); var tools = 0; var frames = 0;
        await using var session = Session(wire, (_, _) => { tools++; return Task.FromResult("{}"); });
        session.AudioReceived += _ => Interlocked.Increment(ref frames);
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "Testing, hello, how are you tonight?");
        wire.Emit(Created(turn, "r1")); wire.Emit(Audio("r1", "a1")); wire.Emit(Done("r1"));
        await Eventually(() => frames == 1);
        Assert.Equal(0, tools);
        Assert.Single(session.History);
    }
    [Fact]
    public async Task LateAudioIsDroppedAfterInterruption()
    {
        var wire = new Wire(); var frames = 0;
        await using var session = Session(wire);
        session.AudioReceived += _ => Interlocked.Increment(ref frames);
        var first = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(first, "Hello");
        wire.Emit(Created(first, "old")); wire.Emit(Item("old", "old-item", "final_answer")); wire.Emit(Audio("old", "old-item"));
        await Eventually(() => frames == 1);
        var next = await session.BeginTurnAsync(new Dictionary<string, int> { ["old-item"] = 250 });
        var truncate = Assert.Single(wire.Events, e => e.GetProperty("type").GetString() == "conversation.item.truncate");
        Assert.Equal(250, truncate.GetProperty("audio_end_ms").GetInt32());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SubmitAudioAsync(first, new byte[4800]));
        wire.Emit(Audio("old", "old-item")); wire.Emit(Done("old"));
        await session.SubmitTextAsync(next, "New question");
        wire.Emit(Created(next, "new")); wire.Emit(Item("new", "new-item", "final_answer")); wire.Emit(Audio("new", "new-item"));
        await Eventually(() => frames == 2);
        Assert.Equal(2, frames);
    }
    [Fact]
    public async Task FollowupUsesTheSameConnectionAndKeepsConversation()
    {
        var wire = new Wire();
        await using var session = Session(wire);
        var first = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(first, "Hello tonight");
        wire.Emit(Created(first, "r"));
        wire.Emit(new { type = "response.output_audio_transcript.done", response_id = "r", item_id = "a", transcript = "Hello there!" });
        wire.Emit(Done("r"));
        await Eventually(() => session.History.First().Answer == "Hello there!");
        var second = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(second, "What did I just say?");
        Assert.Equal(1, wire.ConnectCount);
        Assert.Equal(2, session.History.Count);
        Assert.Equal("Hello tonight", session.History[0].Question);
    }
    [Fact]
    public async Task CommentaryIsNeitherPlayedNorKeptInConversationalHistory()
    {
        var wire = new Wire(); var frames = 0;
        await using var session = Session(wire);
        session.AudioReceived += _ => Interlocked.Increment(ref frames);
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "How much platinum?");
        wire.Emit(Created(turn, "r"));
        wire.Emit(Item("r", "preamble", "commentary"));
        wire.Emit(Audio("r", "preamble"));
        wire.Emit(new { type = "response.output_audio_transcript.done", response_id = "r", item_id = "preamble", transcript = "Let me check." });
        wire.Emit(Item("r", "answer", "final_answer"));
        wire.Emit(Audio("r", "answer"));
        wire.Emit(new { type = "response.output_audio_transcript.done", response_id = "r", item_id = "answer", transcript = "One ton." });
        wire.Emit(Done("r"));
        await Eventually(() => session.History.First().Answer == "One ton.");
        Assert.Equal(1, frames);
    }
    [Fact]
    public async Task InterruptedUnclassifiedAudioNeverLeaksIntoNextAnswer()
    {
        var wire = new Wire(); var frames = 0;
        await using var session = Session(wire);
        session.AudioReceived += _ => Interlocked.Increment(ref frames);
        var first = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(first, "First");
        wire.Emit(Created(first, "old")); wire.Emit(Audio("old", "unknown"));
        await Task.Delay(30);
        var next = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(next, "Second");
        wire.Emit(Done("old"));
        wire.Emit(Created(next, "new")); wire.Emit(Audio("new", "new-item")); wire.Emit(Done("new"));
        await Eventually(() => frames == 1);
        Assert.Equal(1, frames);
    }
    [Fact]
    public async Task ResearchIsInvokedOnlyThroughAValidFunctionCall()
    {
        var wire = new Wire(); var tools = 0;
        await using var session = Session(wire, (call, _) => { Assert.Equal("Where can I buy gold?", call.Question); Interlocked.Increment(ref tools); return Task.FromResult("{\"answer\":\"Verified result\"}"); });
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "Where can I buy gold?");
        wire.Emit(Created(turn, "r"));
        wire.Emit(Done("r", new { type = "function_call", name = "research_elite", call_id = "c", arguments = "{\"question\":\"Where can I buy gold?\"}" }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        Assert.Equal(1, tools);
        Assert.Contains(wire.Events, e => e.GetProperty("type").GetString() == "conversation.item.create" && e.GetProperty("item").GetProperty("type").GetString() == "function_call_output");
    }
    [Fact]
    public async Task EmptyAudioNeverCreatesAResponse()
    {
        var wire = new Wire();
        await using var session = Session(wire);
        var turn = await session.BeginTurnAsync(Heard());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SubmitAudioAsync(turn, []));
        Assert.DoesNotContain(wire.Events, e => e.GetProperty("type").GetString() == "response.create");
    }
    [Fact]
    public async Task InterruptedResearchCannotStartAnOldReply()
    {
        var wire = new Wire(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = Session(wire, async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return "{}"; });
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "Research something");
        wire.Emit(Created(turn, "r"));
        wire.Emit(Done("r", new { id = "abandoned-call", type = "function_call", name = "research_elite", call_id = "c", arguments = "{\"question\":\"Research something\"}" }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.BeginTurnAsync(Heard());
        Assert.Contains(wire.Events, e => e.GetProperty("type").GetString() == "conversation.item.delete" && e.GetProperty("item_id").GetString() == "abandoned-call");
        Assert.DoesNotContain(wire.Events, e => e.GetProperty("type").GetString() == "conversation.item.create" && e.GetProperty("item").GetProperty("type").GetString() == "function_call_output");
        Assert.Single(wire.Events, e => e.GetProperty("type").GetString() == "response.create");
    }
    [Fact]
    public async Task TemporaryFailureRetriesOnceInTheSameVoiceSession()
    {
        var wire = new Wire(); var faults = new List<VoiceFault>();
        await using var session = Session(wire);
        session.Faulted += f => { lock (faults) faults.Add(f); };
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "What's my jump range?");
        wire.Emit(Created(turn, "first")); wire.Emit(Failed("first"));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        Assert.Equal(1, wire.ConnectCount);
        Assert.Single(wire.Events, e => e.GetProperty("type").GetString() == "session.update");
        lock (faults) Assert.Empty(faults);
        wire.Emit(Created(turn, "retry")); wire.Emit(Failed("retry"));
        await Eventually(() => { lock (faults) return faults.Count == 1; });
        lock (faults) Assert.Equal(turn, faults[0].Turn);
        Assert.Equal(2, wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create"));
    }
    [Fact]
    public async Task ProviderFailureReasonIsLoggedBeforeTheUserError()
    {
        var wire = new Wire(); var log = new FailureLog(); var fault = new TaskCompletionSource<VoiceFault>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new RealtimeVoiceSession(() => wire, () => "test-key", () => "marin", (_, _) => Task.FromResult("{}"), log);
        session.Faulted += f => fault.TrySetResult(f);
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "Hello");
        wire.Emit(Created(turn, "response-for-support")); wire.Emit(Failed("response-for-support", code: "insufficient_quota"));
        Assert.Equal(turn, (await fault.Task.WaitAsync(TimeSpan.FromSeconds(2))).Turn);
        var diagnostic = Assert.Single(log.Items, item => item.Kind == "voice.response_failure");
        Assert.Contains("insufficient_quota", diagnostic.Message);
        Assert.Contains("response-for-support", diagnostic.Message);
        Assert.Equal(turn, diagnostic.Turn);
    }
    [Fact]
    public async Task RetryPreservesCompletedToolResultsAndRemovesOnlyFailedOutput()
    {
        var wire = new Wire(); var tools = 0;
        await using var session = Session(wire, (_, _) => { Interlocked.Increment(ref tools); return Task.FromResult("{\"jumpRange\":28.1}"); });
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "What's my jump range?");
        wire.Emit(Created(turn, "lookup"));
        wire.Emit(Done("lookup", new { type = "function_call", name = "get_game_context", call_id = "call", arguments = "{}" }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        wire.Emit(Created(turn, "answer"));
        var failed = Failed("answer", output: new { id = "partial-answer", type = "message" });
        wire.Emit(failed); wire.Emit(failed); // duplicate terminal event must not queue a second retry
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 3);
        Assert.Equal(1, tools);
        Assert.Equal("partial-answer", Assert.Single(wire.Events, e => e.GetProperty("type").GetString() == "conversation.item.delete").GetProperty("item_id").GetString());
        Assert.Single(wire.Events, e => e.GetProperty("type").GetString() == "conversation.item.create" && e.GetProperty("item").GetProperty("type").GetString() == "function_call_output");
    }
    [Fact]
    public async Task NewPushToTalkCancelsPendingRetry()
    {
        var wire = new Wire(); var retryAnnounced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = Session(wire);
        session.StatusChanged += s => { if (s.Text == "Retrying reply") retryAnnounced.TrySetResult(); };
        var first = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(first, "Old question");
        wire.Emit(Created(first, "failed")); wire.Emit(Failed("failed"));
        await retryAnnounced.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var next = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(next, "New question");
        await Task.Delay(500);
        Assert.Equal(2, wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create"));
        Assert.DoesNotContain(wire.Events, e => e.GetProperty("type").GetString() == "conversation.item.delete");
    }
    [Fact]
    public async Task FailureAfterAudibleOutputDoesNotReplayTheAnswer()
    {
        var wire = new Wire(); var frames = 0; var faults = 0;
        await using var session = Session(wire);
        session.AudioReceived += _ => Interlocked.Increment(ref frames);
        session.Faulted += _ => Interlocked.Increment(ref faults);
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "Hello");
        wire.Emit(Created(turn, "r")); wire.Emit(Item("r", "answer", "final_answer")); wire.Emit(Audio("r", "answer"));
        await Eventually(() => frames == 1);
        wire.Emit(Failed("r"));
        await Eventually(() => faults == 1);
        Assert.Single(wire.Events, e => e.GetProperty("type").GetString() == "response.create");
    }
    [Fact]
    public async Task TokenLimitRetryGetsExtraRoomWithoutChangingVoiceOrSession()
    {
        var wire = new Wire();
        await using var session = Session(wire);
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "Hello");
        wire.Emit(Created(turn, "limited")); wire.Emit(Failed("limited", "incomplete", "max_output_tokens", null));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        var retry = wire.Events.Last(e => e.GetProperty("type").GetString() == "response.create");
        Assert.Equal(4096, retry.GetProperty("response").GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(1, wire.ConnectCount);
    }
    [Theory]
    [InlineData("incomplete", "content_filter", null)]
    [InlineData("failed", null, "invalid_api_key")]
    [InlineData("failed", null, "insufficient_quota")]
    public async Task NonTransientFailuresNeverRetry(string status, string? reason, string? code)
    {
        var wire = new Wire(); var faults = 0;
        await using var session = Session(wire);
        session.Faulted += _ => Interlocked.Increment(ref faults);
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "Hello");
        wire.Emit(Created(turn, "r")); wire.Emit(Failed("r", status, reason, code));
        await Eventually(() => faults == 1);
        Assert.Single(wire.Events, e => e.GetProperty("type").GetString() == "response.create");
    }
    [Fact]
    public void ClipboardToolHasDedicatedSchemaAndTruthfulConfirmationPolicy()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(RealtimeProtocol.SessionUpdate("marin")));
        var config = doc.RootElement.GetProperty("session");
        var tool = Assert.Single(config.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "copy_system");
        Assert.Equal("system", tool.GetProperty("parameters").GetProperty("required")[0].GetString());
        Assert.Contains("Do NOT send clipboard actions to research_elite", config.GetProperty("instructions").GetString());
        Assert.Contains("THIS tool result confirms clipboardCopied=true", config.GetProperty("instructions").GetString());
    }
    [Fact]
    public async Task CopyFollowupReachesDedicatedHostActionWithTypedSystem()
    {
        var wire = new Wire(); var calls = new List<VoiceToolCall>();
        await using var session = Session(wire, (call, _) => {
            lock (calls) calls.Add(call);
            return Task.FromResult("{\"clipboardCopied\":true,\"copiedSystem\":\"Sol\"}");
        });
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "Copy that system to my clipboard.");
        wire.Emit(Created(turn, "copy"));
        wire.Emit(Done("copy", new { type = "function_call", name = "copy_system", call_id = "copy-call", arguments = "{\"system\":\"Sol\"}" }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        lock (calls)
        {
            var call = Assert.Single(calls);
            Assert.Equal("copy_system", call.Name);
            Assert.Equal("Sol", call.System);
            Assert.Equal("", call.Question);
        }
        Assert.Contains(wire.Events, e => e.GetProperty("type").GetString() == "conversation.item.create"
            && e.GetProperty("item").GetProperty("type").GetString() == "function_call_output"
            && e.GetProperty("item").GetProperty("output").GetString()!.Contains("\"clipboardCopied\":true"));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"system\":null}")]
    [InlineData("{\"system\":123}")]
    [InlineData("{\"system\":\"Sol, Colonia\"}")]
    [InlineData("{\"system\":\"https://example.com\"}")]
    public async Task InvalidCopyToolArgumentsNeverReachHost(string arguments)
    {
        var wire = new Wire(); var calls = 0;
        await using var session = Session(wire, (_, _) => { calls++; return Task.FromResult("{}"); });
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, "Copy a location.");
        wire.Emit(Created(turn, "copy"));
        wire.Emit(Done("copy", new { type = "function_call", name = "copy_system", call_id = "bad-copy", arguments }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData("save_screenshot", "{}", "Take a screenshot")]
    [InlineData("inspect_game", "{\"question\":\"Explain this warning\"}", "Explain this warning")]
    public async Task ScreenToolsReachTheirDedicatedHostAction(string name, string arguments, string user)
    {
        var wire = new Wire(); var used = new List<VoiceToolCall>();
        await using var session = Session(wire, (call, _) => {
            lock (used) used.Add(call);
            return Task.FromResult(name == "save_screenshot" ? "{\"screenshotSaved\":true}" : "{\"answer\":\"The warning is visible.\"}");
        });
        var turn = await session.BeginTurnAsync(Heard());
        await session.SubmitTextAsync(turn, user);
        wire.Emit(Created(turn, "screen"));
        wire.Emit(Done("screen", new { type = "function_call", name, call_id = "screen-call", arguments }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        lock (used) Assert.Equal(name, Assert.Single(used).Name);
    }
    private sealed class Wire : IRealtimeTransport
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private readonly List<JsonElement> _sent = [];
        public bool IsConnected { get; private set; }
        public int ConnectCount { get; private set; }
        public JsonElement[] Events { get { lock (_sent) return _sent.ToArray(); } }
        public Task ConnectAsync(string model, string key, CancellationToken token) { IsConnected = true; ConnectCount++; return Task.CompletedTask; }
        public Task SendAsync(string json, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(json);
            lock (_sent) _sent.Add(doc.RootElement.Clone());
            if (doc.RootElement.GetProperty("type").GetString() == "session.update") Emit(new { type = "session.updated" });
            return Task.CompletedTask;
        }
        public void Emit(object value) => _incoming.Writer.TryWrite(JsonSerializer.Serialize(value));
        public async IAsyncEnumerable<string> ReceiveAsync([EnumeratorCancellation] CancellationToken token)
        { await foreach (var json in _incoming.Reader.ReadAllAsync(token)) yield return json; }
        public ValueTask DisposeAsync() { IsConnected = false; _incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class FailureLog : IDiagnosticLog
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string Kind, string Message, int? Turn)> Items { get; } = new();
        public void Write(string kind, string message, int? turn = null) => Items.Enqueue((kind, message, turn));
    }
}

