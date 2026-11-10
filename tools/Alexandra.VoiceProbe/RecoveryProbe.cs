using System.Collections.Concurrent;
using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;

internal static class RecoveryProbe
{
    public static async Task RunAsync(string key)
    {
        Console.WriteLine("Synthetic long-context check: no microphone, real game data, playback, saved audio or clipboard actions.");
        var log = new ProbeLog();
        var heard = new ConcurrentDictionary<string, int>();
        var answers = new ConcurrentDictionary<int, string>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = JsonSerializer.Serialize(new {
            provenance = "Synthetic test fixture only, not actual commander data.",
            carrier = new { name = "Test Star", fuelTonnes = 752, observedNow = true },
            otherMaterials = Enumerable.Range(0, 400).Select(i => new {
                name = "Synthetic engineering material " + i, category = "test", count = i, capacity = 300
            })
        });
        await using var session = new RealtimeVoiceSession(() => new RealtimeWebSocketTransport(), () => key, () => "marin",
            (call, _) => Task.FromResult(call.Name is "get_game_context" or "lookup_game_records" or "read_memory"
                ? payload : "{\"available\":false,\"reason\":\"This synthetic test supports local data lookups only.\"}"), log);
        session.AudioReceived += frame => heard.AddOrUpdate(frame.ItemId, frame.Pcm.Length, (_, bytes) => bytes + frame.Pcm.Length);
        session.TranscriptReceived += (turn, role, text) => { if (role == "assistant") answers.AddOrUpdate(turn, text, (_, old) => old + " " + text); };
        session.StatusChanged += status => { if (status.Text == "Conversation ready") completed.TrySetResult(); };
        session.Faulted += failure => completed.TrySetException(new InvalidOperationException(failure.Message));
        var questions = new[] {
            "How much fuel does my carrier, Test Star, have? Check the current local game data.",
            "Check my carrier fuel again. How much would remain after using 100 tonnes?",
            "Check the latest local records again: how much tritium does my carrier have now?",
            "What is the name of the carrier we've been discussing? Just its name."
        };
        for (var index = 0; index < questions.Length; index++)
        {
            completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Synthetic acknowledgement preserves dialogue in this headless test; no audio is actually played.
            var turn = await session.BeginTurnAsync(heard.ToDictionary(x => x.Key, x => x.Value / 48));
            heard.Clear();
            await session.SubmitTextAsync(turn, questions[index]);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(90));
            if (!answers.TryGetValue(turn, out var answer) || string.IsNullOrWhiteSpace(answer))
                throw new InvalidOperationException("No completed answer.");
            Console.WriteLine($"PASS: synthetic turn {index + 1}: {answer}");
            if (index == 3 && !answer.Contains("Test Star", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The follow-up lost the carrier name.");
        }
        if (log.Prunes < 3) throw new InvalidOperationException("Expected old tool data to be retired between turns.");
        if (log.InputTokens.Count < 6 || log.InputTokens.Any(n => n > 24000))
            throw new InvalidOperationException("Unexpected live context growth.");
        Console.WriteLine($"PASS: {log.Prunes} tool cleanups; peak input {log.InputTokens.Max()} tokens; same Marin connection and follow-up retained.");
    }
    private sealed class ProbeLog : IDiagnosticLog
    {
        public ConcurrentQueue<int> InputTokens { get; } = new();
        public int Prunes;
        public void Write(string kind, string message, int? turn = null)
        {
            if (kind == "voice.context_pruned") { Interlocked.Increment(ref Prunes); Console.WriteLine(message); }
            if (kind == "voice.usage")
            {
                using var doc = JsonDocument.Parse(message);
                var count = doc.RootElement.GetProperty("inputTokens").GetInt32();
                InputTokens.Enqueue(count); Console.WriteLine($"Usage: turn {turn}, {count} input tokens.");
            }
            if (kind is "voice.api_error" or "voice.connection_error" or "voice.response_failure")
                Console.WriteLine(kind + ": " + message); // All conversation data in this probe is synthetic.
        }
    }
}
