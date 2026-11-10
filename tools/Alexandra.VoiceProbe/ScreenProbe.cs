using System.Collections.Concurrent;
using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;

internal static class ScreenProbe
{
    // Synthetic screen/save results; no desktop capture, files, microphone or playback.
    public static async Task RunAsync(string key)
    {
        var calls = new ConcurrentQueue<VoiceToolCall>();
        var transcripts = new ConcurrentQueue<(int Turn, string Text)>();
        var audio = new ConcurrentDictionary<string, int>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failingSave = false;
        await using var session = new RealtimeVoiceSession(() => new RealtimeWebSocketTransport(), () => key, () => "marin",
            (call, token) => {
                token.ThrowIfCancellationRequested(); calls.Enqueue(call);
                return Task.FromResult(call.Name switch {
                    "save_screenshot" => failingSave
                        ? "{\"error\":\"Elite is not in front. No screenshot was saved.\"}"
                        : "{\"screenshotSaved\":true,\"folder\":\"Pictures / Alexandra\",\"fileName\":\"test.png\"}",
                    "inspect_game" => "{\"answer\":\"The panel says FUEL LOW. Refuel before another jump.\",\"clipboardCopied\":false}",
                    "get_game_context" => "{\"availability\":\"Synthetic data only\"}",
                    _ => "{\"answer\":\"Elite's standard screenshot shortcut is F10.\",\"clipboardCopied\":false}"
                });
            });
        session.AudioReceived += f => audio.AddOrUpdate(f.ItemId, f.Pcm.Length, (_, n) => n + f.Pcm.Length);
        session.TranscriptReceived += (turn, role, text) => { if (role == "assistant") transcripts.Enqueue((turn, text)); };
        session.StatusChanged += s => { if (s.Text == "Conversation ready") completed.TrySetResult(); };
        session.Faulted += f => completed.TrySetException(new InvalidOperationException(f.Message));
        await session.ConnectAsync();
        var cases = new[] {
            ("Take a screenshot of my current situation.", true, false),
            ("Explain this warning on my screen.", false, true),
            ("Take a screenshot and explain this warning.", true, true),
            ("How do I take a screenshot myself?", false, false),
            ("Don't look at my screen. Just say hello.", false, false),
            ("Take a screenshot please.", true, false)
        };
        for (var i = 0; i < cases.Length; i++)
        {
            failingSave = i == cases.Length - 1;
            var (question, save, inspect) = cases[i];
            completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var turn = await session.BeginTurnAsync(audio.ToDictionary(p => p.Key, p => p.Value / 48));
            await session.SubmitTextAsync(turn, question);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(55));
            var used = calls.Where(c => c.Turn == turn).ToArray();
            var answer = string.Join(" ", transcripts.Where(t => t.Turn == turn).Select(t => t.Text));
            Console.WriteLine($"TURN {turn}: {question}\nTOOLS: {string.Join(", ", used.Select(c => c.Name))}\nANSWER: {answer}");
            if (used.Any(c => c.Name == "save_screenshot") != save || used.Any(c => c.Name == "inspect_game") != inspect)
                throw new InvalidOperationException("Screen action selection did not match the user's intent.");
            if (answer.Length == 0) throw new InvalidOperationException("No spoken answer.");
            if (failingSave && !System.Text.RegularExpressions.Regex.IsMatch(answer, "not|isn['’]t|couldn['’]t|can['’]t|could not|no screenshot|unable", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Save failure was not disclosed.");
        }
        Console.WriteLine("PASS: saving, visual explanation, combined request, tutorial, refusal and failed-save handling.");
    }
}
