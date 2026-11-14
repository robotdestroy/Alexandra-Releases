using System.Globalization;
using System.Text.RegularExpressions;

namespace Alexandra.Infrastructure;

public static partial class SessionLogCatalog
{
    public static string? FindMostRecent(string directory) => List(directory).FirstOrDefault();
    public static IReadOnlyList<string> List(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        // Session start time is stable even if an older log was edited, migrated
        // or re-exported more recently. Never open .tmp files or recovery backups.
        return Directory.EnumerateFiles(directory)
            .Select(path => (Path: Path.GetFullPath(path), Match: FileName().Match(Path.GetFileName(path))))
            .Where(file => file.Match.Success &&
                DateTime.TryParseExact(file.Match.Groups[1].Value, "yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _) &&
                (File.GetAttributes(file.Path) & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(file => file.Match.Groups[1].Value, StringComparer.Ordinal)
            .ThenByDescending(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.Path)
            .ToArray();
    }

    [GeneratedRegex(@"^Session-(\d{4}-\d{2}-\d{2}-\d{6})-[A-F0-9]{24}\.md$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileName();
}
