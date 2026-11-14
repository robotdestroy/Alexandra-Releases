using Alexandra.Core;

namespace Alexandra.Infrastructure;

public sealed class RealtimeReminderAudioSource(Func<IRealtimeTransport> transportFactory) : IReminderAudioSource
{
    public Task<byte[]> GenerateAsync(string text, string voice, string key, CancellationToken token) =>
        new RealtimeVoicePreviewSource(transportFactory).GenerateLineAsync(
            voice, key, "Reminder: " + text.Trim().TrimEnd('.') + ".", verifyTranscript: true, token);
}
