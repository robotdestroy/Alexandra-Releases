namespace Alexandra.Core;

// Process/startup integrations are supplied by the Windows host. No game input or AI work.
public interface IGameAutoLaunchEnvironment
{
    bool Enabled { get; }
    bool TryGetGameSession(out string? session);
    bool CompanionRunning { get; }
    bool CompanionReady { get; }
    void LaunchCompanion();
    void RestoreCompanion();
}

public enum GameAutoLaunchResult { Disabled, Unknown, Waiting, AlreadyRunning, Launched, RetryLater, WaitingForReady, LaunchUnconfirmed }

public sealed class GameAutoLaunchCoordinator(IGameAutoLaunchEnvironment environment)
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    public const int MaximumAttempts = 3;
    private string? _session;
    private bool _handled;
    private DateTimeOffset _retryAfter;
    private int _attempts;

    public GameAutoLaunchResult Check(DateTimeOffset now)
    {
        if (!environment.Enabled) return GameAutoLaunchResult.Disabled;
        // An unreadable process list is not evidence that the game exited.
        if (!environment.TryGetGameSession(out var session)) return GameAutoLaunchResult.Unknown;
        if (!string.Equals(session, _session, StringComparison.Ordinal))
        { _session = session; _handled = false; _retryAfter = default; _attempts = 0; }
        if (session is null || _handled) return GameAutoLaunchResult.Waiting;
        if (environment.CompanionReady)
        {
            // Restore once without activating the window. A running process alone is not a ready UI.
            try { environment.RestoreCompanion(); }
            catch (Exception) { return GameAutoLaunchResult.RetryLater; }
            _handled = true;
            return _attempts > 0 ? GameAutoLaunchResult.Launched : GameAutoLaunchResult.AlreadyRunning;
        }
        if (environment.CompanionRunning) return GameAutoLaunchResult.WaitingForReady;
        if (now < _retryAfter) return GameAutoLaunchResult.RetryLater;
        if (_attempts >= MaximumAttempts) return GameAutoLaunchResult.LaunchUnconfirmed;
        _attempts++;
        _retryAfter = now + RetryDelay;
        try
        {
            environment.LaunchCompanion();
            // Process.Start can succeed even when the app immediately exits or an update restarts it.
            // Only a subsequent ready-window acknowledgement completes this game session's launch.
            return GameAutoLaunchResult.WaitingForReady;
        }
        catch (Exception)
        {
            return GameAutoLaunchResult.RetryLater;
        }
    }
}

public interface IGameAutoStartService
{
    void Apply(bool enabled);
}
