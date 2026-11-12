using System.Text.RegularExpressions;

namespace Alexandra.Core;

public sealed record MemoryEdit(string Action, string Kind = "note", string? Id = null, string? Text = null);
public sealed record MemoryItem(string Id, string Kind, string Text, string Status, string? System,
    DateTimeOffset UpdatedAt, string Provenance, IReadOnlyList<Source> Sources, DateTimeOffset? ExpiresAt = null);
public sealed record MemoryResult(bool Changed, string Message, MemoryItem? Item = null);
public interface ICompanionMemory : IGameContextSource
{
    string? SessionLogError => null;
    Task SynchronizeAsync(CancellationToken token);
    Task<GameContext> RecallAsync(string query, CancellationToken token);
    Task<MemoryResult> EditAsync(MemoryEdit edit, string userTranscript, CancellationToken token, string? expectedScope = null);
    Task RememberResearchAsync(string query, Answer answer, string scope, CancellationToken token);
    Task<string?> GetScopeAsync(CancellationToken token);
}

// Model-generated tool arguments are a proposal, not permission to retain arbitrary personal information.
public static class MemoryEditPolicy
{
    public static void Validate(MemoryEdit edit, string transcript)
    {
        var spoken = Normalize(transcript);
        if (spoken.Length == 0 || spoken.Length > 6000)
            throw new InvalidOperationException("I need your spoken request before changing memory.");
        if (Regex.IsMatch(spoken, @"\b(don't|do not|never)\s+(remember|save|store|forget|delete|change|update)\b"))
            throw new InvalidOperationException("Your request says not to change memory.");
        var allowed = edit.Action switch
        {
            "save" or "update" => Regex.IsMatch(spoken, @"\b(remember|save|make a note|note that|my goal|my plan|my priority|i'm working on|i am working on|i'm trying to|i am trying to|change my|update my)\b"),
            "complete" => Regex.IsMatch(spoken, @"\b(completed|finished|done with|mark .{0,80}(done|complete))\b"),
            "cancel" => Regex.IsMatch(spoken, @"\b(cancel|abandon|drop|stop working on|no longer)\b"),
            "forget" => Regex.IsMatch(spoken, @"\b(forget|delete|remove .{0,60}(memory|note|goal|preference))\b"),
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("Please say what you want remembered, changed, completed or forgotten.");
        if (edit.Kind is not ("goal" or "note" or "preference"))
            throw new InvalidOperationException("Only your goals, notes and preferences can be written here, not game facts.");
        if (edit.Action is "save" or "update")
        {
            var text = Normalize(edit.Text ?? "");
            if (text.Length is < 3 or > 800 || !spoken.Contains(text, StringComparison.Ordinal))
                throw new InvalidOperationException("Use a short exact phrase from what you just said for this memory, without adding inferred facts.");
            if (Regex.IsMatch(text, @"\b(sk-|api key|password|secret|access token|credit card)\b", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("I don't store credentials or financial secrets in companion memory.");
            if (Regex.IsMatch(spoken, @"\b(maybe|hypothetically|suppose|what if)\b"))
                throw new InvalidOperationException("That sounds tentative. Ask me to remember it when you decide.");
        }
        if (edit.Action != "save" && string.IsNullOrWhiteSpace(edit.Id))
            throw new InvalidOperationException("Look up the specific memory first; don't guess which one to change.");
    }
    public static string Normalize(string text) => Regex.Replace(text.Trim().Replace('’', '\''), @"\s+", " ").ToLowerInvariant();
}

