using Alexandra.Core;
using Xunit;

namespace Alexandra.Tests;

public sealed class ReminderPolicyTests
{
    [Theory]
    [InlineData("When I dock remind me to claim my rewards", ReminderTrigger.Docked, null)]
    [InlineData("Remind me to claim my rewards when I dock at Galileo", ReminderTrigger.Docked, "Galileo")]
    [InlineData("When I enter Sol remind me to claim my rewards", ReminderTrigger.SystemArrival, "Sol")]
    [InlineData("When I touch down remind me to claim my rewards", ReminderTrigger.Touchdown, null)]
    public void ExplicitConditionsAndExactUserExcerptsAreAccepted(string transcript, ReminderTrigger trigger, string? target) =>
        ReminderPolicy.Validate(new("create", trigger, "claim my rewards", target), transcript);
    [Theory]
    [InlineData("Don't remind me when I dock")]
    [InlineData("How can I set a reminder when I dock?")]
    [InlineData("Hypothetically remind me when I dock")]
    [InlineData("When I buy gold remind me to dock")]
    [InlineData("When I dock tell me about Sol")]
    [InlineData("Hello there")]
    public void ModelCannotCreateFromNegationTutorialOrUnsupportedConditions(string transcript) =>
        Assert.Throws<InvalidOperationException>(() => ReminderPolicy.Validate(new("create", ReminderTrigger.Docked, "dock"), transcript));
    [Fact]
    public void ModelCannotInventTextTargetOrStoreCredentials()
    {
        var edit = new ReminderEdit("create", ReminderTrigger.Docked, "buy gold");
        Assert.Throws<InvalidOperationException>(() => ReminderPolicy.Validate(edit, "When I dock remind me to sell gold"));
        Assert.Throws<InvalidOperationException>(() => ReminderPolicy.Validate(edit with { Target = "Galileo" }, "When I dock remind me to buy gold"));
        Assert.Throws<InvalidOperationException>(() => ReminderPolicy.ValidateExcerpt("Sol", "consolation", 100));
        Assert.Throws<InvalidOperationException>(() => ReminderPolicy.ValidateExcerpt("my password", "my password", 100));
        Assert.Throws<InvalidOperationException>(() => ReminderPolicy.ValidateExcerpt("claim" + new string(' ', 200) + "rewards", "claim rewards", 180));
        Assert.Throws<InvalidOperationException>(() => ReminderPolicy.Validate(edit with { Trigger = ReminderTrigger.SystemArrival }, "When I enter Sol remind me to buy gold"));
    }
    [Fact]
    public void CancellingRequiresExplicitRequestAndKnownId()
    {
        ReminderPolicy.Validate(new("cancel", Id: "id"), "cancel that reminder");
        Assert.Throws<InvalidOperationException>(() => ReminderPolicy.Validate(new("cancel", Id: "id"), "what reminders do I have"));
        Assert.Throws<InvalidOperationException>(() => ReminderPolicy.Validate(new("cancel"), "cancel that reminder"));
    }
    [Fact]
    public void ReminderAboutAPhotoDoesNotAuthorizeAnImmediateScreenshot()
    {
        Assert.False(ScreenRequestPolicy.CanSave("When I dock remind me to take a photo"));
        Assert.False(ScreenRequestPolicy.CanSave("Automatically take a photo every time I dock"));
        Assert.True(ScreenRequestPolicy.CanSave("Take a photo—first landing on this planet"));
    }
    [Fact]
    public void AnnouncingRequiresSameLiveSessionFreshEvidenceAndNoCombat()
    {
        var now = DateTimeOffset.UtcNow; var start = now.AddMinutes(-5);
        var reminder = new VoiceReminder("id", ReminderTrigger.Docked, "claim rewards", null, start, "ready", now);
        var snapshot = new ReminderSnapshot("scope", start, true, now, [reminder]);
        var combat = new CombatContext("{}", "e", now, true,
            new("c", false, false, true, now, null, null, null), GameSessionAt: start);
        bool Allowed(ReminderSnapshot? s, CombatContext? c, DateTimeOffset? process) => ReminderPolicy.CanAnnounce(s, reminder, c, process, now);
        Assert.True(Allowed(snapshot, combat, start)); // Docked: combat callout eligibility is deliberately irrelevant.
        Assert.False(Allowed(snapshot, combat with { Callouts = combat.Callouts! with { CombatActive = true } }, start));
        Assert.False(Allowed(snapshot, combat with { ReadAt = now.AddSeconds(-4) }, start));
        Assert.False(Allowed(snapshot with { ReadAt = now.AddSeconds(-6) }, combat, start));
        Assert.False(Allowed(snapshot with { GameLoaded = false }, combat, start));
        Assert.False(Allowed(snapshot, combat with { GameSessionAt = now }, start));
        Assert.False(Allowed(snapshot, combat, now));
        Assert.False(Allowed(snapshot, combat, null));
        Assert.False(Allowed(snapshot, null, start));
        Assert.False(Allowed(snapshot with { Items = [reminder with { Status = "armed" }] }, combat, start));
        Assert.False(Allowed(snapshot with { Items = [reminder with { Status = "notified" }] }, combat, start));
        Assert.True(ReminderPolicy.CanAnnounce(snapshot with { Items = [reminder with { Status = "notified" }] }, reminder, combat, start, now, claimed: true));
    }
}
