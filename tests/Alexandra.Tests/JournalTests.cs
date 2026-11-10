using System.Text.Json;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class JournalTests
{
    private static JournalContextSource.JournalSnapshot Seed()
    {
        var state = new JournalContextSource.JournalSnapshot();
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "commander.jsonl"))) state.Apply(line);
        return state;
    }
    private static JsonElement Facts(JournalContextSource.JournalSnapshot state) =>
        JsonDocument.Parse(state.Build().Json).RootElement.GetProperty("facts").Clone();

    [Fact]
    public void UsesLoadoutAndDoesNotUploadCommanderIdentity()
    {
        var state = Seed();
        Assert.Equal("Sol", state.Build().System);
        Assert.Equal("mandalay", Facts(state).GetProperty("Loadout").GetProperty("Ship").GetString());
        Assert.DoesNotContain("Synthetic Commander", state.Build().Json);
        Assert.DoesNotContain("F123456", state.Build().Json);
    }
    [Fact]
    public void AnotherCommanderCannotInheritThePreviousInventory()
    {
        var state = Seed();
        state.Apply("""{"timestamp":"2026-10-26T13:00:00Z","event":"LoadGame","Commander":"Different Synthetic Commander","Ship":"sidewinder","ShipID":10}""");
        Assert.Null(state.Build().System);
        Assert.False(Facts(state).TryGetProperty("Materials", out _));
        Assert.False(Facts(state).TryGetProperty("Loadout", out _));
        Assert.Equal("sidewinder", Facts(state).GetProperty("Ship").GetProperty("Type").GetString());
    }
    [Fact]
    public void ShipChangesInvalidateOldLoadout()
    {
        var state = Seed();
        state.Apply("""{"timestamp":"2026-10-26T13:00:00Z","event":"ShipyardSwap","ShipType":"anaconda","ShipID":20}""");
        Assert.False(Facts(state).TryGetProperty("Loadout", out _));
    }
    [Fact]
    public void MissingIncrementalInventoryIsUnknownNotStale()
    {
        var state = Seed();
        state.Apply("""{"timestamp":"2026-10-26T13:00:00Z","event":"MaterialCollected","Category":"Raw","Name":"iron","Count":3}""");
        Assert.False(Facts(state).TryGetProperty("Materials", out _));
    }
    [Fact]
    public void OlderEventsCannotRewindCurrentSystem()
    {
        var state = Seed();
        state.Apply("""{"timestamp":"2026-10-25T10:00:00Z","event":"FSDJump","StarSystem":"Colonia"}""");
        Assert.Equal("Sol", state.Build().System);
    }
    [Fact]
    public void UndockingClearsStation()
    {
        var state = Seed();
        state.Apply("""{"timestamp":"2026-10-26T13:00:00Z","event":"Undocked","StationName":"Synthetic Station"}""");
        Assert.False(Facts(state).TryGetProperty("Docked", out _));
    }
    [Fact]
    public async Task MissingJournalFolderDoesNotFabricateGameState()
    {
        var source = new JournalContextSource(() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var context = await source.ReadAsync(CancellationToken.None);
        Assert.Null(context.System);
        Assert.Equal("{}", context.Json);
        Assert.Contains("not found", context.Availability);
    }
    [Fact]
    public void ExposesPartialHistory()
    {
        var state = Seed();
        using var doc = JsonDocument.Parse(state.Build(true, 2).Json);
        Assert.True(doc.RootElement.GetProperty("historyLimited").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("skippedRecords").GetInt32());
    }
}

