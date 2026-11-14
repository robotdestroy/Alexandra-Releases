using System.Text.RegularExpressions;

namespace Alexandra.Core;

// Check the user's actual transcript, never the model's rewritten tool question.
public static partial class ScreenRequestPolicy
{
    public static bool CanSave(string utterance) => !Excluded(utterance) &&
        !SaveDenied().IsMatch(utterance) && !PastSaveQuestion().IsMatch(utterance) &&
        !Regex.IsMatch(utterance, @"\b(remind me|reminder|automatically|whenever|every time)\b", RegexOptions.IgnoreCase) && SaveRequest().IsMatch(utterance);

    public static bool CanInspect(string utterance) => !Excluded(utterance) &&
        !CaptureDenied().IsMatch(utterance) &&
        (VisualRequest().IsMatch(utterance) || CurrentPadQuestion().IsMatch(utterance) &&
            !Regex.IsMatch(utterance, @"\b(remind|automatically|whenever|every time|generally)\b", RegexOptions.IgnoreCase));

    [GeneratedRegex(@"\b(?:where\s+(?:is|are)|where['’]s|find|locate|show\s+me)\s+(?:(?:my|the|assigned|current)\s+)*(?:landing\s+)?pad(?:\s+(?:number\s+)?(?:\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten|twenty|thirty|forty))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentPadQuestion();

    private static bool Excluded(string text) => string.IsNullOrWhiteSpace(text) ||
        ScreenshotInstructions().IsMatch(text) || OtherApp().IsMatch(text);

    [GeneratedRegex(@"\b(?:how\s+(?:do|can|should|would)\s+I\s+(?:take|save|capture)|how\s+to\s+(?:take|save|capture)|(?:what|which).{0,20}(?:key|shortcut).{0,20}(?:screenshot|screen\s*shot|snapshot|picture|photo))\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenshotInstructions();
    [GeneratedRegex(@"\b(?:my|the)\s+(?:desktop|browser|email|other\s+monitor)\b|\b(?:Discord|ChatGPT)\s+(?:window|app|screen)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OtherApp();
    [GeneratedRegex(@"\b(?:don['’]?t|do\s+not|never|without)\s+(?:\w+\s+){0,2}(?:save|saving|store|storing|keep|keeping|record|recording|take|taking|capture|capturing|snap|screenshot)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SaveDenied();
    [GeneratedRegex(@"\b(?:did|have)\s+(?:you|I|we)\b.{0,80}(?:screenshot|screen\s*shot|snapshot|picture|photo)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PastSaveQuestion();
    [GeneratedRegex(@"\b(?:don['’]?t|do\s+not|never|without)\s+(?:(?:ever|please|actually|automatically|using|a|any)\s+){0,2}(?:capture|capturing|look|looking|inspect|inspecting|view|viewing|see|seeing|screenshot|screen\s*shot|snapshot|picture|photo|(?:take|taking)\s+(?:a\s+)?(?:screenshot|screen\s*shot|snapshot|picture|photo))\b", RegexOptions.IgnoreCase)]
    private static partial Regex CaptureDenied();
    [GeneratedRegex(@"\b(?:take|save|grab|snap|make|capture)\b(?:\s+\w+){0,5}\s+(?:screenshot|screen\s*shot|snapshot|picture|photo)\b|\b(?:screenshot|screen\s*shot)\s+(?:this|that|the\s+game|my\s+game|my\s+screen|now)\b|^\s*(?:please\s+)?(?:screenshot|screen\s*shot|snapshot)(?:\s+please)?[.!?]*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SaveRequest();
    [GeneratedRegex(@"\b(?:capture|screenshot|screen\s*shot|what\s+am\s+i\s+(?:looking\s+at|seeing)|what\s+(?:is\s+this|are\s+these)|what\s+(?:does|do)\s+(?:this|that|these|those)\b|on\s+(?:my|the)\s+screen|look\s+at\s+(?:this|that|(?:my|the)\s+(?:screen|game))|(?:explain|read|identify|describe|understand|help\s+(?:me\s+)?with)\s+(?:this|that|these|those)|why\s+(?:is|are)\s+(?:this|that|these|those))\b", RegexOptions.IgnoreCase)]
    private static partial Regex VisualRequest();
}
