using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class AnswerPolicyTests
{
    [Theory]
    [InlineData("Sol", true)]
    [InlineData("HIP 101399", true)]
    [InlineData("Antliae Sector QD-T b3-2", true)]
    [InlineData("BD+87 118", true)]
    [InlineData(" Sol", false)]
    [InlineData("Sol\n", false)]
    [InlineData("https://example.com", false)]
    [InlineData("", false)]
    public void ValidatesSystemNames(string name, bool valid) => Assert.Equal(valid, AnswerPolicy.IsSystemName(name));

    [Theory]
    [InlineData("https://inara.cz/elite/station/1/", true)]
    [InlineData("https://www.reddit.com/r/EliteDangerous/comments/example/", true)]
    [InlineData("file:///C:/private", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("http://localhost/a", false)]
    [InlineData("http://192.168.1.2/a", false)]
    [InlineData("https://name:secret@example.com", false)]
    [InlineData("https://test.local/path", false)]
    public void RestrictsReferenceLinks(string link, bool valid) => Assert.Equal(valid, AnswerPolicy.IsPublicLink(link));

    [Theory]
    [InlineData("What am I looking at?", true)]
    [InlineData("Capture, how do I complete this community goal?", true)]
    [InlineData("What is this?", true)]
    [InlineData("Explain the panel on my screen", true)]
    [InlineData("What am I supposed to buy?", false)]
    [InlineData("Which ship am I flying?", false)]
    [InlineData("Where can I buy gold?", false)]
    public void CapturesOnlyVisualQuestions(string question, bool capture) => Assert.Equal(capture, AnswerPolicy.WantsVisualContext(question));

    [Fact]
    public void RequiresRetrievedDestinationEvidence()
    {
        var answer = new Answer("Travel to Sol", new("Sol", "Test station", "https://inara.cz/elite/test"), []);
        Assert.False(AnswerPolicy.HasDestinationEvidence(answer));
        Assert.True(AnswerPolicy.HasDestinationEvidence(answer with { Sources = [new("Test", "https://inara.cz/elite/test")] }));
        Assert.False(AnswerPolicy.HasDestinationEvidence(answer with { Destination = null }));
    }

    [Theory]
    [InlineData("{\"name\":\"Sol\"}", "sol", "Sol")]
    [InlineData("{\"name\":\"Seoul\"}", "Sol", null)]
    [InlineData("{}", "Sol", null)]
    [InlineData("[]", "Sol", null)]
    public void ResolverNeverSilentlySubstitutesASimilarName(string json, string requested, string? expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, EdsmSystemResolver.CanonicalName(doc.RootElement, requested));
    }
}

