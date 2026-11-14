namespace Alexandra.Core;

// One short user-requested reminder, in the selected voice. No conversational context or fallback voice.
public sealed class ReminderPlayer(IReminderAudioSource source, IStreamingAudioPlayer player,
    TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private CancellationTokenSource? _active;
    private DateTimeOffset _nextAttempt;
    private bool _disposed;
    public bool IsActive => _active is not null;
    public event Action? Failed;
    public async Task TryPlayAsync(VoiceReminder reminder, string voice, string? key, Func<bool> stillRelevant,
        Func<CancellationToken, Task<bool>> claim)
    {
        if (_disposed || IsActive || _clock.GetUtcNow() < _nextAttempt || string.IsNullOrWhiteSpace(key) ||
            !VoiceCatalog.IsValid(voice) || !stillRelevant()) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _active = request;
        _nextAttempt = _clock.GetUtcNow().AddSeconds(20);
        try
        {
            var pcm = await source.GenerateAsync(reminder.Text, voice, key, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!stillRelevant() || !ReferenceEquals(_active, request)) return;
            if (pcm.Length == 0 || pcm.Length > VoiceCatalog.MaxPcmBytes || pcm.Length % 2 != 0)
                throw new InvalidOperationException("Invalid reminder audio.");
            if (!await claim(request.Token)) return; // Durable at-most-once before speech starts.
            request.Token.ThrowIfCancellationRequested();
            if (!stillRelevant() || !ReferenceEquals(_active, request)) return;
            player.Append("reminder", pcm);
            while (player.HasUnplayedAudio)
            {
                request.Token.ThrowIfCancellationRequested();
                if (!stillRelevant()) return;
                await (delay ?? Task.Delay)(TimeSpan.FromMilliseconds(80), request.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (ReferenceEquals(_active, request))
            { _nextAttempt = _clock.GetUtcNow().AddMinutes(2); Failed?.Invoke(); }
        }
        finally
        {
            if (ReferenceEquals(_active, request)) { StopPlayer(); _active = null; }
        }
    }
    public void Cancel()
    {
        var active = _active; _active = null; active?.Cancel(); StopPlayer();
    }
    private void StopPlayer() { try { player.Stop(); } catch (Exception) { } }
    public void Dispose() { if (_disposed) return; _disposed = true; Cancel(); player.Dispose(); }
}
