using System.Text.RegularExpressions;

namespace Alexandra.Core;

// Conservative local extraction of first-person gameplay plans, not a conversation archive.
// Keep the player's words; never turn a plan into a game fact or a completed goal.
public static class SessionIntentPolicy
{
    private static readonly Regex Boundary = new(@"[.!?;\r\n]+|\b(?:and\s+then|then)\b|\band\s+(?=I(?:'m| am| will|'ll| plan| want| intend)\b)", RegexOptions.IgnoreCase);
    private static readonly Regex Declaration = new(@"\b(?:I(?:'m| am| will|'ll| plan to| want to| intend to)|my (?:plan|goal|priority)(?: (?:tonight|today|this session))? is)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Action = new(@"\b(?:going to|gonna|about to|trying to|working on|turn(?:ing)? in|collect(?:ing)?|claim(?:ing)?|redeem(?:ing)?|fight(?:ing)?|kill(?:ing)?|hunt(?:ing)?|combat|mine|mining|explor\w*|trade|trading|buy(?:ing)?|sell(?:ing)?|deliver\w*|haul\w*|build\w*|engineer\w*|upgrad\w*|unlock\w*|complet\w*|head\w*|travel\w*|jump\w*|visit\w*|land\w*|dock\w*)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Gameplay = new(@"\b(?:community (?:goal|event)s?|combat|conflict zones?|bount\w*|bonds?|pirates?|coalition|faction|missions?|material\w*|engineer\w*|mining|explor\w*|exobiolog\w*|cargo|commodit\w*|trade|trading|gold|palladium|steel|credits?|stations?|systems?|carriers?|ships?|modules?|outfit\w*|colonis\w*|coloniz\w*|guardian|thargoid\w*)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Excluded = new(@"\b(?:maybe|perhaps|might|could|hypothetically|suppose|what if|if I|should I|can I|would I|don't|do not|won't|will not|not going|not doing|no longer|never|he said|she said|you said|they said|example|quote|password|secret|api key|access token|credit card|social security)\b|sk-|https?://|\S+@\S+", RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> Extract(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript) || transcript.Length > 6000) return [];
        var normalized = Regex.Replace(transcript.Replace('’', '\''), @"[\p{Cc}\p{Cf}\s]+", " ").Trim();
        // Negated/tentative speech and questions must not silently become retained plans.
        if (Excluded.IsMatch(normalized) || normalized.Contains('?')) return [];
        var result = new List<string>();
        foreach (var clause in Boundary.Split(normalized))
        {
            var match = Declaration.Match(clause);
            if (!match.Success) continue;
            var text = clause[match.Index..].Trim(' ', ',', ':', '"');
            if (text.Length is < 8 or > 360 || !Action.IsMatch(text) || !Gameplay.IsMatch(text)) continue;
            if (!result.Contains(text, StringComparer.OrdinalIgnoreCase)) result.Add(text);
            if (result.Count == 4) break;
        }
        return result;
    }
}

public sealed record SessionLogAnchor(string Scope, string SessionId, DateTimeOffset SpokenAt);
