using System.Text.Json;

namespace Alexandra.Infrastructure;

// Keep provider failure metadata, not the response's audio, tool arguments or game data.
public sealed record RealtimeFailure(string Status, string? Reason, string? Code, string? Type, string? Message)
{
    public static RealtimeFailure Parse(JsonElement response)
    {
        var details = response.TryGetProperty("status_details", out var d) ? d : default;
        var error = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("error", out var e) ? e : default;
        return new(Text(response, "status") ?? "unknown", Text(details, "reason"), Text(error, "code"),
            Text(error, "type"), Text(error, "message"));
    }
    public bool CanRetry(bool audioEmitted, int previousRetries) => !audioEmitted && previousRetries == 0 &&
        Reason != "content_filter" && Code != "content_filter" &&
        (Status == "incomplete" && Reason == "max_output_tokens" ||
         Status == "failed" && (Code is "server_error" or "internal_error" or "internal_server_error" or "service_unavailable" ||
                                Type == "server_error" && string.IsNullOrEmpty(Code)));
    public int? RetryOutputLimit => Status == "incomplete" && Reason == "max_output_tokens" ? 4096 : null;
    public bool IsRateLimit => Status == "failed" && Code == "rate_limit_exceeded";
    public string UserMessage => Reason == "content_filter" || Code == "content_filter"
        ? "That request couldn't be completed. Try rephrasing it."
        : Code == "rate_limit_exceeded"
            ? "Voice is temporarily busy. Please wait a moment, then try again."
            : Code == "insufficient_quota"
                ? "API credit or billing limit reached. Check the project's billing settings."
            : Reason == "max_output_tokens"
                ? "The voice reply reached its limit. Try a shorter question."
                : "The voice service couldn't finish that reply. Hold push-to-talk to try again.";
    public string Diagnostic(JsonElement response)
    {
        var usage = response.TryGetProperty("usage", out var u) ? u : default;
        return LocalDiagnosticLog.Sanitize(JsonSerializer.Serialize(new {
            responseId = Text(response, "id"), status = Status, reason = Reason, code = Code, errorType = Type,
            message = Message is { Length: > 600 } ? Message[..600] : Message,
            inputTokens = Number(usage, "input_tokens"), outputTokens = Number(usage, "output_tokens")
        }));
    }
    private static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Number(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
