using System.Text;
using System.Text.RegularExpressions;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

public sealed class MarkdownSessionLogWriter(string directory) : ISessionLogWriter
{
    public async Task WriteAsync(SessionLogFile file, CancellationToken token)
    {
        if (!Regex.IsMatch(file.Id, "^[A-F0-9]{24}$")) throw new ArgumentException("Invalid session identifier.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Session-{file.StartedAt.UtcDateTime:yyyy-MM-dd-HHmmss}-{file.Id}.md");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, file.Markdown, new UTF8Encoding(false), token);
        token.ThrowIfCancellationRequested();
        File.Move(temporary, path, true);
    }
}
