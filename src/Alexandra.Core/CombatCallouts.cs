namespace Alexandra.Core;

public enum CombatCalloutKind { ShieldsLost, LowHull, CriticalHull, HeatDamage, Overheating }
public static class CombatCalloutLines
{
    public const int MaxPcmBytes = 24000 * 2 * 6;
    public static string Text(CombatCalloutKind kind) => kind switch {
        CombatCalloutKind.ShieldsLost => "Shields down.",
        CombatCalloutKind.LowHull => "Low hull. Forty percent or less.",
        CombatCalloutKind.CriticalHull => "Critical hull. Twenty percent or less.",
        CombatCalloutKind.HeatDamage => "Heat damage detected.",
        CombatCalloutKind.Overheating => "Overheating.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
public sealed record CombatCalloutObservation(string ContextId, bool Eligible, bool CombatActive, bool? ShieldsUp,
    DateTimeOffset StatusAt, DateTimeOffset? HullDamageAt, double? HullPercent, DateTimeOffset? HeatDamageAt,
    DateTimeOffset? HeatWarningAt = null, bool? Overheating = null);
public sealed record CombatCallout(CombatCalloutKind Kind, string ContextId, DateTimeOffset At);

// Edge-triggered, fail-closed and bounded. Busy/disabled observations are consumed, never queued for later.
public sealed class CombatCalloutPolicy
{
    private CombatCalloutObservation? _previous;
    private DateTimeOffset? _observedAt, _lastAttempt;
    private readonly Dictionary<CombatCalloutKind, DateTimeOffset> _attempts = [];
    public CombatCallout? Observe(CombatContext? context, DateTimeOffset now, bool enabled, bool quiet)
    {
        if (!enabled || !Fresh(context, now))
        { _previous = null; _observedAt = now; return null; }
        var next = context!.Callouts!;
        var previous = _previous;
        _previous = next;
        var continuous = _observedAt is { } last && now >= last && now - last <= TimeSpan.FromSeconds(3);
        _observedAt = now;
        if (!continuous || previous is null || previous.ContextId != next.ContextId || !quiet || !next.CombatActive) return null;
        CombatCallout? candidate = null;
        // Prefer one higher-value warning if several events arrive in the same poll.
        if (next.HullDamageAt is { } hullAt && Recent(hullAt, now, 5) &&
            (next.HullDamageAt != previous.HullDamageAt || next.HullPercent != previous.HullPercent))
        {
            if (next.HullPercent is >= 0 and <= 20 && !(previous.HullPercent is <= 20))
                candidate = new(CombatCalloutKind.CriticalHull, next.ContextId, hullAt);
            else if (next.HullPercent is > 20 and <= 40 && !(previous.HullPercent is <= 40))
                candidate = new(CombatCalloutKind.LowHull, next.ContextId, hullAt);
        }
        if (candidate is null && previous.ShieldsUp == true && next.ShieldsUp == false && Recent(next.StatusAt, now, 5))
            candidate = new(CombatCalloutKind.ShieldsLost, next.ContextId, next.StatusAt);
        if (candidate is null && next.HeatDamageAt is { } heatAt && next.HeatDamageAt != previous.HeatDamageAt && Recent(heatAt, now, 5))
            candidate = new(CombatCalloutKind.HeatDamage, next.ContextId, heatAt);
        if (candidate is null && next.HeatWarningAt is { } warningAt && next.HeatWarningAt != previous.HeatWarningAt &&
            Recent(warningAt, now, 5) && !(next.Overheating == false && next.StatusAt >= warningAt))
            candidate = new(CombatCalloutKind.Overheating, next.ContextId, warningAt);
        if (candidate is null && previous.Overheating == false && next.Overheating == true && Recent(next.StatusAt, now, 5))
            candidate = new(CombatCalloutKind.Overheating, next.ContextId, next.StatusAt);
        if (candidate is null || _lastAttempt is { } attempt && now - attempt < TimeSpan.FromSeconds(candidate.Kind == CombatCalloutKind.CriticalHull ? 5 : 20) ||
            _attempts.TryGetValue(candidate.Kind, out var category) && now - category < TimeSpan.FromSeconds(90)) return null;
        _lastAttempt = now; _attempts[candidate.Kind] = now;
        return candidate;
    }
    public static bool IsRelevant(CombatCallout callout, CombatContext? context, DateTimeOffset now)
    {
        if (!Fresh(context, now) || !Recent(callout.At, now, 8)) return false;
        var state = context!.Callouts!;
        if (state.ContextId != callout.ContextId || !state.CombatActive) return false;
        return callout.Kind switch {
            CombatCalloutKind.ShieldsLost => state.ShieldsUp == false,
            CombatCalloutKind.LowHull => state.HullDamageAt == callout.At && state.HullPercent is > 20 and <= 40,
            CombatCalloutKind.CriticalHull => state.HullDamageAt == callout.At && state.HullPercent is >= 0 and <= 20,
            CombatCalloutKind.HeatDamage => state.HeatDamageAt is { } at && at >= callout.At && Recent(at, now, 5),
            CombatCalloutKind.Overheating => !(state.Overheating == false && state.StatusAt >= callout.At) &&
                (state.Overheating == true || state.HeatWarningAt == callout.At && Recent(callout.At, now, 5)),
            _ => false
        };
    }
    public void Reset() { _previous = null; _observedAt = null; } // Retain cooldowns across settings/voice changes.
    private static bool Fresh(CombatContext? c, DateTimeOffset now) =>
        c is { LiveAvailable: true, Callouts.Eligible: true } && Recent(c.ReadAt, now, 3);
    private static bool Recent(DateTimeOffset at, DateTimeOffset now, int seconds) =>
        at <= now && now - at <= TimeSpan.FromSeconds(seconds);
}

public interface ICombatCalloutAudioSource
{
    // Only these fixed, app-owned phrases; no game records, conversation, microphone or arbitrary model text.
    Task<byte[]> GenerateAsync(CombatCalloutKind kind, string voice, string key, CancellationToken token);
}

// Owns a separate audio player, so callouts cannot change/truncate the conversational history.
public sealed class CombatCalloutPlayer(ICombatCalloutAudioSource source, IStreamingAudioPlayer player,
    TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    private readonly Dictionary<(string, CombatCalloutKind), byte[]> _cache = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private CancellationTokenSource? _active;
    private DateTimeOffset? _failedAt;
    private bool _disposed;
    public bool IsActive => _active is not null;
    public event Action? Failed;
    // Called on the owning UI context. The recheck runs before and throughout playback.
    public async Task TryPlayAsync(CombatCallout callout, string voice, string? key, bool allowed, Func<bool> stillRelevant)
    {
        if (_disposed || IsActive || !allowed || string.IsNullOrWhiteSpace(key) || !VoiceCatalog.IsValid(voice) ||
            !stillRelevant() || _failedAt is { } failed && _clock.GetUtcNow() - failed < TimeSpan.FromMinutes(2)) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _active = request;
        try
        {
            if (!_cache.TryGetValue((voice, callout.Kind), out var pcm))
            {
                pcm = await source.GenerateAsync(callout.Kind, voice, key, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (_disposed || !ReferenceEquals(_active, request)) return;
                if (pcm.Length == 0 || pcm.Length > CombatCalloutLines.MaxPcmBytes || pcm.Length % 2 != 0)
                    throw new InvalidOperationException("Invalid callout audio.");
                _cache[(voice, callout.Kind)] = pcm;
            }
            if (!stillRelevant()) return;
            player.Append("combat-callout", pcm);
            while (player.HasUnplayedAudio)
            {
                request.Token.ThrowIfCancellationRequested();
                if (!stillRelevant()) return;
                await (delay ?? Task.Delay)(TimeSpan.FromMilliseconds(80), request.Token);
            }
        }
        catch (OperationCanceledException) { } // Suppressed rather than replayed after PTT or a changed situation.
        catch (Exception)
        { if (ReferenceEquals(_active, request)) { _failedAt = _clock.GetUtcNow(); Failed?.Invoke(); } }
        finally
        {
            if (ReferenceEquals(_active, request)) { StopPlayer(); _active = null; }
        }
    }
    public void Cancel(bool clearCache = false)
    {
        var active = _active; _active = null; active?.Cancel();
        StopPlayer();
        if (clearCache) _cache.Clear();
    }
    private void StopPlayer() { try { player.Stop(); } catch (Exception) { } }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Cancel(true);
        try { player.Dispose(); } catch (Exception) { }
    }
}
