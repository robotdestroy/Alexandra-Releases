using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class SessionIntentTests
{
    [Theory]
    [InlineData("I'm turning in old community goals and then I'm going to do the new community goal combat.", 2)]
    [InlineData("Hello Alexandra, I'm about to enter a medium conflict zone and I'm gonna be doing the community event.", 2)]
    [InlineData("I want to unlock this engineer.", 1)]
    [InlineData("My plan tonight is to upgrade my ship.", 1)]
    [InlineData("I'm mining gold.", 1)]
    [InlineData("Maybe I'm going to do the community goal.", 0)]
    [InlineData("I'm not going to do combat tonight.", 0)]
    [InlineData("I'm no longer doing the community goal.", 0)]
    [InlineData("Should I say I'm going to do combat?", 0)]
    [InlineData("She said I'm going to do the community goal.", 0)]
    [InlineData("I'm going to do combat, but don't save this.", 0)]
    [InlineData("I'm going to buy dinner.", 0)]
    [InlineData("Hello, how are you?", 0)]
    [InlineData("I'm going to do combat. My password is synthetic.", 0)]
    [InlineData("My email is example@example.test and I'm doing combat.", 0)]
    public void KeepsOnlyClearGameplayStatements(string transcript, int count) => Assert.Equal(count, SessionIntentPolicy.Extract(transcript).Count);

    [Fact]
    public void ExcerptsKeepUserWordsNotGreetingsOrInventedCompletion()
    {
        var result = SessionIntentPolicy.Extract("It's going well, I'm turning in community goals and then I'm going to do combat.");
        Assert.Equal(new[] { "I'm turning in community goals", "I'm going to do combat" }, result);
        var session = new GameplaySession();
        Assert.True(session.AddStatedPlans(string.Join(" and then ", result), DateTimeOffset.UtcNow));
        Assert.False(session.AddStatedPlans(string.Join(" and then ", result), DateTimeOffset.UtcNow));
        Assert.Empty(session.GoalChanges); Assert.Empty(session.OpenGoals);
        Assert.Contains("intent, not proof of completion", session.Render().Markdown);
        Assert.DoesNotContain("It's going well", session.Render().Markdown);
    }

    [Fact]
    public void AnchorsRejectDifferentSessionCommanderAndDisabledLogging()
    {
        var now = DateTimeOffset.Parse("2026-11-11T10:00:00Z");
        var book = new SessionLogbook(); book.Configure(true, now);
        var anchor = book.CaptureAnchor("pilot-a", now, "Sol", new Dictionary<long, MissionMemory>(), [], null)!;
        Assert.False(book.RecordIntent(anchor with { Scope = "pilot-b" }, "I'm doing combat"));
        Assert.False(book.RecordIntent(anchor with { SessionId = "wrong" }, "I'm doing combat"));
        Assert.True(book.RecordIntent(anchor, "I'm doing combat"));
        book.Configure(false, now.AddMinutes(1));
        Assert.False(book.RecordIntent(anchor, "I'm mining gold"));
        book.Configure(true, now.AddMinutes(2));
        Assert.False(book.RecordIntent(anchor, "I'm mining gold"));
    }

    [Fact]
    public void SessionPlanStorageIsBoundedAndEscapesMarkdown()
    {
        var session = new GameplaySession();
        for (var i = 0; i < 50; i++) session.AddStatedPlans($"I'm upgrading ship {i} **today**", DateTimeOffset.UtcNow);
        Assert.Equal(24, session.StatedPlans.Count);
        Assert.Contains(@"\*\*today\*\*", session.Render().Markdown);
    }
}
