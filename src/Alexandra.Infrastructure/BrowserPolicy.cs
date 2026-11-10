using System.Text.RegularExpressions;
namespace Alexandra.Infrastructure;

// Explicit research-only navigation. No arbitrary origins, account pages, executable URLs or writes.
public static partial class BrowserPolicy
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    { "inara.cz", "www.inara.cz", "edsm.net", "www.edsm.net", "elitedangerous.com", "www.elitedangerous.com",
      "elite-dangerous.fandom.com" };
    public static bool CanNavigate(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || !Hosts.Contains(uri.Host)) return false;
        var decoded = Uri.UnescapeDataString(uri.PathAndQuery);
        if (DangerPattern().IsMatch(decoded) || decoded.Contains('\\') || decoded.Contains("..")) return false;
        if (uri.Host.EndsWith("inara.cz", StringComparison.OrdinalIgnoreCase))
            return InaraPagePattern().IsMatch(uri.AbsolutePath);
        if (uri.Host.EndsWith("edsm.net", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith("/en/system", StringComparison.OrdinalIgnoreCase) ||
                   uri.AbsolutePath.StartsWith("/en/search", StringComparison.OrdinalIgnoreCase);
        if (uri.Host.Contains("fandom.com", StringComparison.OrdinalIgnoreCase)) return uri.AbsolutePath.StartsWith("/wiki/") && !uri.AbsolutePath.Contains(':');
        return uri.AbsolutePath.StartsWith("/news") || uri.AbsolutePath.StartsWith("/guides") || uri.AbsolutePath == "/";
    }
    public static bool CanRequest(string url, string method, string resourceType, bool navigation)
    {
        if (method is not ("GET" or "HEAD")) return false; // No account edits, messages or POST actions.
        if (navigation) return CanNavigate(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || !Hosts.Contains(uri.Host) || DangerPattern().IsMatch(Uri.UnescapeDataString(uri.PathAndQuery))) return false;
        // Images/fonts/media/third-party tracking are unnecessary for text-based research.
        return resourceType is "stylesheet" or "script" or "xhr" or "fetch";
    }
    public static bool IsBlockedPage(string title, string text, int? httpStatus = null) =>
        httpStatus is 401 or 403 or 429 || BlockPattern().IsMatch(title + "\n" + text[..Math.Min(text.Length, 4000)]);
    public static bool SafeLabel(string label) => !DangerPattern().IsMatch(label) && !BlockPattern().IsMatch(label);
    [GeneratedRegex(@"(?:^|[/=\s?&:_-])(login|log-in|signin|sign-in|logout|register|account|profile|settings|password|token|secret|auth|delete|remove|edit|upload|download|donate|purchase|subscribe|unsubscribe|post|reply|comment|send|vote|follow|validatechallenge|challenge|captcha)(?:$|[/=\s?&:_-])", RegexOptions.IgnoreCase)]
    private static partial Regex DangerPattern();
    [GeneratedRegex(@"^/elite/(?:commodities|commodity|nearest(?:-[a-z]+)?|station|starsystem|starsystems|stations|engineer|engineers|blueprint|blueprints|component|components|experimental|synthesis|techbroker|outfitting|ship|ships|module|modules|communitygoals|trade-route|traderoutes|materialtrader|materialtraders|search)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex InaraPagePattern();
    [GeneratedRegex(@"access check required|verify (?:that )?you(?:'re| are) (?:a )?human|confirm your visit|not a bot|captcha|access denied|just a moment|checking your browser|enable javascript and cookies", RegexOptions.IgnoreCase)]
    private static partial Regex BlockPattern();
}

