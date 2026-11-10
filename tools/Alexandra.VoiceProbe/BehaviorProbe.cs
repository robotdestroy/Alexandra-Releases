using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alexandra.Core;
using Alexandra.Infrastructure;

// Opt-in, real voice-model evaluation with entirely synthetic tools. No microphone, game files,
// browser, clipboard, audio playback or persistent conversation logs. --live --behavior is required.
internal static class BehaviorProbe
{
    public static async Task RunAsync(string key)
    {
        var calls = new ConcurrentQueue<VoiceToolCall>();
        var transcripts = new ConcurrentQueue<(int Turn, string Text)>();
        var audio = new ConcurrentDictionary<string, int>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cargo = 1;
        var missingCarrier = false;
        var unavailableResearch = false;
        var failures = new List<string>();
        await using var session = new RealtimeVoiceSession(() => new RealtimeWebSocketTransport(), () => key, () => "marin",
            (call, token) => {
                token.ThrowIfCancellationRequested();
                calls.Enqueue(call);
                if (call.Name == "get_game_context")
                    return Task.FromResult(JsonSerializer.Serialize(new {
                        Availability = "Synthetic fresh journal snapshot", gameData = new {
                            currentSystem = "Sol", observedAt = DateTimeOffset.UtcNow,
                            facts = new {
                                CarrierStats = missingCarrier ? null : new { Name = "Test Horizon", Callsign = "QZX-123" },
                                Cargo = new { timestamp = DateTimeOffset.UtcNow, Inventory = new[] { new { Name = "platinum", Count = cargo } } },
                                CommunityGoal = new { CurrentGoals = new[] { new {
                                    Title = "Test Mining Initiative", SystemName = "Sol", MarketName = "Test Port",
                                    PlayerContribution = 0, IsComplete = false
                                } } }
                            }
                        }
                    }));
                if (unavailableResearch) return Task.FromResult("{\"answer\":\"The public report is unavailable, so I couldn't verify the carrier's location.\",\"clipboardCopied\":false}");
                var carrierQuestion = call.Question.Contains("carrier", StringComparison.OrdinalIgnoreCase) || call.Question.Contains("QZX-123");
                return Task.FromResult(JsonSerializer.Serialize(new {
                    answer = carrierQuestion
                        ? "Inara last reported Test Horizon (QZX-123) in Colonia on August 15, 2026. This old report does not confirm its current position."
                        : "The Test Mining Initiative's special reward requires signing up and delivering ten tons of mined platinum to Test Port. One ton isn't enough; your recorded contribution is zero.",
                    clipboardCopied = false,
                    sources = new[] { new { title = "Synthetic test fixture", url = "https://example.com/test-fixture" } }
                }));
            }, new FailureLog());
        session.AudioReceived += frame => audio.AddOrUpdate(frame.ItemId, frame.Pcm.Length, (_, count) => count + frame.Pcm.Length);
        session.TranscriptReceived += (turn, role, text) => { if (role == "assistant") transcripts.Enqueue((turn, text)); };
        session.StatusChanged += status => { if (status.Text == "Conversation ready") completed.TrySetResult(); };
        session.Faulted += failure => completed.TrySetException(new InvalidOperationException(failure.Message));
        await session.ConnectAsync();
        Console.WriteLine("Live voice-policy evaluation. All game data and search answers are synthetic; nothing is played aloud.");

        async Task Check(string question, bool local, bool research, params string[] answerFragments)
        {
            completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var turn = await session.BeginTurnAsync(audio.ToDictionary(pair => pair.Key, pair => pair.Value / 48));
            await session.SubmitTextAsync(turn, question);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(55));
            var used = calls.Where(c => c.Turn == turn).ToArray();
            var answer = string.Join(" ", transcripts.Where(t => t.Turn == turn).Select(t => t.Text));
            var words = Regex.Matches(answer, @"\S+").Count;
            Console.WriteLine($"TURN {turn}: {question}\nTOOLS: {string.Join(", ", used.Select(c => c.Name))}\nANSWER ({words} words): {answer}");
            if (used.Any(c => c.Name == "get_game_context") != local) failures.Add($"Turn {turn}: fresh context policy.");
            if (used.Any(c => c.Name == "research_elite") != research) failures.Add($"Turn {turn}: automatic research policy.");
            if (words is 0 or > 45) failures.Add($"Turn {turn}: expected 1-45 words, got {words}.");
            if (Regex.IsMatch(answer, @"let me (?:quickly )?check|give me a moment|nice and tidy|you're good to go|I(?:'|’)ll (?:check|look)", RegexOptions.IgnoreCase))
                failures.Add($"Turn {turn}: unnecessary preamble/filler.");
            foreach (var fragment in answerFragments)
                if (!Regex.IsMatch(answer, fragment, RegexOptions.IgnoreCase)) failures.Add($"Turn {turn}: missing expected evidence '{fragment}'.");
            if (question.Contains("Where is my carrier") && research && !used.Any(c => c.Name == "research_elite" && c.Question.Contains("QZX-123")))
                failures.Add($"Turn {turn}: carrier lookup lost callsign.");
        }
        await Check("Testing, hello, how are you tonight?", false, false);
        await Check("How many tons of platinum am I carrying right now?", true, false, "one|1");
        cargo = 7;
        await Check("How much platinum am I carrying now?", true, false, "seven|7");
        await Check("Where is my carrier located right now?", true, true, "Colonia", "August|old|stale|outdated|last reported");
        await Check("Can one ton of platinum get me the special reward for the current mining community goal?", true, true, "ten|10", "mined");
        await Check("So I just sell it to the market?", false, false, "Test Port", "mined");
        unavailableResearch = true;
        await Check("Where is my carrier right now? Please verify again.", true, true, "can['’]t|couldn['’]t|cannot|could not|unable|unverified|unavailable");
        missingCarrier = true;
        await Check("What's my current credit balance?", true, false, "can['’]t|cannot|don['’]t|not|isn['’]t|unavailable|unknown");
        if (failures.Count > 0) throw new InvalidOperationException("Behavior evaluation failed:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS: eight live turns; concise replies, refreshed cargo, automatic carrier lookup, verified goal rules, honest missing data.");
    }
    private sealed class FailureLog : IDiagnosticLog
    {
        public void Write(string kind, string message, int? turn = null)
        {
            if (kind is "voice.response_failure" or "voice.response_retry" or "voice.api_error" or "voice.connection_error")
                Console.WriteLine(kind + ": " + LocalDiagnosticLog.Sanitize(message));
        }
    }
}
