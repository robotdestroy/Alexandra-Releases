namespace Alexandra.Core;

public enum UpdatePhase { Unavailable, Idle, Checking, Downloading, Ready, Failed }
public sealed record AppUpdateState(UpdatePhase Phase, string Message, string? Version = null, int? Percent = null);

public interface IAppUpdateBackend
{
    string CurrentVersion { get; }
    string? UnavailableReason { get; }
    string? PendingVersion { get; }
    Task<string?> FindUpdateAsync(CancellationToken token);
    Task DownloadAsync(Action<int> progress, CancellationToken token);
    void ScheduleApplyAndRestart();
}

// Serializes manual and automatic checks. Downloading never interrupts a conversation or restarts the app.
public sealed class AppUpdateCoordinator(IAppUpdateBackend backend)
{
    private readonly SemaphoreSlim _gate = new(1);
    private int _operation;
    public string CurrentVersion => backend.CurrentVersion;
    public AppUpdateState State { get; private set; } = backend.UnavailableReason is { } reason
        ? new(UpdatePhase.Unavailable, reason)
        : new(UpdatePhase.Idle, "Updates are checked automatically. You can also check now.");
    public event Action<AppUpdateState>? Changed;

    private void Set(AppUpdateState state) { State = state; Changed?.Invoke(state); }

    public async Task CheckAndDownloadAsync(CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(0, token)) return;
        var operation = Interlocked.Increment(ref _operation);
        try
        {
            token.ThrowIfCancellationRequested();
            if (backend.UnavailableReason is { } reason) { Set(new(UpdatePhase.Unavailable, reason)); return; }
            if (backend.PendingVersion is { } pending) { Ready(pending); return; }
            Set(new(UpdatePhase.Checking, "Checking for updates…"));
            var version = await backend.FindUpdateAsync(token);
            token.ThrowIfCancellationRequested();
            if (version is null) { Set(new(UpdatePhase.Idle, $"Alexandra {CurrentVersion} is up to date.")); return; }
            Set(new(UpdatePhase.Downloading, $"Downloading Alexandra {version}…", version, 0));
            await backend.DownloadAsync(percent =>
            {
                if (operation == Volatile.Read(ref _operation) && !token.IsCancellationRequested && State.Phase == UpdatePhase.Downloading)
                    Set(new(UpdatePhase.Downloading, $"Downloading Alexandra {version}…", version, Math.Clamp(percent, 0, 100)));
            }, token);
            token.ThrowIfCancellationRequested();
            if (backend.PendingVersion is not { } downloaded) throw new InvalidOperationException("No verified update was staged.");
            Ready(downloaded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Set(new(UpdatePhase.Idle, "Update check cancelled. You can try again later.")); }
        catch (Exception)
        { Set(new(UpdatePhase.Failed, "Couldn't download an update. Your current app still works; check your connection and try again.")); }
        finally { Interlocked.Increment(ref _operation); _gate.Release(); }
    }

    private void Ready(string version) => Set(new(UpdatePhase.Ready,
        $"Alexandra {version} is ready. It will install next time you open the app, or you can restart now.", version));

    public void ScheduleApplyAndRestart()
    {
        if (State.Phase != UpdatePhase.Ready || backend.PendingVersion is null)
            throw new InvalidOperationException("No downloaded update is ready.");
        backend.ScheduleApplyAndRestart();
    }
}

public static class UpdateFeedPolicy
{
    public static bool IsPublicGitHubRepository(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length == 2 && segments.All(s => s.Length > 0 && s is not ("." or "..") &&
            s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }
}
