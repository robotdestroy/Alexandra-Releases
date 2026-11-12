namespace Alexandra.Core;

public sealed record SessionLogFile(string Id, DateTimeOffset StartedAt, string Markdown);

// Local writing only. Summaries contain selected game facts, never audio or a conversation transcript.
public interface ISessionLogWriter
{
    Task WriteAsync(SessionLogFile file, CancellationToken token);
}
