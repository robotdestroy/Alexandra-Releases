namespace Alexandra.Core;

public sealed record DestinationCopyResult(bool ClipboardCopied, string? CopiedSystem,
    string ClipboardStatus, string ClipboardNote);

// Both explicit copy requests and automatic research destinations use this validation path.
// Windows access remains behind ISystemClipboard; never write unvalidated model text.
public sealed class DestinationCopyService(ISystemResolver systems, ISystemClipboard clipboard)
{
    public Task<DestinationCopyResult> CopyResearchAsync(Answer answer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (answer.Destination is null)
            return Task.FromResult(NotCopied("no_destination", "No single travel destination was selected; clipboard unchanged."));
        if (!AnswerPolicy.HasDestinationEvidence(answer))
            return Task.FromResult(NotCopied("missing_evidence", "The travel recommendation has no retrieved supporting source; clipboard unchanged."));
        return CopySystemAsync(answer.Destination.System, token);
    }

    public async Task<DestinationCopyResult> CopySystemAsync(string? candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!AnswerPolicy.IsSystemName(candidate))
            return NotCopied("invalid_system", "Choose one exact galaxy system, not a station, URL or sentence; clipboard unchanged.");
        string? system;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { system = await systems.ResolveAsync(candidate!, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return NotCopied("lookup_timeout", "System verification timed out; clipboard unchanged. Try copying again."); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        { token.ThrowIfCancellationRequested(); return NotCopied("lookup_unavailable", "System verification is unavailable; clipboard unchanged. Try copying again."); }
        token.ThrowIfCancellationRequested();
        if (!AnswerPolicy.IsSystemName(system) || !string.Equals(candidate, system, StringComparison.OrdinalIgnoreCase))
            return NotCopied("system_not_found", "That exact system was not found in EDSM; clipboard unchanged. Check its spelling; do not substitute a different system.");
        var copied = await clipboard.TryCopyAsync(system!, token);
        token.ThrowIfCancellationRequested();
        return copied
            ? new(true, system, "copied", $"Copied {system}.")
            : NotCopied("clipboard_unavailable", "Windows could not confirm the clipboard update after retries. Please try copying again.");
    }

    private static DestinationCopyResult NotCopied(string status, string note) => new(false, null, status, note);
}
