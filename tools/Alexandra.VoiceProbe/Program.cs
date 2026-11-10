using System.Speech.Synthesis;
using System.IO;
using Alexandra.Infrastructure;
using NAudio.Wave;

if (args.Contains("--verify-clipboard")) { await ClipboardProbe.VerifyWindowsAsync(); return; }
if (args.Contains("--verify-records")) { await RecordsProbe.VerifyLocalAsync(); return; }
if (!args.Contains("--live")) { Console.WriteLine("Explicit --live flag required. This test uses API credits."); return; }
var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No API key available.");
if (args.Contains("--vision")) { await VisionProbe.RunAsync(key); return; }
if (args.Contains("--screens")) { await ScreenProbe.RunAsync(key); return; }
if (args.Contains("--clipboard")) { await ClipboardProbe.VerifyVoiceAsync(key); return; }
if (args.Contains("--behavior")) { await BehaviorProbe.RunAsync(key); return; }
if (args.Contains("--memory")) { await MemoryProbe.RunAsync(key); return; }
if (args.Contains("--records")) { await RecordsProbe.RunAsync(key); return; }
if (args.Contains("--paste")) { await PasteProbe.RunAsync(key); return; }
if (args.Contains("--previews")) { await PreviewProbe.RunAsync(key); return; }
if (args.Contains("--recovery")) { await RecoveryProbe.RunAsync(key); return; }
var toolCalls = 0;
long bytes = 0;
var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using var session = new RealtimeVoiceSession(() => new RealtimeWebSocketTransport(), () => key, () => "coral",
    (_, _) => { Interlocked.Increment(ref toolCalls); return Task.FromResult("{\"available\":false}"); });
session.AudioReceived += frame => Interlocked.Add(ref bytes, frame.Pcm.Length);
session.TranscriptReceived += (_, role, text) => Console.WriteLine(role + ": " + text);
session.StatusChanged += status => { if (status.Text == "Conversation ready") complete.TrySetResult(); };
session.Faulted += failure => complete.TrySetException(new InvalidOperationException(failure.Message));
await session.ConnectAsync();
Console.WriteLine("Connected. No microphone, game data, or screenshot sent.");
using var wav = new MemoryStream();
using (var synth = new SpeechSynthesizer())
{
    synth.SetOutputToWaveStream(wav);
    synth.Speak("Testing, hello, how are you tonight?");
}
using var input = new MemoryStream(wav.ToArray());
using var reader = new WaveFileReader(input);
using var resampler = new MediaFoundationResampler(reader, new WaveFormat(24000, 16, 1));
using var pcm = new MemoryStream();
var buffer = new byte[48000];
int count;
while ((count = resampler.Read(buffer, 0, buffer.Length)) > 0) pcm.Write(buffer, 0, count);
var turn = await session.BeginTurnAsync(new Dictionary<string, int>());
await session.SubmitAudioAsync(turn, pcm.ToArray());
await complete.Task.WaitAsync(TimeSpan.FromSeconds(45));
if (bytes == 0) throw new InvalidOperationException("No voice audio returned.");
Console.WriteLine("Greeting audio received: " + bytes + " bytes.");
complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
// Nothing was played aloud: truncate that assistant audio while retaining the user's greeting.
var followup = await session.BeginTurnAsync(new Dictionary<string, int>());
await session.SubmitTextAsync(followup, "I was just testing the microphone. What were the first words I said?");
await complete.Task.WaitAsync(TimeSpan.FromSeconds(45));
if (toolCalls != 0) throw new InvalidOperationException("Casual conversation unexpectedly invoked a lookup.");
Console.WriteLine("PASS: greeting, audio response, follow-up, zero research calls.");

