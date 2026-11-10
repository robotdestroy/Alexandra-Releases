using System.Text.RegularExpressions;

namespace Alexandra.Core;

public sealed record CopiedDestination(string System, uint ClipboardSequence, DateTimeOffset CopiedAt);
public sealed record DestinationPasteResult(bool PasteSent, string? System, string Status, string Note)
{
    public static DestinationPasteResult Blocked(string status, string note) => new(false, null, status, note);
}
public sealed class CopiedDestinationMemory
{
    private readonly object _gate = new();
    private CopiedDestination? _value;
    public CopiedDestination? Read() { lock (_gate) return _value; }
    // Called only after the validated copy path has written AND read back the Windows clipboard.
    public void Remember(string system, uint clipboardSequence)
    {
        lock (_gate) _value = AnswerPolicy.IsSystemName(system) && clipboardSequence != 0
            ? new(system, clipboardSequence, DateTimeOffset.UtcNow) : null;
    }
    public void Clear() { lock (_gate) _value = null; }
}
public interface IDestinationPasteDriver
{
    Task<DestinationPasteResult> PasteAsync(CopiedDestination destination, CancellationToken token);
}
public sealed class DestinationPasteService(CopiedDestinationMemory memory, IDestinationPasteDriver driver)
{
    public async Task<DestinationPasteResult> PasteAsync(string actualUserTranscript, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var destination = memory.Read();
        if (!PasteRequestPolicy.CanPaste(actualUserTranscript, destination?.System))
            return DestinationPasteResult.Blocked("not_requested", "Ask explicitly to paste the copied destination, for example 'paste that'.");
        if (destination is null)
            return DestinationPasteResult.Blocked("no_verified_copy", "Ask me to copy a destination first. I only paste a system I verified and copied during this app session.");
        var result = await driver.PasteAsync(destination, token);
        if (result.Status == "clipboard_changed") memory.Clear();
        return result;
    }
}

// Check the real transcript, never model arguments. Reject tutorial/past-tense/negative requests,
// other applications, arbitrary text and a named destination different from the verified copy.
public static partial class PasteRequestPolicy
{
    public static bool CanPaste(string utterance, string? copiedSystem)
    {
        if (string.IsNullOrWhiteSpace(utterance) || utterance.Length > 500) return false;
        var text = Regex.Replace(utterance.Trim(), @"(?:[,\s]+(?:for\s+me|now|again|please))+[.!?]*$", "", RegexOptions.IgnoreCase);
        var match = Request().Match(text);
        if (!match.Success) return false;
        var target = Regex.Replace(match.Groups["target"].Value.Trim(), @"\s+", " ").ToLowerInvariant();
        return target is "" or "it" or "that" or "this" or "the location" or "the system" or "the destination"
            or "that location" or "that system" or "that destination" or "this location" or "this system" or "this destination"
            or "the copied location" or "the copied system" or "the copied destination" or "what you copied"
            or "the location you copied" or "the system you copied" or "the destination you copied"
            || copiedSystem is not null && target.Equals(copiedSystem, StringComparison.OrdinalIgnoreCase);
    }
    [GeneratedRegex(@"^(?:Alexandra[,\s]+)?(?:(?:okay|ok|yes|please)[,\s]+)*(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?)?(?:go\s+ahead\s+and\s+)?paste(?:\s+(?<target>.*?))?(?:\s+(?:in|into)\s+(?:the\s+)?(?:galaxy\s+map(?:\s+search(?:\s+(?:field|box))?)?|search(?:\s+(?:field|box))?))?(?:\s+for\s+me)?(?:\s+now)?(?:[,\s]+please)?[.!?]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Request();
}

// Only this fixed gesture is exposed, never a general key/text driver.
public interface IFixedPasteKeys
{
    bool PressControl();
    bool PressV();
    void ReleaseV();
    void ReleaseControl();
}
public sealed class FixedPasteGesture(IFixedPasteKeys keys, Func<bool> stillSafe,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public async Task<bool> SendAsync(CancellationToken token)
    {
        var control = false;
        var v = false;
        var wait = delay ?? Task.Delay;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!stillSafe()) return false;
            control = keys.PressControl();
            if (!control) return false;
            await wait(TimeSpan.FromMilliseconds(25), token);
            token.ThrowIfCancellationRequested();
            if (!stillSafe()) return false;
            v = keys.PressV();
            if (!v) return false;
            await wait(TimeSpan.FromMilliseconds(55), token);
            return true;
        }
        finally
        {
            // Key-up cleanup must run even if interrupted or focus changes. No new key-down on cancellation.
            try { if (v) keys.ReleaseV(); }
            finally { if (control) keys.ReleaseControl(); }
        }
    }
}
