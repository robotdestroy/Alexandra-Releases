using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;

internal static class MemoryProbe
{
    // Opt-in live model evaluation, isolated synthetic memory; no microphone, game access, playback or clipboard.
    public static async Task RunAsync(string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "alexandra-memory-probe-" + Guid.NewGuid().ToString("N"));
        var journals = Path.Combine(root, "journals");
        Directory.CreateDirectory(journals);
        var journal = Path.Combine(journals, "Journal.2026-11-01.01.log");
        await File.WriteAllTextAsync(journal, """
            {"timestamp":"2026-11-01T10:00:00Z","event":"LoadGame","Commander":"Synthetic Memory Probe","FID":"SYNTHETIC-PROBE","Ship":"mandalay","ShipID":1}
            {"timestamp":"2026-11-01T10:00:01Z","event":"Location","StarSystem":"Sol"}
            {"timestamp":"2026-11-01T10:00:02Z","event":"Materials","Raw":[{"Name":"iron","Count":10}],"Manufactured":[],"Encoded":[]}

            """);
        try
        {
            var memory = new CompanionMemory(() => journals, Path.Combine(root, "memory"));
            await RunTurn("Remember my goal is to collect twenty iron for engineering.", memory, key, requireWrite: true);
            // Fresh voice session AND fresh memory service, with missed gameplay in between.
            await File.AppendAllTextAsync(journal, "{\"timestamp\":\"2026-11-01T11:00:00Z\",\"event\":\"MaterialCollected\",\"Name\":\"iron\",\"Category\":\"Raw\",\"Count\":3}\n");
            memory = new CompanionMemory(() => journals, Path.Combine(root, "memory"));
            var answer = await RunTurn("What goal was I working on, and how much iron do I have now?", memory, key);
            if (!answer.Contains("iron", StringComparison.OrdinalIgnoreCase) ||
                !System.Text.RegularExpressions.Regex.IsMatch(answer, @"\b(13|thirteen)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidOperationException("The reply did not use the caught-up inventory.");
            await RunTurn("Forget my saved goal about collecting iron.", memory, key, requireWrite: true);
            var state = JsonSerializer.Deserialize<JsonElement>((await memory.ReadAsync(default)).Json);
            if (state.GetProperty("memory").GetProperty("savedItems").GetArrayLength() != 0)
                throw new InvalidOperationException("The goal was not forgotten.");
            Console.WriteLine("PASS: live voice-tool selection, persistent goal recall across a fresh session, offline material catch-up and forgetting.");
        }
        finally { Directory.Delete(root, true); } // Only this uniquely created synthetic fixture.
    }
    private static async Task<string> RunTurn(string question, CompanionMemory memory, string key, bool requireWrite = false)
    {
        var calls = new ConcurrentQueue<string>();
        var answers = new ConcurrentQueue<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RealtimeVoiceSession? session = null;
        await using (session = new RealtimeVoiceSession(() => new RealtimeWebSocketTransport(), () => key, () => "marin",
            async (call, token) =>
            {
                calls.Enqueue(call.Name);
                return call.Name switch
                {
                    "read_memory" or "get_game_context" => (await memory.RecallAsync(call.Question, token)).Json,
                    "update_memory" => JsonSerializer.Serialize(await memory.EditAsync(call.Memory!,
                        await session!.GetUserTranscriptAsync(call.Turn, token), token), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    _ => "{\"error\":\"Synthetic fixture: no research or other actions available. Use memory.\"}"
                };
            }))
        {
            session.TranscriptReceived += (_, role, text) => { if (role == "assistant") answers.Enqueue(text); };
            session.StatusChanged += s => { if (s.Text == "Conversation ready") done.TrySetResult(); };
            session.Faulted += f => done.TrySetException(new InvalidOperationException(f.Message));
            var turn = await session.BeginTurnAsync(new Dictionary<string, int>());
            await session.SubmitTextAsync(turn, question);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(55));
            if (requireWrite && !calls.Contains("update_memory")) throw new InvalidOperationException("Expected memory write tool.");
            if (!requireWrite && !calls.Contains("read_memory") && !calls.Contains("get_game_context"))
                throw new InvalidOperationException("Expected a fresh memory read.");
            var answer = string.Join(" ", answers);
            Console.WriteLine("QUESTION: " + question + "\nTOOLS: " + string.Join(", ", calls) + "\nANSWER: " + answer);
            return answer;
        }
    }
}

