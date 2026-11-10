using System.Text.Json;
using System.Text.RegularExpressions;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

// Text-only, bounded local diagnostics. Never pass raw request bodies, audio, screenshots or journals.
public sealed partial class LocalDiagnosticLog(string directory, Func<bool> enabled) : IDiagnosticLog
{
    private readonly object _sync = new();
    private readonly string _session = Guid.NewGuid().ToString("N")[..10];
    private int _part;
    public string DirectoryPath => Path.GetFullPath(directory);
    public string? LastError { get; private set; }
    public void Write(string kind, string message, int? turn = null)
    {
        if (!enabled()) return;
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                Prune();
                var path = Path.Combine(DirectoryPath, $"session-{_session}-{_part:D3}.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    path = Path.Combine(DirectoryPath, $"session-{_session}-{++_part:D3}.jsonl");
                File.AppendAllText(path, JsonSerializer.Serialize(new {
                    utc = DateTimeOffset.UtcNow, session = _session, turn,
                    kind = Sanitize(kind), message = Sanitize(message)
                }) + Environment.NewLine);
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { LastError = "Could not write local diagnostics. Check access to the log folder."; }
        }
    }
    public string ReadRecent()
    {
        lock (_sync)
        {
            if (!Directory.Exists(DirectoryPath)) return "";
            var files = LogFiles().OrderByDescending(f => f.LastWriteTimeUtc).Take(4).Reverse();
            var text = string.Join(Environment.NewLine, files.Select(f => File.ReadAllText(f.FullName)));
            return text.Length > 300_000 ? text[^300_000..] : text;
        }
    }
    public void Clear()
    {
        lock (_sync)
        {
            // Only this feature's own exact file pattern; never delete the containing settings folder.
            foreach (var file in LogFiles()) file.Delete();
            _part++;
        }
    }
    private IEnumerable<FileInfo> LogFiles() => Directory.Exists(DirectoryPath)
        ? new DirectoryInfo(DirectoryPath).EnumerateFiles("session-*.jsonl", SearchOption.TopDirectoryOnly)
        : [];
    private void Prune()
    {
        var files = LogFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7) || total > 10_000_000) file.Delete();
        }
    }
    public static string Sanitize(string value)
    {
        value = SecretPattern().Replace(value, "[REDACTED]");
        value = TokenPattern().Replace(value, "$1[REDACTED]");
        value = DataPattern().Replace(value, "[BINARY OMITTED]");
        return value.Length > 12_000 ? value[..12_000] + " [truncated]" : value;
    }
    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();
    [GeneratedRegex(@"((?:Bearer\s+|(?:api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|password|token|secret)\s*[=:]\s*[""']?))[^\s""'&,}]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
    [GeneratedRegex(@"data:(?:image|audio)/[^\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex DataPattern();
}

