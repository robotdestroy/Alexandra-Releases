using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Alexandra.Infrastructure;

// Session-wide cooldown survives new PTT turns. Accessed under the voice session's lock.
public sealed partial class RealtimeCooldown(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _resets = [];
    private DateTimeOffset _notBefore;
    public static readonly TimeSpan MaximumAutomaticWait = TimeSpan.FromSeconds(60);
    public TimeSpan Remaining => _notBefore > _clock.GetUtcNow() ? _notBefore - _clock.GetUtcNow() : TimeSpan.Zero;
    public void Observe(JsonElement e)
    {
        if (!e.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Array) return;
        foreach (var limit in limits.EnumerateArray())
        {
            if (!limit.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                name.GetString() is not ("tokens" or "requests") ||
                !limit.TryGetProperty("reset_seconds", out var reset) || reset.ValueKind != JsonValueKind.Number || !reset.TryGetDouble(out var seconds) ||
                !double.IsFinite(seconds) || seconds < 0 || seconds > 86400) continue;
            _resets[name.GetString()!] = _clock.GetUtcNow().AddSeconds(seconds);
        }
    }
    public TimeSpan Record(RealtimeFailure failure, int previousRetries = 0)
    {
        var hint = RetryHint(failure.Message);
        if (hint == TimeSpan.MaxValue) { _notBefore = DateTimeOffset.MaxValue; return Remaining; }
        var seconds = hint?.TotalSeconds ??
            (_resets.TryGetValue(failure.Type ?? "tokens", out var reset)
                && reset > _clock.GetUtcNow() ? (reset - _clock.GetUtcNow()).TotalSeconds : 5 * Math.Pow(2, Math.Clamp(previousRetries, 0, 4)));
        // Never shorten a provider delay to fit our automatic retry window.
        var until = _clock.GetUtcNow().AddSeconds(seconds + 1 + Random.Shared.NextDouble() * .25);
        if (until > _notBefore) _notBefore = until;
        return Remaining;
    }
    public static TimeSpan? RetryHint(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var match = DelayPattern().Match(message.Length > 2000 ? message[..2000] : message);
        if (!match.Success) return null;
        double seconds = 0;
        foreach (Match part in DurationPart().Matches(match.Groups[1].Value))
        {
            if (!double.TryParse(part.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                return TimeSpan.MaxValue;
            seconds += value * (part.Groups[2].Value.ToLowerInvariant() switch { "ms" => .001, "m" => 60, "h" => 3600, _ => 1 });
        }
        // An extreme hint is deferred, not clamped into an early retry.
        return !double.IsFinite(seconds) || seconds > 86400 ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
    }
    [GeneratedRegex(@"try again in\s+((?:[0-9]+(?:\.[0-9]+)?\s*(?:ms|s|m|h)\s*)+)(?=[.,;\s]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DelayPattern();
    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*(ms|s|m|h)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPart();
}
