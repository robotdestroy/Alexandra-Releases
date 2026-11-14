using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class LandingPadTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T10:01:00Z");
    private static JournalContextSource.JournalSnapshot Fixture()
    {
        var state = new JournalContextSource.JournalSnapshot();
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "docking-guidance.jsonl"))) state.Apply(line);
        return state;
    }
    [Fact]
    public void JournalAssignmentProducesShortPad24DirectionsAndSurvivesCheckpoint()
    {
        var state = JournalContextSource.JournalSnapshot.Restore(Fixture().Export());
        var answer = LandingPadGuide.Describe(state.Docking, null, Now);
        Assert.True(answer.Located); Assert.Equal(24, answer.Pad);
        Assert.Contains("near the entrance", answer.Answer); Assert.Contains("upper side", answer.Answer);
        Assert.Contains("12 o'clock", answer.Answer); Assert.Contains("right", answer.Orientation);
        Assert.True(LandingPadGuide.Describe(state.Docking, 7, Now).Located);
        Assert.Equal(24, state.Docking!.Pad); // Asking about a different pad does not reassign it.
    }
    [Fact]
    public void AllStandardPadsAreMappedExactlyOnceAndInvalidPadsDoNotWrap()
    {
        var state = Fixture();
        foreach (var type in new[] { "Coriolis", "Orbis", "Ocellus" })
            for (var pad = 1; pad <= 45; pad++)
                Assert.True(LandingPadGuide.Describe(state.Docking! with { StationType = type }, pad, Now).Located);
        foreach (var pad in new[] { 0, -1, 46, 64, 65, int.MaxValue })
            Assert.False(LandingPadGuide.Describe(state.Docking, pad, Now).Located);
    }
    [Theory]
    [InlineData("FleetCarrier")][InlineData("SurfaceStation")][InlineData("Outpost")][InlineData(null)]
    public void UnsupportedLayoutsAreNotInvented(string? type) =>
        Assert.False(LandingPadGuide.Describe(Fixture().Docking! with { StationType = type }, 24, Now).Located);
    [Theory]
    [InlineData("DockingCancelled")][InlineData("DockingTimeout")][InlineData("DockingDenied")][InlineData("DockingRequested")]
    [InlineData("Undocked")][InlineData("Shutdown")][InlineData("Died")][InlineData("SupercruiseEntry")][InlineData("FSDJump")]
    [InlineData("LoadGame")][InlineData("ShipyardSwap")][InlineData("Commander")]
    public void OldAssignmentsDoNotSurviveResetEvents(string kind)
    {
        var state = Fixture();
        state.Apply($$"""{"timestamp":"2026-11-13T10:01:00Z","event":"{{kind}}","StationName":"Synthetic Port","Commander":"Synthetic Pad Tester","ShipID":1}""");
        Assert.Null(state.Docking?.Pad);
    }
    [Fact]
    public void OldClearanceCannotBePresentedAsActive()
    {
        var state = Fixture();
        Assert.False(LandingPadGuide.Describe(state.Docking, null, Now.AddMinutes(11)).Located);
        Assert.False(LandingPadGuide.Describe(state.Docking, null, Now.AddMinutes(-2)).Located);
        Assert.False(LandingPadGuide.Describe(null, 24, Now).Located);
        state.Apply("""{"timestamp":"2026-11-13T10:01:00Z","event":"Docked","StationName":"Synthetic Port","StationType":"Coriolis"}""");
        Assert.True(state.Docking!.Docked); Assert.Equal(24, state.Docking.Pad);
    }
    [Fact]
    public void DifferentStationAndNonDockedLocationCannotReuseOldPad()
    {
        var state = Fixture();
        state.Apply("""{"timestamp":"2026-11-13T10:01:00Z","event":"Docked","StationName":"Other Synthetic Port","StationType":"Coriolis"}""");
        Assert.Null(state.Docking!.Pad);
        state.Apply("""{"timestamp":"2026-11-13T10:01:01Z","event":"Location","Docked":false}""");
        Assert.Null(state.Docking);
    }
    [Theory]
    [InlineData("Where is landing pad two four in the space station?")]
    [InlineData("Where's my assigned pad?")]
    [InlineData("Show me landing pad 24 on the docking display")]
    public void CurrentPadQuestionsMayInspectButDoNotSave(string text)
    { Assert.True(ScreenRequestPolicy.CanInspect(text)); Assert.False(ScreenRequestPolicy.CanSave(text)); }
    [Theory]
    [InlineData("Where is pad 24? Do not capture my screen.")]
    [InlineData("Find landing pad 24 in my browser")]
    [InlineData("Automatically find my landing pad whenever I dock")]
    [InlineData("Tell me about landing pad sizes")]
    public void GeneralOrDeniedQuestionsDoNotAuthorizeCapture(string text) => Assert.False(ScreenRequestPolicy.CanInspect(text));
}
