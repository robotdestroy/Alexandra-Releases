namespace Alexandra.Core;

public static class VoiceCatalog
{
    public const string DefaultVoice = "marin";
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[] {
        DefaultVoice, "coral", "cedar", "alloy", "ash", "sage", "ballad", "echo", "verse", "shimmer"
    });
    public const string SampleLine = "Hello Commander. I'm Alexandra, ready for our next adventure.";
    public const int MaxPcmBytes = 24000 * 2 * 12;
    public static bool IsValid(string voice) => Names.Contains(voice);
}
public interface IVoicePreviewSource
{
    // One complete 24-kHz, 16-bit, mono PCM sample. No microphone, game context or tools.
    Task<byte[]> GenerateAsync(string voice, string key, CancellationToken token);
}

// Dialog-scoped cache and cancellation. No settings changes, personal data, logs or files.
public sealed class VoicePreviewController(IVoicePreviewSource source, IStreamingAudioPlayer player,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _active;
    private int _generation;
    private bool _disposed;
    public event Action<string>? StatusChanged;

    public async Task PreviewAsync(string voice, string? key, bool consent)
    {
        CancellationTokenSource request;
        int generation;
        lock (_gate)
        {
            if (_disposed) return;
            CancelLocked();
            if (!consent) { StatusChanged?.Invoke("Enable cloud voice below to hear a preview."); return; }
            if (string.IsNullOrWhiteSpace(key)) { StatusChanged?.Invoke("Add an API key to hear a preview."); return; }
            if (!VoiceCatalog.IsValid(voice)) { StatusChanged?.Invoke("Choose a supported voice."); return; }
            request = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            _active = request;
            generation = _generation;
            StatusChanged?.Invoke($"Loading {voice} preview...");
        }
        var wait = delay ?? Task.Delay;
        try
        {
            // Arrow-key browsing and fast repeated choices should not generate a request for every row.
            await wait(TimeSpan.FromMilliseconds(300), request.Token);
            byte[]? sample;
            lock (_gate) _cache.TryGetValue(voice, out sample);
            sample ??= await source.GenerateAsync(voice, key, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (sample.Length == 0 || sample.Length > VoiceCatalog.MaxPcmBytes || sample.Length % 2 != 0)
                throw new InvalidOperationException("Invalid preview audio.");
            lock (_gate)
            {
                if (_disposed || generation != _generation) return;
                _cache[voice] = sample;
                player.Append("voice-preview", sample);
                StatusChanged?.Invoke($"Playing {voice} preview");
            }
            while (true)
            {
                request.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_disposed || generation != _generation) return;
                    if (!player.HasUnplayedAudio)
                    {
                        StopPlayer();
                        StatusChanged?.Invoke($"{voice} preview finished. Save to use this voice.");
                        return;
                    }
                }
                await wait(TimeSpan.FromMilliseconds(80), request.Token);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (!_disposed && generation == _generation)
                { StopPlayer(); StatusChanged?.Invoke("Voice preview timed out. Try again."); }
            }
        }
        catch (Exception)
        {
            lock (_gate)
            {
                if (!_disposed && generation == _generation)
                { StopPlayer(); StatusChanged?.Invoke("Couldn't play this preview. Check your API access and audio output, then try again."); }
            }
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_active, request)) _active = null;
            request.Dispose();
        }
    }
    public void Cancel(bool clearCache = false)
    {
        lock (_gate)
        {
            CancelLocked();
            if (clearCache) _cache.Clear();
            if (!_disposed) StatusChanged?.Invoke("");
        }
    }
    private void CancelLocked()
    {
        _generation++;
        _active?.Cancel(); // The request owns disposal once its work has unwound.
        _active = null;
        StopPlayer();
    }
    private void StopPlayer() { try { player.Stop(); } catch (Exception) { /* A disconnected audio device must not break Setup. */ } }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelLocked();
            _cache.Clear();
            try { player.Dispose(); } catch (Exception) { }
        }
    }
}
