using Alexandra.Core;
using Alexandra.Infrastructure;

internal static class PreviewProbe
{
    public static async Task RunAsync(string key)
    {
        Console.WriteLine("Synthetic voice samples only: no microphone, game context, playback or audio files.");
        var source = new RealtimeVoicePreviewSource(() => new RealtimeWebSocketTransport());
        foreach (var voice in VoiceCatalog.Names)
        {
            var audio = await source.GenerateAsync(voice, key, CancellationToken.None);
            if (audio.Length < 4800) throw new InvalidOperationException("Sample unexpectedly short.");
            Console.WriteLine($"PASS: {voice}, server-confirmed voice, completed {audio.Length / 48000d:F1}s audio.");
        }
    }
}
