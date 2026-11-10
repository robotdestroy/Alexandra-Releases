using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;
public sealed class MemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alex-memory-tests-" + Guid.NewGuid().ToString("N"));
    private string Journals => Path.Combine(_root, "journals");
    private string Store => Path.Combine(_root, "memory");
    private string Journal => Path.Combine(Journals, "Journal.2026-11-01.01.log");
    public MemoryTests()
    {
        Directory.CreateDirectory(Journals);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "memory-session.jsonl"), Journal);
    }
    private CompanionMemory Create(Func<bool>? enabled = null) => new(() => Journals, Store, enabled);
    private async Task Append(string json) => await File.AppendAllTextAsync(Journal, json + "\n");
    private static JsonElement Root(GameContext c) => JsonDocument.Parse(c.Json).RootElement.Clone();
    private static JsonElement Memory(GameContext c) => Root(c).GetProperty("memory");
    private static JsonElement Items(GameContext c) => Memory(c).GetProperty("savedItems");
    private static JsonElement Inventory(GameContext c) => Memory(c).GetProperty("engineeringMaterials");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task CatchesUpWhileAppWasClosedAndDoesNotDuplicateDeltas()
    {
        var first = Create();
        await first.SynchronizeAsync(default);
        await Append("""{"timestamp":"2026-11-01T11:00:00Z","event":"MaterialCollected","Name":"iron","Category":"Raw","Count":3}""");
        await Append("""{"timestamp":"2026-11-01T11:00:01Z","event":"FSDJump","StarSystem":"Colonia"}""");
        var second = Create();
        var context = await second.ReadAsync(default);
        Assert.Equal("Colonia", context.System);
        Assert.Equal(13, Inventory(context).GetProperty("balances")[0].GetProperty("count").GetInt32());
        Assert.Equal(13, Inventory(await second.ReadAsync(default)).GetProperty("balances")[0].GetProperty("count").GetInt32());
        Assert.DoesNotContain("Fixture Commander", context.Json);
        Assert.DoesNotContain("F-SYNTHETIC", context.Json);
    }
    [Fact]
    public async Task UserGoalsAndVisitedPlacesSurviveRestartWithoutInventingCompletion()
    {
        var first = Create();
        var saved = await first.EditAsync(new("save", "goal", Text: "improve my explorer"), "My goal is to improve my explorer", default);
        Assert.True(saved.Changed);
        await Append("""{"timestamp":"2026-11-01T11:00:00Z","event":"FSDJump","StarSystem":"Deciat"}""");
        var second = Create();
        var context = await second.RecallAsync("What was I working on?", default);
        Assert.Equal("active", Items(context)[0].GetProperty("status").GetString());
        Assert.Equal("Sol", Items(context)[0].GetProperty("system").GetString());
        Assert.Contains("Deciat", Memory(context).GetProperty("recentVisits").GetRawText());
        var completed = await second.EditAsync(new("complete", "goal", saved.Item!.Id), "I finished that goal", default);
        Assert.Equal("completed", completed.Item!.Status);
        await second.EditAsync(new("forget", "goal", saved.Item.Id), "Forget that goal", default);
        Assert.Empty(Items(await Create().ReadAsync(default)).EnumerateArray());
    }
    [Fact]
    public async Task NewGoalsCannotBeInferredFromUnrelatedSpeechOrModelArguments()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().EditAsync(new("save", "goal", Text:"buy gold"), "Hello how are you?", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().EditAsync(new("save", "note", Text:"I own an Anaconda"), "Remember I own a Sidewinder", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().EditAsync(new("save", "note", Text:"this place"), "Don't remember this place", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().EditAsync(new("save", "goal", Text:"build a station"), "Maybe remember build a station", default));
    }
    [Fact]
    public async Task CommanderSwitchKeepsNotesAndInventorySeparate()
    {
        var memory = Create();
        await memory.EditAsync(new("save", "note", Text:"my mining spot"), "Remember my mining spot", default);
        var other = Path.Combine(Journals, "Journal.2026-11-02.01.log");
        await File.WriteAllTextAsync(other, """
            {"timestamp":"2026-11-02T10:00:00Z","event":"LoadGame","Commander":"Fixture B","FID":"F-SYNTHETIC-B","Ship":"sidewinder","ShipID":2}
            {"timestamp":"2026-11-02T10:00:01Z","event":"Location","StarSystem":"Achenar"}

            """);
        var context = await memory.ReadAsync(default);
        Assert.Equal("Achenar", context.System);
        Assert.Empty(Items(context).EnumerateArray());
        Assert.False(Inventory(context).GetProperty("known").GetBoolean());
        await File.AppendAllTextAsync(other, """
            {"timestamp":"2026-11-02T11:00:00Z","event":"LoadGame","Commander":"Fixture Commander A","Ship":"mandalay","ShipID":1}
            {"timestamp":"2026-11-02T11:00:01Z","event":"Location","StarSystem":"Sol"}

            """);
        Assert.Single(Items(await memory.ReadAsync(default)).EnumerateArray());
    }
    [Fact]
    public async Task PartialTrailingLineAndSplitUtf8AreReadExactlyOnce()
    {
        var memory = Create(); await memory.SynchronizeAsync(default);
        var line = System.Text.Encoding.UTF8.GetBytes("""{"timestamp":"2026-11-01T11:00:00Z","event":"FSDJump","StarSystem":"Test é"}""" + "\n");
        var split = Array.IndexOf(line, (byte)0xC3) + 1;
        await using (var file = new FileStream(Journal, FileMode.Append)) await file.WriteAsync(line.AsMemory(0, split));
        Assert.Equal("Sol", (await memory.ReadAsync(default)).System);
        await using (var file = new FileStream(Journal, FileMode.Append)) await file.WriteAsync(line.AsMemory(split));
        Assert.Equal("Test é", (await Create().ReadAsync(default)).System);
    }
    [Fact]
    public async Task MissionProgressRedirectAndCompletionComeFromJournal()
    {
        var memory = Create(); await memory.SynchronizeAsync(default);
        await Append("""{"timestamp":"2026-11-01T11:00:00Z","event":"CargoDepot","MissionID":100,"TotalItemsToDeliver":20,"ItemsDelivered":12}""");
        await Append("""{"timestamp":"2026-11-01T11:00:01Z","event":"MissionRedirected","MissionID":100,"NewDestinationSystem":"Sol","NewDestinationStation":"New Depot"}""");
        var mission = Memory(await memory.ReadAsync(default)).GetProperty("missions").GetProperty("active")[0].GetProperty("mission");
        Assert.Equal(12, mission.GetProperty("delivered").GetInt32());
        Assert.Equal("Sol", mission.GetProperty("destinationSystem").GetString());
        await Append("""{"timestamp":"2026-11-01T11:00:02Z","event":"MissionCompleted","MissionID":100}""");
        var state = Memory(await Create().ReadAsync(default));
        Assert.Empty(state.GetProperty("missions").GetProperty("active").EnumerateArray());
        Assert.Contains("MissionCompleted", state.GetProperty("recentMilestones").GetRawText());
    }
    [Fact]
    public async Task ConstructionSnapshotsReplaceTotalsInsteadOfAddingThem()
    {
        var memory = Create(); await memory.SynchronizeAsync(default);
        await Append("""{"timestamp":"2026-11-01T11:00:00Z","event":"ColonisationConstructionDepot","MarketID":555,"ConstructionComplete":false,"ResourcesRequired":[{"Name":"steel","RequiredAmount":50,"ProvidedAmount":30}]}""");
        var sites = Memory(await memory.ReadAsync(default)).GetProperty("construction");
        Assert.Single(sites.EnumerateArray());
        Assert.Equal(30, sites[0].GetProperty("ResourcesRequired")[0].GetProperty("ProvidedAmount").GetInt32());
    }
    [Fact]
    public async Task MaterialConsumptionAndTradesAreAppliedWithBaseline()
    {
        var memory = Create(); await memory.SynchronizeAsync(default);
        await Append("""{"timestamp":"2026-11-01T11:00:00Z","event":"Synthesis","Materials":[{"Name":"iron","Count":2}]}""");
        await Append("""{"timestamp":"2026-11-01T11:00:01Z","event":"MaterialTrade","Paid":{"Material":"iron","Quantity":3,"Category":"Raw"},"Received":{"Material":"nickel","Quantity":1,"Category":"Raw"}}""");
        var inventory = Inventory(await memory.ReadAsync(default));
        Assert.True(inventory.GetProperty("known").GetBoolean());
        Assert.Equal(5, inventory.GetProperty("balances")[0].GetProperty("count").GetInt32());
        Assert.Equal(1, inventory.GetProperty("balances")[1].GetProperty("count").GetInt32());
    }
    [Fact]
    public async Task MissingBaselineAndUnderflowNeverFabricateBalances()
    {
        await File.WriteAllTextAsync(Journal, """
            {"timestamp":"2026-11-01T10:00:00Z","event":"LoadGame","Commander":"No baseline","FID":"SYNTHETIC","ShipID":1}
            {"timestamp":"2026-11-01T11:00:00Z","event":"MaterialCollected","Name":"iron","Count":3}

            """);
        var inventory = Inventory(await Create().ReadAsync(default));
        Assert.False(inventory.GetProperty("known").GetBoolean());
        Assert.Empty(inventory.GetProperty("balances").EnumerateArray());
    }
    [Fact]
    public async Task InterruptedMemoryChangeDoesNotCommit()
    {
        var memory = Create(); await memory.SynchronizeAsync(default);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => memory.EditAsync(new("save", "note", Text:"this place"), "Remember this place", cancelled.Token));
        Assert.Empty(Items(await Create().ReadAsync(default)).EnumerateArray());
    }
    [Fact]
    public async Task CorruptMemoryIsPreservedNotSilentlyOverwritten()
    {
        await Create().SynchronizeAsync(default);
        var path = Assert.Single(Directory.GetFiles(Store, "memory-*.json"));
        await File.WriteAllTextAsync(path, "{broken");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().ReadAsync(default));
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }
    [Fact]
    public async Task ReplacedJournalRebuildsFactsButKeepsPersonalNotes()
    {
        var memory = Create();
        await memory.EditAsync(new("save", "note", Text:"this place"), "Remember this place", default);
        await File.WriteAllTextAsync(Journal, """
            {"timestamp":"2026-11-03T10:00:00Z","event":"LoadGame","Commander":"Fixture Commander A","ShipID":1}
            {"timestamp":"2026-11-03T10:00:01Z","event":"Location","StarSystem":"Achenar"}

            """);
        var context = await memory.ReadAsync(default);
        Assert.Equal("Achenar", context.System);
        Assert.Single(Items(context).EnumerateArray());
        Assert.False(Inventory(context).GetProperty("known").GetBoolean());
    }
    [Fact]
    public async Task DisabledMemoryDoesNotSaveOrReturnPersonalNotes()
    {
        var memory = Create();
        await memory.EditAsync(new("save", "note", Text:"this place"), "Remember this place", default);
        var disabled = Create(() => false);
        Assert.False(Root(await disabled.ReadAsync(default)).TryGetProperty("memory", out _));
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.EditAsync(new("save", "note", Text:"other place"), "Remember other place", default));
    }
    [Fact]
    public async Task ResearchRemainsSeparateDatedAndBoundToCommander()
    {
        var memory = Create();
        var scope = await memory.GetScopeAsync(default);
        await memory.RememberResearchAsync("Where is gold?", new("Synthetic stock report.", null, [new("Source", "https://inara.cz/elite/")]), scope!, default);
        var context = await Create().RecallAsync("gold", default);
        Assert.Empty(Items(context).EnumerateArray());
        var observation = Assert.Single(Memory(context).GetProperty("researchObservations").EnumerateArray());
        Assert.Equal("research", observation.GetProperty("kind").GetString());
        Assert.True(observation.TryGetProperty("expiresAt", out _));
        await memory.RememberResearchAsync("Different gold", new("Don't retain.", null, [new("Source", "https://inara.cz/elite/")]), "different-scope", default);
        Assert.Single(Memory(await memory.RecallAsync("gold", default)).GetProperty("researchObservations").EnumerateArray());
    }
    [Fact]
    public async Task MissingSidecarsAreNotReusedAndOldSessionFilesAreRejected()
    {
        var memory = Create();
        var cargo = Path.Combine(Journals, "Cargo.json");
        await File.WriteAllTextAsync(cargo, """{"timestamp":"2026-11-01T11:00:00Z","Count":10}""");
        Assert.Contains("Cargo.json", (await memory.ReadAsync(default)).Json);
        File.Delete(cargo);
        Assert.DoesNotContain("Cargo.json", (await memory.ReadAsync(default)).Json);
        await File.WriteAllTextAsync(cargo, """{"timestamp":"2026-10-01T11:00:00Z","Count":100}""");
        Assert.DoesNotContain("Cargo.json", (await memory.ReadAsync(default)).Json);
    }
    [Fact]
    public async Task InitialReplayIsNotLimitedToEightyFiles()
    {
        for (var i = 0; i < 85; i++) await File.WriteAllTextAsync(Path.Combine(Journals, $"Journal.Z{i:D3}.log"), "{\"event\":\"Fileheader\"}\n");
        Assert.Equal("Sol", (await Create().ReadAsync(default)).System);
    }
[Fact]
    public async Task BadMaterialSnapshotCannotClaimAnEmptyKnownInventory()
    {
        var memory = Create(); await memory.SynchronizeAsync(default);
        await Append("""{"timestamp":"2026-11-01T11:00:00Z","event":"Materials","Raw":[{"Name":"iron","Count":"bad"}],"Manufactured":[],"Encoded":[]}""");
        var context = await memory.ReadAsync(default);
        Assert.False(Inventory(context).GetProperty("known").GetBoolean());
        Assert.Empty(Inventory(context).GetProperty("balances").EnumerateArray());
        Assert.False(Root(context).GetProperty("game").GetProperty("facts").TryGetProperty("Materials", out _));
    }
    [Fact]
    public async Task SwitchingCommanderWhileTranscribingRejectsThePendingWrite()
    {
        var memory = Create(); var originalScope = await memory.GetScopeAsync(default);
        await Append("""{"timestamp":"2026-11-01T11:00:00Z","event":"LoadGame","Commander":"Different fixture","FID":"OTHER-SYNTHETIC","ShipID":2}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => memory.EditAsync(new("save", "note", Text:"this place"), "Remember this place", default, originalScope));
        Assert.Empty(Items(await memory.ReadAsync(default)).EnumerateArray());
    }
    [Fact]
    public async Task DuplicateCopiedEventsDoNotDoubleCountSameTimestamp()
    {
        var memory = Create(); await memory.SynchronizeAsync(default);
        const string collected = """{"timestamp":"2026-11-01T11:00:00Z","event":"MaterialCollected","Name":"iron","Category":"Raw","Count":3}""";
        await Append(collected); await Append(collected);
        Assert.Equal(13, Inventory(await memory.ReadAsync(default)).GetProperty("balances")[0].GetProperty("count").GetInt32());
    }
    [Fact]
    public async Task MalformedCompleteLineDoesNotBlockFollowingEvents()
    {
        var memory = Create(); await memory.SynchronizeAsync(default);
        await Append("{bad");
        await Append("""{"timestamp":"2026-11-01T11:00:00Z","event":"FSDJump","StarSystem":"Colonia"}""");
        var context = await memory.ReadAsync(default);
        Assert.Equal("Colonia", context.System);
        Assert.Contains("incomplete", context.Availability);
        Assert.False(Inventory(context).GetProperty("known").GetBoolean());
    }
    [Fact]
    public async Task MissingJournalFolderRetainsDatedMemoriesWithoutCallingThemLive()
    {
        var memory = Create(); await memory.EditAsync(new("save", "note", Text:"this place"), "Remember this place", default);
        Directory.Move(Journals, Journals + "-offline");
        var context = await memory.ReadAsync(default);
        Assert.Single(Items(context).EnumerateArray());
        Assert.Contains("last-known", context.Availability);
        Assert.False(Memory(context).GetProperty("journalFolderAvailable").GetBoolean());
    }
    [Fact]
    public void MemoryToolsAreTypedAndPolicyKeepsPlansSeparateFromFacts()
    {
        var session = JsonSerializer.SerializeToElement(RealtimeProtocol.SessionUpdate("marin")).GetProperty("session");
        Assert.Contains(session.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "update_memory");
        Assert.Contains("NOT verified game facts", RealtimeProtocol.Instructions);
        Assert.Contains("short EXACT phrase", RealtimeProtocol.Instructions);
    }
}

