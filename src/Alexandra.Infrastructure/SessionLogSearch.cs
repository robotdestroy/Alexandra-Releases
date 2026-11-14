using System.Text;
using System.Text.RegularExpressions;

namespace Alexandra.Infrastructure;

public sealed record SessionLogSearchResult(IReadOnlyList<string> Paths, int UnreadableCount);

/// <summary>Local, read-only session search. It never follows photo links or calls a service.</summary>
public static partial class SessionLogSearch
{
    public const int MaximumFileBytes = 2 * 1024 * 1024;

    public static async Task<SessionLogSearchResult> FilterAsync(IReadOnlyList<string> paths, string query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query)) return new(paths.ToArray(), 0);
        var search = new Query(query);
        var matches = new List<string>();
        var unreadable = 0;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { unreadable++; continue; }
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumFileBytes) { unreadable++; continue; }
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                // Bound even a file that grows during the read. Never retain whole journals in a disk index.
                var buffer = new char[(int)stream.Length + 1];
                var count = await reader.ReadBlockAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == buffer.Length) { unreadable++; continue; }
                if (search.Matches(new string(buffer, 0, count))) matches.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
        }
        token.ThrowIfCancellationRequested();
        return new(matches, unreadable);
    }

    public static bool Matches(string markdown, string query) => new Query(query).Matches(markdown);

    private sealed class Query
    {
        private readonly string[] _terms;
        private readonly string[] _shipNames;
        public Query(string query)
        {
            var normal = Normalize(query);
            _terms = normal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var alias = Normalize(SessionLogPresentation.ShipName(query.Trim()));
            // A ship-name query means a ship actually recorded as used, not a ship mentioned in a goal.
            _shipNames = normal.Length >= 3 && normal != "SHIP" ? SessionLogPresentation.KnownShipNames
                .Select(Normalize).Where(name => (" " + name + " ").Contains(" " + normal + " ", StringComparison.Ordinal) ||
                    alias != normal && name == alias).Distinct().ToArray() : [];
        }
        public bool Matches(string markdown)
        {
            if (_terms.Length == 0) return true;
            var formatted = SessionLogPresentation.Format(markdown);
            if (_shipNames.Length > 0)
            {
                var end = formatted.IndexOf("\n## ", StringComparison.Ordinal);
                var header = end < 0 ? formatted : formatted[..end];
                var ships = string.Join(" ", ShipLines().Matches(header).Select(match => match.Groups[1].Value));
                var normalShips = Normalize(ships);
                return _shipNames.Any(name => normalShips.Contains(name, StringComparison.Ordinal));
            }
            // Search readable content, not the user's local paths or web-link targets.
            var text = Normalize(LinkTargets().Replace(formatted, ""));
            return _terms.All(term => text.Contains(term, StringComparison.Ordinal));
        }
    }

    private static string Normalize(string text) => Separators().Replace(text.Normalize(NormalizationForm.FormKC), " ").Trim().ToUpperInvariant();
    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Separators();
    [GeneratedRegex(@"(?m)^- Ships?(?: used)?: ([^\r\n]+)")]
    private static partial Regex ShipLines();
    [GeneratedRegex(@"(?<=\])\((?:<[^>\r\n]*>|[^)\r\n]*)\)")]
    private static partial Regex LinkTargets();
}
