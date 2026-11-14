using System.Text.RegularExpressions;

namespace Alexandra.Core;

public enum ReminderTrigger { Docked, SystemArrival, Touchdown }
public sealed record ReminderEdit(string Action, ReminderTrigger? Trigger = null, string? Text = null,
    string? Target = null, string? Id = null);
public sealed record VoiceReminder(string Id, ReminderTrigger Trigger, string Text, string? Target,
    DateTimeOffset CreatedAt, string Status = "armed", DateTimeOffset? TriggeredAt = null);
public sealed record ReminderResult(bool Changed, string Message, VoiceReminder? Reminder = null);
public sealed record ReminderSnapshot(string Scope, DateTimeOffset SessionAt, bool GameLoaded, DateTimeOffset ReadAt,
    IReadOnlyList<VoiceReminder> Items);
public interface IReminderMemory
{
    Task<ReminderSnapshot?> ReadRemindersAsync(CancellationToken token);
    Task<ReminderResult> EditReminderAsync(ReminderEdit edit, string transcript, string expectedScope, CancellationToken token);
    Task<bool> ClaimReminderAsync(string scope, string id, DateTimeOffset triggeredAt, CancellationToken token);
}

// Tool arguments propose a change; only the current user's words authorize one.
public static class ReminderPolicy
{
    public static void Validate(ReminderEdit edit, string transcript)
    {
        var spoken = MemoryEditPolicy.Normalize(transcript);
        if (spoken.Length is < 3 or > 6000 || Regex.IsMatch(spoken,
            @"\b(don't|do not|never|hypothetically|suppose|what if|example|you said|he said|she said)\b|\bhow (do|can|would)\b"))
            throw new InvalidOperationException("Ask me directly to set or cancel a reminder.");
        if (edit.Action == "cancel")
        {
            if (string.IsNullOrWhiteSpace(edit.Id) || !Regex.IsMatch(spoken, @"\b(cancel|remove|delete|forget)\b") ||
                !Regex.IsMatch(spoken, @"\breminder\b"))
                throw new InvalidOperationException("Name the reminder you want cancelled; look it up first.");
            return;
        }
        if (edit.Action != "create" || edit.Trigger is null || !Enum.IsDefined(edit.Trigger.Value) ||
            !Regex.IsMatch(spoken, @"\b(remind me|set (?:a |an? )?reminder)\b") ||
            !Regex.IsMatch(spoken, @"\b(when|once|after|next time)\b"))
            throw new InvalidOperationException("Tell me what to remind you about, and when: docking, arriving in a system, or touching down.");
        var condition = edit.Trigger switch {
            ReminderTrigger.Docked => @"\b(dock|docking|docked)\b",
            ReminderTrigger.SystemArrival => @"\b(arrive|enter|reach|jump|get (?:to|back to)|return to)\b",
            ReminderTrigger.Touchdown => @"\b(land|landing|touch down|touchdown)\b",
            _ => "(?!)"
        };
        if (!Regex.IsMatch(spoken, @"\b(?:when|once|after|next time)\s+(?:(?:I|we|I'm|we're|my ship)\s+)?(?:(?:am|are|have|next|finally|successfully)\s+){0,2}" + condition, RegexOptions.IgnoreCase))
            throw new InvalidOperationException("That reminder condition doesn't match what you said. Use docking, entering a system or touching down.");
        ValidateExcerpt(edit.Text, spoken, 180);
        if (edit.Target is { Length: > 0 }) ValidateExcerpt(edit.Target, spoken, 100);
        if (edit.Trigger == ReminderTrigger.SystemArrival && string.IsNullOrWhiteSpace(edit.Target))
            throw new InvalidOperationException("Which system should trigger that reminder?");
    }
    public static void ValidateExcerpt(string? text, string transcript, int maxLength)
    {
        var normalized = MemoryEditPolicy.Normalize(text ?? "");
        if (normalized.Length is < 2 || text!.Length > maxLength || text.Any(char.IsControl) ||
            !Regex.IsMatch(MemoryEditPolicy.Normalize(transcript), @"(?<![\p{L}\p{N}])" + Regex.Escape(normalized) + @"(?![\p{L}\p{N}])") ||
            Regex.IsMatch(normalized, @"sk-|https?://|\b(api key|password|secret|access token|credit card)\b"))
            throw new InvalidOperationException("Use a short exact phrase from your request, without credentials or added details.");
    }
    public static bool CanAnnounce(ReminderSnapshot? snapshot, VoiceReminder reminder, CombatContext? combat,
        DateTimeOffset? gameStartedAt, DateTimeOffset now, bool claimed = false)
    {
        return snapshot is { GameLoaded: true } && gameStartedAt is { } started &&
            reminder.TriggeredAt is { } triggered && triggered <= now &&
            snapshot.SessionAt >= started.AddSeconds(-2) && snapshot.SessionAt <= now &&
            snapshot.ReadAt <= now && now - snapshot.ReadAt <= TimeSpan.FromSeconds(5) &&
            combat is { LiveAvailable: true, Callouts.CombatActive: false } &&
            combat.GameSessionAt == snapshot.SessionAt &&
            combat.ReadAt <= now && now - combat.ReadAt <= TimeSpan.FromSeconds(3) &&
            snapshot.Items.Any(r => r.Id == reminder.Id && (r.Status == "ready" || claimed && r.Status == "notified") && r.TriggeredAt == reminder.TriggeredAt);
    }
}

public interface IReminderAudioSource
{
    // Only an explicitly saved reminder excerpt, not a model-generated notification.
    Task<byte[]> GenerateAsync(string text, string voice, string key, CancellationToken token);
}
