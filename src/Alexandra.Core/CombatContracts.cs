namespace Alexandra.Core;

public enum CombatQueryKind { Live, Review }
public sealed record CombatContext(string Json, string EvidenceKey, DateTimeOffset ReadAt, bool LiveAvailable,
    CombatCalloutObservation? Callouts = null, string? DiagnosticState = null);
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
    public void Arm(int turn, CombatContext context, CombatQueryKind kind)
    {
        lock (_gate)
        {
            _turn = turn;
            _evidence = kind == CombatQueryKind.Live && context.LiveAvailable ? context : null;
        }
    }
    public bool CanPlay(int turn, CombatContext? current, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_turn != turn || _evidence is null) return true;
            return current is not null && current.EvidenceKey == _evidence.EvidenceKey && current.LiveAvailable &&
                now >= _evidence.ReadAt && now - _evidence.ReadAt <= TimeSpan.FromSeconds(12) &&
                now >= current.ReadAt && now - current.ReadAt <= TimeSpan.FromSeconds(3);
        }
    }
    public void Clear() { lock (_gate) { _turn = -1; _evidence = null; } }
}
