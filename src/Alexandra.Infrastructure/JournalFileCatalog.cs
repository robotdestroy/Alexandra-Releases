using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Alexandra.Infrastructure;

// The first journal timestamp is authoritative. Legacy YYMMDD filenames sort AFTER
// modern YYYY-MM-DD filenames; neither alphabetical order nor copied-file mtime is chronology.
public sealed class JournalFileCatalog
{
    private readonly Dictionary<string, Header> _headers = new(StringComparer.OrdinalIgnoreCase);

    public string[] GetFiles(string directory)
    {
        var files = new DirectoryInfo(directory).GetFiles("Journal.*.log");
        var present = files.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in _headers.Keys.Where(k => !present.Contains(k)).ToArray()) _headers.Remove(old);
        return files.Select(file => (File: file, At: Timestamp(file)))
            .OrderBy(item => item.At).ThenBy(item => Part(item.File.Name))
            .ThenBy(item => item.File.Name, StringComparer.OrdinalIgnoreCase).Select(item => item.File.FullName).ToArray();
    }

    private DateTimeOffset Timestamp(FileInfo file)
    {
        if (_headers.TryGetValue(file.FullName, out var saved) && saved.Length == file.Length &&
            saved.Written == file.LastWriteTimeUtc && saved.Created == file.CreationTimeUtc) return saved.At;
        var at = FilenameTime(file.Name);
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[4096]; var count = stream.Read(bytes);
            var end = Array.IndexOf(bytes, (byte)'\n', 0, count);
            if (end >= 0)
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 0, end).Trim('\r', '\uFEFF'));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("timestamp", out var timestamp) &&
                    timestamp.ValueKind == JsonValueKind.String && timestamp.TryGetDateTimeOffset(out var value)) at = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        _headers[file.FullName] = new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc, at);
        return at;
    }

    private static DateTimeOffset FilenameTime(string name)
    {
        var pieces = name.Split('.');
        if (pieces.Length < 3) return DateTimeOffset.MinValue;
        var stamp = pieces[1];
        if (stamp.Length == 12 && stamp.All(char.IsDigit)) stamp = "20" + stamp;
        return DateTimeOffset.TryParseExact(stamp, ["yyyyMMddHHmmss", "yyyy-MM-dd'T'HHmmss", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue;
    }
    private static int Part(string name) => int.TryParse(name.Split('.')[^2], out var part) ? part : 0;
    private sealed record Header(long Length, DateTime Written, DateTime Created, DateTimeOffset At);
}
