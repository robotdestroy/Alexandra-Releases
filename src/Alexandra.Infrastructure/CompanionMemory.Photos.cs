using Alexandra.Core;

namespace Alexandra.Infrastructure;

public sealed partial class CompanionMemory
{
    public async Task<SessionPhotoAnchor?> CapturePhotoAnchorAsync(DateTimeOffset capturedAt, DateTimeOffset gameStartedAt, CancellationToken token)
    {
        var anchor = await CaptureSessionLogAnchorAsync(capturedAt, gameStartedAt, token);
        if (anchor is null) return null;
        await _gate.WaitAsync(token);
        try
        {
            await SyncLocked(token);
            if (_document?.CurrentProfile is not { } scope || SessionScope(scope) != anchor.Scope || Current is not { } profile) return null;
            return new(anchor, Snapshot(scope).System, profile.Reminders.Station);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RecordSessionPhotoAsync(SessionPhotoAnchor anchor, SavedScreenshot photo, string caption, CancellationToken token)
    {
        if (sessionLogs is null || sessionLogsEnabled?.Invoke() == false || photo.FullPath is null ||
            !Path.IsPathFullyQualified(photo.FullPath) || Path.GetFileName(photo.FullPath) != photo.FileName ||
            !string.Equals(Path.GetExtension(photo.FullPath), ".png", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(photo.FullPath, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc) return false;
        await _gate.WaitAsync(token);
        try
        {
            await SyncLocked(token);
            if (_document?.CurrentProfile is not { } scope || SessionScope(scope) != anchor.Session.Scope) return false;
            var entry = new SessionPhoto(anchor.Session.SpokenAt.ToUniversalTime(), uri.AbsoluteUri,
                SessionLogbook.Clean(string.IsNullOrWhiteSpace(caption) ? "Game photo" : caption),
                anchor.System is null ? null : SessionLogbook.Clean(anchor.System),
                anchor.Station is null ? null : SessionLogbook.Clean(anchor.Station));
            if (_document.SessionLogs?.RecordPhoto(anchor.Session, entry) != true) return false;
            _dirty = true; await SaveLocked(token); await ExportSessions(token);
            return SessionLogError is null;
        }
        finally { _gate.Release(); }
    }
}
