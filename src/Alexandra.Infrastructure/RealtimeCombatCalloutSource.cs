using Alexandra.Core;

namespace Alexandra.Infrastructure;

public sealed class RealtimeCombatCalloutSource(Func<IRealtimeTransport> transportFactory) : ICombatCalloutAudioSource
{
    public Task<byte[]> GenerateAsync(CombatCalloutKind kind, string voice, string key, CancellationToken token) =>
        new RealtimeVoicePreviewSource(transportFactory).GenerateLineAsync(
            voice, key, CombatCalloutLines.Text(kind), verifyTranscript: true, token);
}
