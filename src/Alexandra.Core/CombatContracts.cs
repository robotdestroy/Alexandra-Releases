namespace Alexandra.Core;

public enum CombatQueryKind { Live, Review }
public sealed record CombatContext(string Json, string EvidenceKey, DateTimeOffset ReadAt, bool LiveAvailable,
    CombatCalloutObservation? Callouts = null, string? DiagnosticState = null, DateTimeOffset? GameSessionAt = null,
    string? ContinuityKey = null, DockingContext? Docking = null);
// Null means no verifiable game process. Implemented at the Windows boundary, never in Core.
public interface IGameSessionSource { DateTimeOffset? StartedAt { get; } }
public interface ICombatContextSource
{
    CombatContext? Latest { get; }
    Task<CombatContext> ReadAsync(CancellationToken token);
}

// Gate only answers that actually used live combat evidence, never normal chat or after-action reviews.
public sealed class CombatReplyGuard
{
    private readonly object _gate = new();
    private int _turn = -1;
    private CombatContext? _evidence;
    private DateTimeOffset? _playbackStarted;
    public void Arm(int turn, CombatContext context, CombatQueryKind kind)
    {
        lock (_gate)
        {
            _turn = turn;
            _evidence = kind == CombatQueryKind.Live && context.LiveAvailable ? context : null;
            _playbackStarted = null;
        }
    }
    public bool CanPlay(int turn, CombatContext? current, DateTimeOffset now, bool startPlayback = false)
    {
        lock (_gate)
        {
            if (_turn != turn || _evidence is null) return true;
            // Answers describe a recent snapshot, not a stream of corrections. Routine
            // target/pip/health changes must not cancel a sentence that is being spoken.
            // The continuity key changes on session, ship/vehicle and location transitions.
            if (current is null || !current.LiveAvailable ||
                (current.ContinuityKey ?? current.EvidenceKey) != (_evidence.ContinuityKey ?? _evidence.EvidenceKey) ||
                now < _evidence.ReadAt || now < current.ReadAt || now - current.ReadAt > TimeSpan.FromSeconds(5)) return false;
            var allowed = _playbackStarted is { } started
                ? now >= started && now - started <= TimeSpan.FromSeconds(45)
                : now - _evidence.ReadAt <= TimeSpan.FromSeconds(15);
            if (allowed && startPlayback) _playbackStarted ??= now;
            return allowed;
        }
    }
    public void Clear() { lock (_gate) { _turn = -1; _evidence = null; _playbackStarted = null; } }
}
