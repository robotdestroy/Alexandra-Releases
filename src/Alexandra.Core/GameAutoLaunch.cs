namespace Alexandra.Core;

// Process/startup integrations are supplied by the Windows host. No game input or AI work.
public interface IGameAutoLaunchEnvironment
{
    bool Enabled { get; }
    bool TryGetGameSession(out string? session);
    bool CompanionRunning { get; }
    void LaunchCompanion();
}

public enum GameAutoLaunchResult { Disabled, Unknown, Waiting, AlreadyRunning, Launched, RetryLater }

public sealed class GameAutoLaunchCoordinator(IGameAutoLaunchEnvironment environment)
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private string? _session;
    private bool _handled;
    private DateTimeOffset _retryAfter;

    public GameAutoLaunchResult Check(DateTimeOffset now)
    {
        if (!environment.Enabled) return GameAutoLaunchResult.Disabled;
        // An unreadable process list is not evidence that the game exited.
        if (!environment.TryGetGameSession(out var session)) return GameAutoLaunchResult.Unknown;
        if (!string.Equals(session, _session, StringComparison.Ordinal))
        { _session = session; _handled = false; _retryAfter = default; }
        if (session is null || _handled) return GameAutoLaunchResult.Waiting;
        if (environment.CompanionRunning)
        {
            _handled = true;
            return GameAutoLaunchResult.AlreadyRunning;
        }
        if (now < _retryAfter) return GameAutoLaunchResult.RetryLater;
        try
        {
            environment.LaunchCompanion();
            // Closing Alexandra is intentional. Do not reopen it during this game session.
            _handled = true;
            return GameAutoLaunchResult.Launched;
        }
        catch (Exception)
        {
            _retryAfter = now + RetryDelay;
            return GameAutoLaunchResult.RetryLater;
        }
    }
}

public interface IGameAutoStartService
{
    void Apply(bool enabled);
}
