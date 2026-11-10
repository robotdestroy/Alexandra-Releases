using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Alexandra.App;
using Alexandra.Core;
using Alexandra.Infrastructure;

internal static class ClipboardProbe
{
    // Opt-in desktop smoke test. Never log existing clipboard text, discard rich formats or clobber a concurrent user copy.
    public static Task VerifyWindowsAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () => {
                string? previous = null;
                var written = false;
                try
                {
                    var data = System.Windows.Clipboard.GetDataObject();
                    var allowed = new[] { DataFormats.Text, DataFormats.UnicodeText, DataFormats.StringFormat, DataFormats.Locale };
                    if (data is not null && data.GetFormats().Any(f => !allowed.Contains(f)))
                        throw new InvalidOperationException("Clipboard contains non-text formats; desktop test skipped to preserve them.");
                    previous = data?.GetData(DataFormats.UnicodeText) as string;
                    using var http = new HttpClient();
                    var clipboard = new WindowsSystemClipboard(dispatcher, () => true);
                    var service = new DestinationCopyService(new EdsmSystemResolver(http), clipboard);
                    var result = await service.CopySystemAsync("Sol", default);
                    written = result.ClipboardCopied;
                    if (!written || System.Windows.Clipboard.GetText() != "Sol")
                        throw new InvalidOperationException("Windows clipboard verification failed.");
                    var stale = new WindowsSystemClipboard(dispatcher, () => false);
                    try { await stale.TryCopyAsync("Colonia", default); throw new InvalidOperationException("Stale turn was not rejected."); }
                    catch (OperationCanceledException) { }
                    if (System.Windows.Clipboard.GetText() != "Sol") throw new InvalidOperationException("Stale copy changed clipboard.");
                    Console.WriteLine("PASS: EDSM validation, real Windows write/read-back, stale turn rejected.");
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally
                {
                    try
                    {
                        if (written && System.Windows.Clipboard.GetText() == "Sol")
                        {
                            if (previous is null) System.Windows.Clipboard.Clear();
                            else System.Windows.Clipboard.SetText(previous);
                            Console.WriteLine("Previous text clipboard restored (contents not logged).");
                        }
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    dispatcher.InvokeShutdown();
                    done.TrySetResult();
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    // Real voice-model tool selection, simulated clipboard: no audio playback, game data or clipboard writes.
    public static async Task VerifyVoiceAsync(string key)
    {
        var calls = new ConcurrentQueue<VoiceToolCall>();
        var transcripts = new ConcurrentQueue<(int Turn, string Text)>();
        var audio = new ConcurrentDictionary<string, int>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unavailable = false;
        await using var session = new RealtimeVoiceSession(() => new RealtimeWebSocketTransport(), () => key, () => "marin",
            (call, token) => {
                token.ThrowIfCancellationRequested(); calls.Enqueue(call);
                if (call.Name != "copy_system") return Task.FromResult("{\"error\":\"This test only supports copy_system. Sol is already identified.\"}");
                return Task.FromResult(JsonSerializer.Serialize(new {
                    clipboardCopied = !unavailable, copiedSystem = unavailable ? null : call.System,
                    clipboardStatus = unavailable ? "clipboard_unavailable" : "copied",
                    clipboardNote = unavailable ? "Windows could not confirm the clipboard update. Try copying again." : "System copied and read back successfully."
                }));
            });
        session.AudioReceived += f => audio.AddOrUpdate(f.ItemId, f.Pcm.Length, (_, n) => n + f.Pcm.Length);
        session.TranscriptReceived += (turn, role, text) => { if (role == "assistant") transcripts.Enqueue((turn, text)); };
        session.StatusChanged += s => { if (s.Text == "Conversation ready") completed.TrySetResult(); };
        session.Faulted += f => completed.TrySetException(new InvalidOperationException(f.Message));
        await session.ConnectAsync();
        foreach (var question in new[] { "Copy Sol to the clipboard so I can paste it in the galaxy map.", "Copy that system again, please.", "Please copy that system one more time." })
        {
            completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var turn = await session.BeginTurnAsync(audio.ToDictionary(p => p.Key, p => p.Value / 48));
            await session.SubmitTextAsync(turn, question);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(55));
            var used = calls.Where(c => c.Turn == turn).ToArray();
            var answer = string.Join(" ", transcripts.Where(t => t.Turn == turn).Select(t => t.Text));
            Console.WriteLine($"TURN {turn}: {question}\nTOOLS: {string.Join(", ", used.Select(c => c.Name + ":" + c.System))}\nANSWER: {answer}");
            if (used.Length != 1 || used[0].Name != "copy_system" || used[0].System != "Sol")
                throw new InvalidOperationException("Expected exactly one direct copy_system(Sol) action, without research.");
            if (answer.Length == 0) throw new InvalidOperationException("No voice response.");
            if (unavailable && !System.Text.RegularExpressions.Regex.IsMatch(answer, "couldn['’]t|could not|unable|not|can['’]t|couldn’t", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidOperationException("Clipboard failure was not disclosed.");
            if (turn == 2) unavailable = true;
        }
        Console.WriteLine("PASS: explicit copy, conversational copy follow-up, honest failed-copy response; no actual clipboard writes.");
    }
}
