using System.Text.Json;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class RealtimeFailureTests
{
    [Theory]
    [InlineData("failed", null, "server_error", "server_error", true)]
    [InlineData("failed", null, null, "server_error", true)]
    [InlineData("failed", null, "internal_error", "server_error", true)]
    [InlineData("incomplete", "max_output_tokens", null, null, true)]
    [InlineData("incomplete", "content_filter", null, null, false)]
    [InlineData("failed", "content_filter", "server_error", "server_error", false)]
    [InlineData("failed", null, "insufficient_quota", "server_error", false)]
    [InlineData("failed", null, "rate_limit_exceeded", "server_error", false)]
    [InlineData("failed", null, "invalid_api_key", "invalid_request_error", false)]
    [InlineData("failed", null, null, null, false)]
    [InlineData("cancelled", "client_cancelled", null, null, false)]
    public void OnlyKnownTransientFailuresRetryBeforeSpeech(string status, string? reason, string? code, string? type, bool retry)
    {
        var failure = RealtimeFailure.Parse(JsonSerializer.SerializeToElement(new { status, status_details = new { reason, error = new { code, type } } }));
        Assert.Equal(retry, failure.CanRetry(false, 0));
        Assert.False(failure.CanRetry(true, 0));
        Assert.False(failure.CanRetry(false, 1));
    }
    [Fact]
    public void DetailedDiagnosticsExcludePayloadsAndRedactSecrets()
    {
        var response = JsonSerializer.SerializeToElement(new {
            id = "resp_test", status = "failed",
            status_details = new { error = new { code = "server_error", type = "server_error", message = "Test sk-proj-abcdefghijklmnop" } },
            usage = new { input_tokens = 100, output_tokens = 2 },
            output = new[] { new { privateJournal = "DO NOT LOG", audio = "BINARY" } }
        });
        var diagnostic = RealtimeFailure.Parse(response).Diagnostic(response);
        Assert.Contains("resp_test", diagnostic); Assert.Contains("server_error", diagnostic);
        Assert.Contains("100", diagnostic); Assert.Contains("REDACTED", diagnostic);
        Assert.DoesNotContain("abcdefghijklmnop", diagnostic); Assert.DoesNotContain("DO NOT LOG", diagnostic);
        Assert.DoesNotContain("BINARY", diagnostic);
    }
    [Fact]
    public void MissingAndNullProviderDetailsAreSafe()
    {
        foreach (var json in new[] { "{}", "{\"status\":\"failed\",\"status_details\":null}" })
        {
            var response = JsonSerializer.Deserialize<JsonElement>(json);
            var failure = RealtimeFailure.Parse(response);
            Assert.False(failure.CanRetry(false, 0));
            Assert.Contains("voice service", failure.UserMessage);
            Assert.NotEmpty(failure.Diagnostic(response));
        }
    }
}
