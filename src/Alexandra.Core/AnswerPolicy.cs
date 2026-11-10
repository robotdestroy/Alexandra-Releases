using System.Text.RegularExpressions;

namespace Alexandra.Core;

public static partial class AnswerPolicy
{
    public static bool IsSystemName(string? value) => value is { Length: > 0 and <= 120 } &&
        value == value.Trim() && !value.Any(char.IsControl) &&
        !value.Contains("://", StringComparison.Ordinal) && NamePattern().IsMatch(value);
    [GeneratedRegex(@"^[\p{L}\p{N} .+'()\-]+$")]
    private static partial Regex NamePattern();

    public static bool IsPublicLink(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.IsLoopback ||
            uri.UserInfo.Length > 0)
            return false;
        return uri.Host.Contains('.') && !System.Net.IPAddress.TryParse(uri.Host, out _)
            && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasDestinationEvidence(Answer answer) =>
        answer.Destination is { } d && IsSystemName(d.System) &&
        IsPublicLink(d.EvidenceUrl) && answer.Sources.Any(s =>
            string.Equals(s.Url, d.EvidenceUrl, StringComparison.Ordinal));

    public static bool WantsVisualContext(string question) => ScreenRequestPolicy.CanInspect(question);
}

