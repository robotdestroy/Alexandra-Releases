using System.Text.Json;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

public sealed class EdsmSystemResolver(HttpClient http) : ISystemResolver
{
    public async Task<string?> ResolveAsync(string candidate, CancellationToken cancellationToken)
    {
        if (!AnswerPolicy.IsSystemName(candidate)) return null;
        using var response = await http.GetAsync("https://www.edsm.net/api-v1/system?systemName=" +
            Uri.EscapeDataString(candidate) + "&showId=1", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return CanonicalName(doc.RootElement, candidate);
    }
    public static string? CanonicalName(JsonElement response, string candidate) =>
        response.ValueKind == JsonValueKind.Object && response.TryGetProperty("name", out var name) &&
        string.Equals(name.GetString(), candidate, StringComparison.OrdinalIgnoreCase) &&
        AnswerPolicy.IsSystemName(name.GetString()) ? name.GetString() : null;
}

