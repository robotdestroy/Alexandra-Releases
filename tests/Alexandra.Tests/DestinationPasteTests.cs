using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class DestinationPasteTests
{
    [Theory]
    [InlineData("Paste")]
    [InlineData("Paste that.")]
    [InlineData("Can you paste the location?")]
    [InlineData("Could you please paste that for me?")]
    [InlineData("Please paste the system into the galaxy map search box.")]
    [InlineData("Paste what you copied")]
    [InlineData("Paste Sol")]
    [InlineData("Alexandra, paste the copied destination, please.")]
    [InlineData("Go ahead and paste it")]
    [InlineData("paste now")]
    [InlineData("paste please")]
    [InlineData("paste for me")]
    [InlineData("paste it now please")]
    [InlineData("Paste that again please.")]
    [InlineData("paste Sol into the search field please")]
    public void ExplicitPasteRequestsAreAllowed(string utterance) => Assert.True(PasteRequestPolicy.CanPaste(utterance, "Sol"));

    [Theory]
    [InlineData("Don't paste that")]
    [InlineData("Do not paste")]
    [InlineData("Can you not paste that?")]
    [InlineData("How do I paste?")]
    [InlineData("What happens when I say paste?")]
    [InlineData("Did you paste the location?")]
    [InlineData("Copy Sol so I can paste it")]
    [InlineData("Paste Colonia")]
    [InlineData("Paste that into Discord")]
    [InlineData("Paste the system into my browser")]
    [InlineData("Research how to paste a destination")]
    [InlineData("When I'm ready, paste that")]
    [InlineData("Paste that and press Enter")]
    [InlineData("Paste the password")]
    [InlineData("Paste everything")]
    [InlineData("Paste that but not yet")]
    [InlineData("Can you add a paste feature?")]
    [InlineData("")]
    public void OtherIntentsNeverAuthorizeInput(string utterance) => Assert.False(PasteRequestPolicy.CanPaste(utterance, "Sol"));

    [Fact]
    public async Task OnlyVerifiedRememberedCopyReachesDriver()
    {
        var memory = new CopiedDestinationMemory();
        var driver = new Driver();
        var service = new DestinationPasteService(memory, driver);
        Assert.Equal("no_verified_copy", (await service.PasteAsync("paste that", default)).Status);
        memory.Remember("Sol", 42);
        Assert.True((await service.PasteAsync("paste that", default)).PasteSent);
        Assert.Equal("Sol", Assert.Single(driver.Calls).System);
        Assert.Equal(42u, driver.Calls[0].ClipboardSequence);
        Assert.False((await service.PasteAsync("paste Colonia", default)).PasteSent);
        Assert.Single(driver.Calls);
    }
    [Fact]
    public async Task ChangedClipboardInvalidatesPasteMemoryWithoutRestoringIt()
    {
        var memory = new CopiedDestinationMemory();
        memory.Remember("Sol", 42);
        var driver = new Driver { Result = DestinationPasteResult.Blocked("clipboard_changed", "Changed.") };
        var result = await new DestinationPasteService(memory, driver).PasteAsync("paste", default);
        Assert.False(result.PasteSent);
        Assert.Null(memory.Read());
    }
    [Theory]
    [InlineData("Sol", 0u)]
    [InlineData("Sol\nColonia", 42u)]
    public void InvalidCopyCannotCreatePasteProvenance(string system, uint sequence)
    {
        var memory = new CopiedDestinationMemory();
        memory.Remember(system, sequence);
        Assert.Null(memory.Read());
    }
    [Fact]
    public async Task CancellationBeforeRequestNeverCallsDriver()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var driver = new Driver();
        var memory = new CopiedDestinationMemory();
        memory.Remember("Sol", 42);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DestinationPasteService(memory, driver).PasteAsync("paste", cancel.Token));
        Assert.Empty(driver.Calls);
    }
    [Fact]
    public async Task GestureSendsOnlyControlVAndReleasesBoth()
    {
        var keys = new Keys();
        var delays = new List<TimeSpan>();
        Assert.True(await new FixedPasteGesture(keys, () => true, (duration, _) => { delays.Add(duration); return Task.CompletedTask; }).SendAsync(default));
        Assert.Equal(new[] { "ctrl down", "v down", "v up", "ctrl up" }, keys.Actions);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(55) }, delays);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InterruptAlwaysReleasesExactlyTheKeysPressed(int duringDelay)
    {
        var keys = new Keys();
        using var cancel = new CancellationTokenSource();
        var delays = 0;
        var gesture = new FixedPasteGesture(keys, () => true, (_, token) => {
            if (++delays == duringDelay) cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gesture.SendAsync(cancel.Token));
        Assert.Equal(duringDelay == 1 ? new[] { "ctrl down", "ctrl up" } : new[] { "ctrl down", "v down", "v up", "ctrl up" }, keys.Actions);
    }
    [Fact]
    public async Task FocusOrClipboardChangePreventsPasteAndStillReleasesControl()
    {
        var keys = new Keys(); var safe = true;
        var gesture = new FixedPasteGesture(keys, () => safe, (_, _) => { safe = false; return Task.CompletedTask; });
        Assert.False(await gesture.SendAsync(default));
        Assert.Equal(new[] { "ctrl down", "ctrl up" }, keys.Actions);
    }
    [Fact]
    public async Task NoKeyDownWhenInitialSafetyCheckFails()
    {
        var keys = new Keys();
        Assert.False(await new FixedPasteGesture(keys, () => false).SendAsync(default));
        Assert.Empty(keys.Actions);
    }
    [Fact]
    public async Task RejectedVKeyStillReleasesControl()
    {
        var keys = new Keys { AcceptV = false };
        Assert.False(await new FixedPasteGesture(keys, () => true, (_, _) => Task.CompletedTask).SendAsync(default));
        Assert.Equal(new[] { "ctrl down", "v rejected", "ctrl up" }, keys.Actions);
    }
    [Fact]
    public async Task ReleaseFailureStillAttemptsControlCleanup()
    {
        var keys = new Keys { FailVRelease = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FixedPasteGesture(keys, () => true, (_, _) => Task.CompletedTask).SendAsync(default));
        Assert.Equal(new[] { "ctrl down", "v down", "v up", "ctrl up" }, keys.Actions);
    }
    [Fact]
    public void OnlyRecentSameProcessGalaxyMapEvidenceIsAccepted()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "status-galaxy-map.json"));
        var now = DateTimeOffset.Parse("2026-11-01T10:00:10Z");
        Assert.True(GalaxyMapPasteEvidence.IsReady(json, now.AddMinutes(-1), now));
        Assert.False(GalaxyMapPasteEvidence.IsReady(json.Replace("\"GuiFocus\":6", "\"GuiFocus\":0"), now.AddMinutes(-1), now));
        Assert.False(GalaxyMapPasteEvidence.IsReady(json.Replace("\"GuiFocus\":6", "\"GuiFocus\":7"), now.AddMinutes(-1), now));
        Assert.False(GalaxyMapPasteEvidence.IsReady(json, now.AddHours(-1), now.AddMinutes(6)));
        Assert.False(GalaxyMapPasteEvidence.IsReady(json, now, now)); // Status predates game process.
        Assert.False(GalaxyMapPasteEvidence.IsReady(json, now.AddHours(-1), now.AddMinutes(-1))); // Future timestamp.
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"event\":\"Status\",\"GuiFocus\":\"6\",\"timestamp\":\"2026-11-01T10:00:00Z\"}")]
    public void MissingOrMalformedMapEvidenceBlocksInput(string json) =>
        Assert.False(GalaxyMapPasteEvidence.IsReady(json, DateTimeOffset.MinValue.AddDays(1), DateTimeOffset.UtcNow));

    private sealed class Driver : IDestinationPasteDriver
    {
        public List<CopiedDestination> Calls { get; } = [];
        public DestinationPasteResult Result { get; init; } = new(true, "Sol", "paste_sent", "Paste sent; no acceptance verified.");
        public Task<DestinationPasteResult> PasteAsync(CopiedDestination destination, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls.Add(destination); return Task.FromResult(Result); }
    }
    private sealed class Keys : IFixedPasteKeys
    {
        public List<string> Actions { get; } = [];
        public bool AcceptV { get; init; } = true;
        public bool FailVRelease { get; init; }
        public bool PressControl() { Actions.Add("ctrl down"); return true; }
        public bool PressV() { Actions.Add(AcceptV ? "v down" : "v rejected"); return AcceptV; }
        public void ReleaseV() { Actions.Add("v up"); if (FailVRelease) throw new InvalidOperationException("Synthetic release failure."); }
        public void ReleaseControl() => Actions.Add("ctrl up");
    }
}
