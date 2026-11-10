using System.Collections.Concurrent;
using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;

internal static class PasteProbe
{
    // Synthetic clipboard and driver: no Windows input, game access, screenshots or playback.
    public static async Task RunAsync(string key)
    {
        var memory = new CopiedDestinationMemory();
        var driver = new SimulatedPaste();
        var paste = new DestinationPasteService(memory, driver);
        var copy = new DestinationCopyService(new Resolver(), new SimulatedClipboard(memory));
        var calls = new ConcurrentQueue<VoiceToolCall>();
        var transcripts = new ConcurrentQueue<(int Turn, string Text)>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RealtimeVoiceSession? session = null;
        await using (session = new RealtimeVoiceSession(() => new RealtimeWebSocketTransport(), () => key, () => "marin",
            async (call, token) => {
                calls.Enqueue(call);
                object result = call.Name switch {
                    "copy_system" => await copy.CopySystemAsync(call.System, token),
                    "paste_destination" => await paste.PasteAsync(await session!.GetUserTranscriptAsync(call.Turn, token), token),
                    _ => new { error = "This fixture only supports copy and paste. Do not use other tools." }
                };
                return JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }))
        {
            session.TranscriptReceived += (turn, role, text) => { if (role == "assistant") transcripts.Enqueue((turn, text)); };
            session.StatusChanged += s => { if (s.Text == "Conversation ready") completed.TrySetResult(); };
            session.Faulted += f => completed.TrySetException(new InvalidOperationException(f.Message));
            var cases = new (string Question, string? Tool, int DriverCalls)[] {
                ("Copy Sol so I can paste it in the galaxy map.", "copy_system", 0),
                ("Paste that.", "paste_destination", 1),
                ("How do I paste a location?", null, 1),
                ("Don't paste anything.", null, 1),
                ("Paste that again please.", "paste_destination", 2),
                ("Paste that.", "paste_destination", 2)
            };
            foreach (var item in cases)
            {
                completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (item.DriverCalls == 2) driver.ClipboardChanged = true;
                var turn = await session.BeginTurnAsync(new Dictionary<string, int>());
                await session.SubmitTextAsync(turn, item.Question);
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(55));
                var used = calls.Where(c => c.Turn == turn).ToArray();
                var answer = string.Join(" ", transcripts.Where(t => t.Turn == turn).Select(t => t.Text));
                Console.WriteLine($"QUESTION: {item.Question}\nTOOLS: {string.Join(", ", used.Select(c => c.Name))}\nANSWER: {answer}");
                if (item.Tool is null ? used.Length != 0 : used.Length != 1 || used[0].Name != item.Tool)
                    throw new InvalidOperationException("Unexpected voice tool selection.");
                if (driver.Calls != item.DriverCalls) throw new InvalidOperationException("Unexpected simulated input.");
                if (answer.Length == 0) throw new InvalidOperationException("No spoken response.");
            }
        }
        Console.WriteLine("PASS: copy-only request, explicit paste, no input for tutorial/refusal, changed clipboard and missing provenance.");
    }
    private sealed class Resolver : ISystemResolver
    {
        public Task<string?> ResolveAsync(string candidate, CancellationToken token) =>
            Task.FromResult<string?>(candidate.Equals("Sol", StringComparison.OrdinalIgnoreCase) ? "Sol" : null);
    }
    private sealed class SimulatedClipboard(CopiedDestinationMemory memory) : ISystemClipboard
    {
        public Task<bool> TryCopyAsync(string system, CancellationToken token)
        { token.ThrowIfCancellationRequested(); memory.Remember(system, 42); return Task.FromResult(true); }
    }
    private sealed class SimulatedPaste : IDestinationPasteDriver
    {
        public int Calls { get; private set; }
        public bool ClipboardChanged { get; set; }
        public Task<DestinationPasteResult> PasteAsync(CopiedDestination destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(ClipboardChanged ? DestinationPasteResult.Blocked("clipboard_changed", "The clipboard changed. Ask me to copy the destination again.")
                : new DestinationPasteResult(true, destination.System, "paste_sent", "Paste shortcut sent. Text and route not verified; no Enter sent."));
        }
    }
}
