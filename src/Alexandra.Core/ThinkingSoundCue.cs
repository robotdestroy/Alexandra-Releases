namespace Alexandra.Core;

public interface IThinkingSoundPlayer : IDisposable
{
    void PlayPulse();
    void Stop();
}

// Driven by the UI's current turn/state, not delayed background tasks. No stale scheduled sound can fire.
public sealed class ThinkingSoundCue(IThinkingSoundPlayer player, TimeProvider? clock = null,
    Action<Exception>? onFailure = null) : IDisposable
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan PulseInterval = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int? _turn;
    private long? _waitingSince, _lastPulse;
    private bool _hasPlayback, _failed, _disposed;

    public void Update(int turn, bool waiting, bool speaking, bool enabled, bool blocked)
    {
        if (_disposed || _failed) return;
        if (!enabled || blocked || !waiting || speaking) { Reset(); return; }
        if (_turn != turn) { Reset(); _turn = turn; }
        if (_failed) return;
        var now = _clock.GetTimestamp();
        _waitingSince ??= now;
        if (_clock.GetElapsedTime(_waitingSince.Value, now) < InitialDelay) return;
        if (_lastPulse is { } last && _clock.GetElapsedTime(last, now) < PulseInterval) return;
        try
        {
            _hasPlayback = true; // Clean up even if starting the output device fails halfway through.
            player.PlayPulse();
            _lastPulse = now; // Never catch up missed pulses after a delayed UI tick.
        }
        catch (Exception ex) { Fail(ex); }
    }

    public void Reset()
    {
        _turn = null;
        _waitingSince = _lastPulse = null;
        if (!_hasPlayback) return;
        _hasPlayback = false;
        try { player.Stop(); }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception ex)
    {
        if (_failed) return;
        _failed = true; // Optional feedback must never break voice, cause repeated errors, or switch speakers.
        Reset();
        onFailure?.Invoke(ex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
        try { player.Dispose(); }
        catch (Exception ex) { Fail(ex); }
    }
}
