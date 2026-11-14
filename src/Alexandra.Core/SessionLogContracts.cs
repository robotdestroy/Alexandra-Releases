namespace Alexandra.Core;

public sealed record SessionLogFile(string Id, DateTimeOffset StartedAt, string Markdown);
public sealed record SessionPhoto(DateTimeOffset At, string FileUri, string Caption, string? System, string? Station);
public sealed record SessionPhotoAnchor(SessionLogAnchor Session, string? System, string? Station);

// Local writing only. Summaries contain selected game facts, never audio or a conversation transcript.
public interface ISessionLogWriter
{
    Task WriteAsync(SessionLogFile file, CancellationToken token);
}
