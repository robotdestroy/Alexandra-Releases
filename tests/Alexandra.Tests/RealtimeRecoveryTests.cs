using System.Collections.Concurrent;
using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed partial class RealtimeTests
{
    private sealed class RecoveryClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
        public Task Delay(TimeSpan time, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Advance(time); return Task.CompletedTask; }
    }
    private static object Limited(string id, string message = "Please try again in 10.803s.") => new {
        type = "response.done", response = new { id, status = "failed", output = Array.Empty<object>(),
            status_details = new { error = new { code = "rate_limit_exceeded", type = "tokens", message } } }
    };
    private static int Responses(Wire wire) => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create");

    [Fact]
    public void SessionHasFirmContextBudgetAndRetention()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(RealtimeProtocol.SessionUpdate("marin")));
        var truncation = doc.RootElement.GetProperty("session").GetProperty("truncation");
        Assert.Equal("retention_ratio", truncation.GetProperty("type").GetString());
        Assert.Equal(.8, truncation.GetProperty("retention_ratio").GetDouble());
        Assert.Equal(16000, truncation.GetProperty("token_limits").GetProperty("post_instructions").GetInt32());
    }
    [Fact]
    public async Task LongConversationRetiresLargeLookupPairsButKeepsSpokenFollowups()
    {
        var wire = new Wire(); var log = new FailureLog();
        await using var session = new RealtimeVoiceSession(() => wire, () => "synthetic", () => "marin",
            (_, _) => Task.FromResult(JsonSerializer.Serialize(new { fuel = 752, padding = new string('x', 50000) })), log);
        for (var index = 0; index < 25; index++)
        {
            var turn = await session.BeginTurnAsync(Heard());
            await session.SubmitTextAsync(turn, "What is my fuel?");
            wire.Emit(Created(turn, "lookup" + index));
            wire.Emit(Done("lookup" + index, new { id = "call-item" + index, type = "function_call", name = "get_game_context", call_id = "call" + index, arguments = "{}" }));
            await Eventually(() => Responses(wire) == (index + 1) * 2);
            var resultItem = wire.Events.Last(e => e.GetProperty("type").GetString() == "conversation.item.create").GetProperty("item");
            Assert.InRange(resultItem.GetProperty("id").GetString()!.Length, 1, 32);
            wire.Emit(Created(turn, "answer" + index));
            wire.Emit(new { type = "response.output_audio_transcript.done", response_id = "answer" + index, item_id = "spoken" + index, transcript = "Your carrier has 752 tonnes." });
            wire.Emit(Done("answer" + index));
            await Eventually(() => session.History.Last().Answer == "Your carrier has 752 tonnes.");
            var deletes = wire.Events.Where(e => e.GetProperty("type").GetString() == "conversation.item.delete").ToArray();
            Assert.Equal(index * 2, deletes.Length);
            Assert.DoesNotContain(deletes, e => e.GetProperty("item_id").GetString()!.StartsWith("spoken"));
        }
        Assert.Equal(1, wire.ConnectCount); Assert.Equal(12, session.History.Count);
        Assert.Equal(24, log.Items.Count(e => e.Kind == "voice.context_pruned"));
    }
    [Fact]
    public async Task RateLimitWaitsThenRetriesInSameVoiceWithoutRepeatingTools()
    {
        var wire = new Wire(); var clock = new RecoveryClock(); var toolCalls = 0; var statuses = new ConcurrentQueue<VoiceStatus>();
        await using var session = new RealtimeVoiceSession(() => wire, () => "synthetic", () => "marin",
            (_, _) => { toolCalls++; return Task.FromResult("{\"clipboardCopied\":true,\"copiedSystem\":\"Sol\"}"); },
            clock: clock, delay: clock.Delay);
        session.StatusChanged += statuses.Enqueue;
        var turn = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(turn, "Copy Sol");
        wire.Emit(Created(turn, "lookup")); wire.Emit(Done("lookup", new { id = "call-item", type = "function_call", name = "copy_system", call_id = "c", arguments = "{\"system\":\"Sol\"}" }));
        await Eventually(() => Responses(wire) == 2);
        var before = clock.GetUtcNow();
        wire.Emit(Created(turn, "limited")); wire.Emit(Limited("limited")); wire.Emit(Limited("limited"));
        await Eventually(() => Responses(wire) == 3);
        Assert.True(clock.GetUtcNow() - before >= TimeSpan.FromSeconds(10.803));
        Assert.Equal(1, toolCalls); Assert.Equal(1, wire.ConnectCount);
        Assert.Contains(statuses, s => s.Attention && s.Busy && s.Text.StartsWith("Voice busy"));
        Assert.DoesNotContain(wire.Events, e => e.GetProperty("type").GetString() == "conversation.item.delete");
    }
    [Fact]
    public async Task RepeatedRateLimitsAreBoundedAndNeverUseAnotherSpeaker()
    {
        var wire = new Wire(); var clock = new RecoveryClock(); var fault = new TaskCompletionSource<VoiceFault>();
        await using var session = new RealtimeVoiceSession(() => wire, () => "synthetic", () => "marin",
            (_, _) => Task.FromResult("{}"), clock: clock, delay: clock.Delay);
        session.Faulted += f => fault.TrySetResult(f);
        var turn = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(turn, "Hello");
        for (var i = 0; i < 3; i++)
        {
            wire.Emit(Created(turn, "r" + i)); wire.Emit(Limited("r" + i));
            if (i < 2) await Eventually(() => Responses(wire) == i + 2);
        }
        Assert.Contains("temporarily busy", (await fault.Task.WaitAsync(TimeSpan.FromSeconds(2))).Message);
        Assert.Equal(3, Responses(wire)); Assert.Equal(1, wire.ConnectCount);
        Assert.Single(wire.Events, e => e.GetProperty("type").GetString() == "session.update");
    }
    [Fact]
    public async Task LongServerDelayIsDeferredRatherThanRetriedEarly()
    {
        var wire = new Wire(); var clock = new RecoveryClock(); var fault = new TaskCompletionSource<VoiceFault>();
        await using var session = new RealtimeVoiceSession(() => wire, () => "synthetic", () => "marin",
            (_, _) => Task.FromResult("{}"), clock: clock, delay: clock.Delay);
        session.Faulted += f => fault.TrySetResult(f);
        var turn = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(turn, "Hello");
        var before = clock.GetUtcNow();
        wire.Emit(Created(turn, "r")); wire.Emit(Limited("r", "Please try again in 120s."));
        await fault.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Responses(wire)); Assert.Equal(before, clock.GetUtcNow());
    }
    [Fact]
    public async Task NewPttCancelsOldRetryButStillHonorsSharedCooldown()
    {
        var wire = new Wire(); var clock = new RecoveryClock();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new RealtimeVoiceSession(() => wire, () => "synthetic", () => "marin",
            (_, _) => Task.FromResult("{}"), clock: clock, delay: (_, token) => { waiting.TrySetResult(); return gate.Task.WaitAsync(token); });
        var first = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(first, "Old question");
        wire.Emit(Created(first, "old")); wire.Emit(Limited("old"));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await session.BeginTurnAsync(Heard());
        var submit = session.SubmitTextAsync(second, "New question");
        Assert.Equal(1, Responses(wire)); Assert.False(submit.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(30)); gate.SetResult(); await submit;
        Assert.Equal(2, Responses(wire));
        Assert.Equal(second, RealtimeProtocol.ResponseTurn(wire.Events.Last(e => e.GetProperty("type").GetString() == "response.create").GetProperty("response")));
    }
    [Fact]
    public async Task RateLimitAfterAudibleSpeechDoesNotReplay()
    {
        var wire = new Wire(); var clock = new RecoveryClock(); var fault = new TaskCompletionSource<VoiceFault>(); var frames = 0;
        await using var session = new RealtimeVoiceSession(() => wire, () => "synthetic", () => "marin",
            (_, _) => Task.FromResult("{}"), clock: clock, delay: clock.Delay);
        session.Faulted += f => fault.TrySetResult(f); session.AudioReceived += _ => frames++;
        var turn = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(turn, "Hello");
        wire.Emit(Created(turn, "r")); wire.Emit(Item("r", "answer", "final_answer")); wire.Emit(Audio("r", "answer"));
        await Eventually(() => frames == 1); wire.Emit(Limited("r"));
        await fault.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(1, Responses(wire));
    }
    [Fact]
    public async Task LookupLimitStopsInsteadOfFeedingAnEndlessErrorLoop()
    {
        var wire = new Wire(); var toolCalls = 0; var fault = new TaskCompletionSource<VoiceFault>();
        await using var session = Session(wire, (_, _) => { toolCalls++; return Task.FromResult("{}"); });
        session.Faulted += f => fault.TrySetResult(f);
        var turn = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(turn, "Check my game");
        for (var index = 0; index < 7; index++)
        {
            wire.Emit(Created(turn, "loop" + index));
            wire.Emit(Done("loop" + index, new { id = "lookup-item" + index, type = "function_call", name = "get_game_context", call_id = "c" + index, arguments = "{}" }));
            if (index < 6) await Eventually(() => Responses(wire) == index + 2);
        }
        Assert.Contains("lookup limit", (await fault.Task.WaitAsync(TimeSpan.FromSeconds(2))).Message);
        Assert.Equal(6, toolCalls); Assert.Equal(7, Responses(wire));
    }
    [Fact]
    public async Task CorrelatedTopLevelRateErrorRetriesOnlyItsOwnRequest()
    {
        var wire = new Wire(); var clock = new RecoveryClock();
        await using var session = new RealtimeVoiceSession(() => wire, () => "synthetic", () => "marin",
            (_, _) => Task.FromResult("{}"), clock: clock, delay: clock.Delay);
        var turn = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(turn, "Hello");
        var id = wire.Events.Last().GetProperty("event_id").GetString();
        var failure = new { type = "error", error = new { event_id = id, code = "rate_limit_exceeded", type = "tokens", message = "Please try again in 0.5s." } };
        wire.Emit(failure); wire.Emit(failure);
        await Eventually(() => Responses(wire) == 2);
        Assert.Equal(2, Responses(wire));
    }
    [Fact]
    public async Task MissingPrunedItemIsIgnoredOnlyForOurMatchingDelete()
    {
        var wire = new Wire(); var faults = 0;
        await using var session = Session(wire);
        session.Faulted += _ => faults++;
        var turn = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(turn, "Where am I?");
        wire.Emit(Created(turn, "r")); wire.Emit(Done("r", new { id = "old-call", type = "function_call", name = "get_game_context", call_id = "c", arguments = "{}" }));
        await Eventually(() => Responses(wire) == 2);
        await session.BeginTurnAsync(Heard());
        var id = wire.Events.First(e => e.GetProperty("type").GetString() == "conversation.item.delete").GetProperty("event_id").GetString();
        wire.Emit(new { type = "error", error = new { code = "conversation_item_not_found", event_id = id } });
        // The later unrelated error is not swallowed by that exception.
        wire.Emit(new { type = "error", error = new { code = "conversation_item_not_found", event_id = "unrelated" } });
        await Eventually(() => faults > 0); Assert.Equal(1, faults);
    }
    [Theory]
    [InlineData("Please try again in 10.803s.", 10.803)]
    [InlineData("Please try again in 250ms.", .25)]
    [InlineData("Please try again in 300s.", 300)]
    [InlineData("Please try again in 1m30.5s.", 90.5)]
    [InlineData("Please try again in 1h2m.", 3720)]
    public void ProviderCooldownHintIsPreserved(string text, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), RealtimeCooldown.RetryHint(text));
    [Theory]
    [InlineData(null)]
    [InlineData("Unknown")]
    [InlineData("try again in -10s")]
    [InlineData("try again in NaNs")]
    public void InvalidHintsDoNotBecomeImmediateRetries(string? text) => Assert.Null(RealtimeCooldown.RetryHint(text));
    [Fact]
    public void RateLimitResetMetadataIsUsedWhenNoHintExists()
    {
        var clock = new RecoveryClock(); var cooldown = new RealtimeCooldown(clock);
        using var data = JsonDocument.Parse("""{"rate_limits":[{"name":"tokens","reset_seconds":20},{"name":"requests","reset_seconds":"invalid"}]}""");
        cooldown.Observe(data.RootElement);
        var delay = cooldown.Record(new("failed", null, "rate_limit_exceeded", "tokens", "temporarily limited"));
        Assert.InRange(delay.TotalSeconds, 21, 21.3);
    }
    [Fact]
    public void UnknownDelaysBackOffAndExtremeHintsAreNeverShortened()
    {
        var clock = new RecoveryClock(); var cooldown = new RealtimeCooldown(clock);
        var failure = new RealtimeFailure("failed", null, "rate_limit_exceeded", "tokens", "temporarily limited");
        Assert.InRange(cooldown.Record(failure).TotalSeconds, 6, 6.3);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.InRange(cooldown.Record(failure, 1).TotalSeconds, 11, 11.3);
        Assert.Equal(TimeSpan.MaxValue, RealtimeCooldown.RetryHint("Please try again in 999999999999999999999h."));
        Assert.True(cooldown.Record(failure with { Message = "Please try again in 999999999999999999999h." }) > TimeSpan.FromDays(365));
    }
    [Fact]
    public void FailureNotificationIsShortQuietAndHasSilentEdges()
    {
        var pcm = AttentionPulse.CreatePcm();
        Assert.Equal(24000, pcm.Length);
        var samples = Enumerable.Range(0, pcm.Length / 2).Select(i => BitConverter.ToInt16(pcm, i * 2)).ToArray();
        Assert.Equal(0, samples[0]); Assert.InRange(Math.Abs(samples[^1]), 0, 1);
        Assert.InRange(samples.Max(s => Math.Abs((int)s)), 300, 1200);
        Assert.All(samples.Skip(4800).Take(2400), s => Assert.Equal(0, s));
    }
}
