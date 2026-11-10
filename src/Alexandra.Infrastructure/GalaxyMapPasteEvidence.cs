using System.Text.Json;

namespace Alexandra.Infrastructure;

// Status.json confirms Galaxy Map (GuiFocus=6), not which field inside the map has focus.
// The player must select the search box; we never infer text-box focus or send Enter.
public static class GalaxyMapPasteEvidence
{
    public static bool IsReady(string json, DateTimeOffset processStarted, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 65536) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            return e.ValueKind == JsonValueKind.Object &&
                e.TryGetProperty("event", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "Status" &&
                e.TryGetProperty("GuiFocus", out var focus) && focus.ValueKind == JsonValueKind.Number && focus.TryGetInt32(out var value) && value == 6 &&
                e.TryGetProperty("timestamp", out var timestamp) && timestamp.ValueKind == JsonValueKind.String &&
                timestamp.TryGetDateTimeOffset(out var at) && at >= processStarted.AddSeconds(-1) &&
                at >= now.AddMinutes(-5) && at <= now.AddSeconds(5);
        }
        catch (JsonException) { return false; }
    }
}
