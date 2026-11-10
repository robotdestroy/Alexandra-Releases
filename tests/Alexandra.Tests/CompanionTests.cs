using System.Net;
using System.Text;
using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class CompanionTests
{
    private static string Response(string speech = "Sol is the destination.") => JsonSerializer.Serialize(new {
        status = "completed", output = new object[] {
            new { type = "web_search_call", action = new { sources = new[] { new { type = "url", url = "https://inara.cz/elite/test", title = "Inara" } } } },
            new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(new {
                speech, destination = new { system = "Sol", station = (string?)null, evidenceUrl = "https://inara.cz/elite/test" }
            }), annotations = Array.Empty<object>() } } }
        }
    });

    [Fact]
    public void StructuredAnswerIsParsedNotSpokenAsJson()
    {
        using var doc = JsonDocument.Parse(Response());
        var answer = OpenAiCompanion.ParseAnswer(doc.RootElement);
        Assert.Equal("Sol is the destination.", answer.Speech);
        Assert.Equal("Sol", answer.Destination?.System);
        Assert.True(AnswerPolicy.HasDestinationEvidence(answer));
        Assert.Single(answer.Sources);
    }
    [Fact]
    public void IncompleteOutputIsRejected()
    {
        using var doc = JsonDocument.Parse("""{"status":"incomplete","output":[]}""");
        Assert.Throws<InvalidOperationException>(() => OpenAiCompanion.ParseAnswer(doc.RootElement));
    }
    [Fact]
    public void OverlongSpeechIsRejectedBeforeTts()
    {
        using var doc = JsonDocument.Parse(Response(new string('a', 4001)));
        Assert.Throws<InvalidOperationException>(() => OpenAiCompanion.ParseAnswer(doc.RootElement));
    }
    [Fact]
    public async Task FollowupsAndPrivacySettingsTravelWithTheRequest()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async request => {
            body = await request.Content!.ReadAsStringAsync();
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Json(Response());
        }));
        var companion = new OpenAiCompanion(http, () => "fake-unit-test-key", () => "gpt-5.4", () => "coral");
        var captureCalled = false;
        await companion.AnswerAsync("Is there a closer one?", new("Sol", "{}", "Synthetic data"),
            [new("Where can I buy gold?", "Try the test station.")],
            _ => { captureCalled = true; return Task.FromResult(new byte[] { 1 }); }, CancellationToken.None);
        Assert.False(captureCalled);
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("required", root.GetProperty("tool_choice").GetString());
        Assert.Equal(3, root.GetProperty("input").GetArrayLength());
        Assert.Contains("Where can I buy gold?", body);
        Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        var policy = root.GetProperty("instructions").GetString()!;
        Assert.Contains("45 words", policy);
        Assert.DoesNotContain("2-5 short sentences", policy);
        Assert.Contains("Missing local data is a reason to search", policy);
        Assert.Contains("site:inara.cz/elite/station/", policy);
        Assert.Contains("community last-reported locations", policy);
        Assert.Contains("Skip routine Inara/source-reliability disclaimers", policy);
        Assert.Contains("Do not explain copy/paste mechanics unless asked", policy);
        Assert.Contains("only when it changes the answer or recommendation", policy);
        Assert.DoesNotContain("Mention its age/date once", policy);
        Assert.DoesNotContain("Say the age of important trade data", policy);
        Assert.Contains("mined vs purchased goods", policy);
        Assert.Contains("Don't promise qualification", policy);
    }
    [Fact]
    public void BrowserPolicyChecksEvidenceWithoutRequiringRoutineSpokenWarnings()
    {
        Assert.Contains("no routine source-reliability disclaimers", ResearchToolProtocol.Instructions);
        Assert.Contains("Check market age, supply, pad size and relevant filters internally", ResearchToolProtocol.Instructions);
        Assert.Contains("Do not claim current stock from a blocked page", ResearchToolProtocol.Instructions);
        Assert.DoesNotContain("disclose the limitation", ResearchToolProtocol.Instructions);
    }
    [Fact]
    public async Task VisualQuestionAttachesOnlyTheRequestedCapture()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async request => { body = await request.Content!.ReadAsStringAsync(); return Json(Response()); }));
        var companion = new OpenAiCompanion(http, () => "fake-unit-test-key", () => "gpt-5.4", () => "coral");
        var count = 0;
        await companion.AnswerAsync("What am I looking at?", new(null, "{}", "No game data"), [],
            _ => { count++; return Task.FromResult(new byte[] { 1, 2, 3 }); }, CancellationToken.None);
        Assert.Equal(1, count);
        Assert.Contains("data:image/jpeg;base64,AQID", body);
        using var request = JsonDocument.Parse(body!);
        Assert.Equal("auto", request.RootElement.GetProperty("tool_choice").GetString());
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
    }
    [Fact]
    public async Task NaturalVisualQuestionAttachesImageWithoutMandatoryWebSearch()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async request => { body = await request.Content!.ReadAsStringAsync(); return Json(Response()); }));
        var companion = new OpenAiCompanion(http, () => "fake-unit-test-key", () => "gpt-5.4", () => "marin");
        var count = 0;
        await companion.AnswerAsync("Explain this warning", new(null, "{}", "None"), [],
            _ => { count++; return Task.FromResult(new byte[] { 1, 2, 3 }); }, default);
        Assert.Equal(1, count);
        using var request = JsonDocument.Parse(body!);
        Assert.Equal("auto", request.RootElement.GetProperty("tool_choice").GetString());
        Assert.Contains("data:image/jpeg;base64,AQID", body);
    }
    [Fact]
    public async Task CaptureFailureDoesNotSendResearch()
    {
        var sent = false;
        using var http = new HttpClient(new Handler(_ => { sent = true; return Task.FromResult(Json(Response())); }));
        var companion = new OpenAiCompanion(http, () => "fake-unit-test-key", () => "gpt-5.4", () => "coral");
        await Assert.ThrowsAsync<InvalidOperationException>(() => companion.AnswerAsync("Capture this", new(null, "{}", "None"), [],
            _ => throw new InvalidOperationException("Elite is not visible."), CancellationToken.None));
        Assert.False(sent);
    }
    [Fact]
    public async Task MissingKeyNeverSendsARequest()
    {
        var sent = false;
        using var http = new HttpClient(new Handler(_ => { sent = true; return Task.FromResult(Json(Response())); }));
        var companion = new OpenAiCompanion(http, () => null, () => "gpt-5.4", () => "coral");
        await Assert.ThrowsAsync<InvalidOperationException>(() => companion.SpeakAsync("Hello", CancellationToken.None));
        Assert.False(sent);
    }
    [Fact]
    public async Task ServiceErrorsDoNotExposeTheResponseBody()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) {
            Content = new StringContent("secret-server-details")
        })));
        var companion = new OpenAiCompanion(http, () => "fake-unit-test-key", () => "gpt-5.4", () => "coral");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => companion.SpeakAsync("Hello", CancellationToken.None));
        Assert.Contains("API key", exception.Message);
        Assert.DoesNotContain("secret-server-details", exception.Message);
    }
    [Fact]
    public async Task SpeechRequestUsesConfiguredVoiceAndWav()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async request => {
            Assert.EndsWith("/audio/speech", request.RequestUri!.AbsolutePath);
            body = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }));
        var companion = new OpenAiCompanion(http, () => "fake-unit-test-key", () => "gpt-5.4", () => "coral");
        await companion.SpeakAsync("Hello", CancellationToken.None);
        Assert.Contains("\"voice\":\"coral\"", body);
        Assert.Contains("\"response_format\":\"wav\"", body);
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return send(request); }
    }
}

