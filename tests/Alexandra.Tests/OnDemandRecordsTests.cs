using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class OnDemandRecordsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alexandra-record-tests-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_root, "Journal.test.log");
    public OnDemandRecordsTests()
    {
        Directory.CreateDirectory(_root);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "on-demand-records.jsonl"), Journal);
    }
    private Task Add(string json) => File.AppendAllTextAsync(Journal, "\n" + json + "\n");
    private async Task<JsonElement> Lookup(GameRecordTopic topic)
        => JsonSerializer.Deserialize<JsonElement>((await new OnDemandGameRecords(() => _root).LookupAsync(topic, default)).Json);
    private async Task<JsonElement> Data(GameRecordTopic topic) => (await Lookup(topic)).GetProperty("data");
    [Fact]
    public async Task CarrierRequestIsNotArrivalAndOtherCarrierIsNotOwned()
    {
        var d = await Data(GameRecordTopic.Carrier);
        Assert.True(d.GetProperty("identified").GetBoolean());
        Assert.Equal("TST-123", d.GetProperty("identity").GetProperty("data").GetProperty("Callsign").GetString());
        Assert.Equal("Sol", d.GetProperty("lastObservedLocation").GetProperty("data").GetProperty("system").GetString());
        Assert.Equal("Achenar", d.GetProperty("requestedJump").GetProperty("data").GetProperty("SystemName").GetString());
        Assert.Equal(60, d.GetProperty("lastObservedFuel").GetProperty("data").GetProperty("Total").GetInt32());
        Assert.Contains("not an arrival", d.GetProperty("jumpRule").GetString());
        Assert.DoesNotContain("Private Synthetic Crew", d.GetRawText());
    }
    [Fact]
    public async Task CancellationClearsRequestWithoutMovingCarrier()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"CarrierJumpCancelled","CarrierID":700}""");
        var d = await Data(GameRecordTopic.Carrier);
        Assert.Equal(JsonValueKind.Null, d.GetProperty("requestedJump").ValueKind);
        Assert.Equal("Sol", d.GetProperty("lastObservedLocation").GetProperty("data").GetProperty("system").GetString());
    }
    [Fact]
    public async Task ConfirmedJumpChangesLocationAndInvalidatesOldFuel()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"CarrierJump","MarketID":700,"StarSystem":"Achenar","StationType":"FleetCarrier","Docked":true}""");
        var d = await Data(GameRecordTopic.Carrier);
        Assert.Equal("Achenar", d.GetProperty("lastObservedLocation").GetProperty("data").GetProperty("system").GetString());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("requestedJump").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, d.GetProperty("fuelUncertainSince").ValueKind);
    }
    [Fact]
    public async Task CarrierTradeCancellationRemovesOnlyThatMarketOrder()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"CarrierTradeOrder","CarrierID":700,"Commodity":"gold","PurchaseOrder":2,"BlackMarket":true}""");
        await Add("""{"timestamp":"2026-11-01T11:00:01Z","event":"CarrierTradeOrder","CarrierID":700,"Commodity":"gold","CancelTrade":true,"BlackMarket":false}""");
        var order = Assert.Single((await Data(GameRecordTopic.Carrier)).GetProperty("tradeOrders").EnumerateArray());
        Assert.True(order.GetProperty("data").GetProperty("BlackMarket").GetBoolean());
    }
    [Fact]
    public async Task NoOwnerStatsDoesNotTurnDockedCarrierIntoOwnCarrier()
    {
        File.WriteAllText(Journal, """
            {"timestamp":"2026-11-01T10:00:01Z","event":"LoadGame","Commander":"Synthetic Beta","FID":"SYNTHETIC-BETA"}
            {"timestamp":"2026-11-01T10:00:02Z","event":"Docked","StarSystem":"Sol","MarketID":900,"StationType":"FleetCarrier"}
            """);
        Assert.False((await Data(GameRecordTopic.Carrier)).GetProperty("identified").GetBoolean());
    }
    [Fact]
    public async Task StoredAndInstalledModulesKeepTheirOwnLocationsAndEngineering()
    {
        var d = await Data(GameRecordTopic.Modules);
        var items = d.GetProperty("storedModules").GetProperty("data").GetProperty("Items");
        Assert.Equal("Achenar", items[0].GetProperty("StarSystem").GetString());
        Assert.False(items[0].TryGetProperty("StationName", out _));
        Assert.Equal("FSD_LongRange", items[1].GetProperty("EngineerModifications").GetString());
        Assert.True(items[2].GetProperty("InTransit").GetBoolean());
        Assert.Equal("special_fsd_heavy", d.GetProperty("currentShipLoadout").GetProperty("data").GetProperty("Modules")[0]
            .GetProperty("Engineering").GetProperty("ExperimentalEffect").GetString());
        Assert.Equal("Mass Manager", d.GetProperty("currentShipLoadout").GetProperty("data").GetProperty("Modules")[0]
            .GetProperty("Engineering").GetProperty("ExperimentalEffectDisplay").GetString());
        Assert.True(d.GetProperty("storageUnchangedSinceSnapshot").GetBoolean());
        Assert.True(d.GetProperty("loadoutUnchangedSinceSnapshot").GetBoolean());
    }
    [Fact]
    public async Task ModuleChangesInvalidateSnapshotsUntilFreshEvidence()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"ModuleRetrieve","ShipID":1,"Slot":"Slot01","RetrievedItem":"int_fuelscoop_size6_class5"}""");
        var old = await Data(GameRecordTopic.Modules);
        Assert.False(old.GetProperty("storageUnchangedSinceSnapshot").GetBoolean());
        Assert.False(old.GetProperty("loadoutUnchangedSinceSnapshot").GetBoolean());
        await Add("""{"timestamp":"2026-11-01T11:00:01Z","event":"StoredModules","Items":[]}""");
        await Add("""{"timestamp":"2026-11-01T11:00:01Z","event":"Loadout","ShipID":1,"Modules":[]}""");
        var fresh = await Data(GameRecordTopic.Modules);
        Assert.True(fresh.GetProperty("storageUnchangedSinceSnapshot").GetBoolean());
        Assert.True(fresh.GetProperty("loadoutUnchangedSinceSnapshot").GetBoolean());
    }
    [Fact]
    public async Task ShipSaleRemovesHistoricalLoadoutAndInvalidatesFleetSnapshot()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"ShipyardSell","SellShipID":1}""");
        var d = await Data(GameRecordTopic.Ships);
        Assert.False(d.GetProperty("inventoryUnchangedSinceSnapshot").GetBoolean());
        Assert.Empty(d.GetProperty("lastRecordedLoadouts").EnumerateArray());
    }
    [Fact]
    public async Task ShipSwapDoesNotReturnPreviousShipsModules()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"ShipyardSwap","ShipID":2}""");
        var d = await Data(GameRecordTopic.Modules);
        Assert.Equal(2, d.GetProperty("activeShipId").GetInt32());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("currentShipLoadout").ValueKind);
    }
    [Fact]
    public async Task CommanderSwitchDoesNotLeakInventoryOrCarrier()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"LoadGame","Commander":"Synthetic Beta","FID":"SYNTHETIC-BETA","ShipID":20}""");
        Assert.False((await Data(GameRecordTopic.Carrier)).GetProperty("identified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await Data(GameRecordTopic.Modules)).GetProperty("storedModules").ValueKind);
        await Add("""{"timestamp":"2026-11-01T12:00:00Z","event":"LoadGame","Commander":"Synthetic Alpha","FID":"SYNTHETIC-ALPHA","ShipID":1}""");
        Assert.True((await Data(GameRecordTopic.Carrier)).GetProperty("identified").GetBoolean());
    }
    [Fact]
    public async Task PublicToolPayloadDoesNotContainCommanderIdentityOrRawPrivateFields()
    {
        var result = await new OnDemandGameRecords(() => _root).LookupAsync(GameRecordTopic.Carrier, default);
        Assert.NotNull(result.MemoryScope);
        Assert.DoesNotContain("SYNTHETIC-ALPHA", result.Json);
        Assert.DoesNotContain("Synthetic Alpha", result.Json);
        Assert.DoesNotContain("FID", result.Json);
        Assert.DoesNotContain("CrewName", result.Json);
    }
    [Fact]
    public async Task HistoricalLoadoutCanBeRequestedByVerifiedShipId()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"ShipyardSwap","ShipID":2}""");
        var result = await new OnDemandGameRecords(() => _root).LookupAsync(GameRecordTopic.Modules, default, 1);
        var d = JsonSerializer.Deserialize<JsonElement>(result.Json).GetProperty("data");
        Assert.Equal(JsonValueKind.Null, d.GetProperty("currentShipLoadout").ValueKind);
        Assert.Equal("Synthetic Explorer", d.GetProperty("requestedShipLastRecordedLoadout").GetProperty("data").GetProperty("ShipName").GetString());
    }
    [Fact]
    public async Task InvalidLoadoutDoesNotLeavePreviousModulesCurrent()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"Loadout","ShipID":1}""");
        Assert.False((await Data(GameRecordTopic.Modules)).GetProperty("loadoutUnchangedSinceSnapshot").GetBoolean());
    }
    [Fact]
    public async Task ClearingSaveDoesNotResurrectOldOwnership()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"ClearSavedGame"}""");
        Assert.False((await Data(GameRecordTopic.Carrier)).GetProperty("identified").GetBoolean());
    }
    [Fact]
    public async Task MalformedTailIsSkippedAndReported()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"StoredModules","Items":[""");
        var d = await Lookup(GameRecordTopic.Modules);
        Assert.Equal(1, d.GetProperty("coverage").GetProperty("unreadableRecordsOrFiles").GetInt32());
    }
    [Fact]
    public async Task RepeatedLookupReadsNewEventsWithoutAnyBackgroundWork()
    {
        var source = new OnDemandGameRecords(() => _root);
        var first = await source.LookupAsync(GameRecordTopic.Carrier, default);
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"CarrierLocation","CarrierID":700,"StarSystem":"Colonia"}""");
        var second = await source.LookupAsync(GameRecordTopic.Carrier, default);
        Assert.DoesNotContain("Colonia", first.Json);
        Assert.Contains("Colonia", second.Json);
        Assert.Single(Directory.GetFiles(_root)); // No cache or checkpoint files.
    }
    [Fact]
    public async Task NewUnidentifiedSessionDoesNotReuseOldCommander()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"FileHeader","part":1}""");
        Assert.False((await Lookup(GameRecordTopic.Carrier)).GetProperty("available").GetBoolean());
    }
    [Fact]
    public async Task ContinuedJournalKeepsCommanderAndEventTimesBeatFilenameOrder()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Journal.aaa.log"), """
            {"timestamp":"2026-11-01T11:00:00Z","event":"FileHeader","part":2}
            {"timestamp":"2026-11-01T11:00:01Z","event":"CarrierLocation","CarrierID":700,"StarSystem":"Colonia"}
            """);
        Assert.Equal("Colonia", (await Data(GameRecordTopic.Carrier)).GetProperty("lastObservedLocation").GetProperty("data").GetProperty("system").GetString());
    }
    [Fact]
    public async Task NewShipAndInsuranceSaleUseDocumentedFieldCasing()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"ShipyardNew","NewShipID":5}""");
        Assert.Equal(5, (await Data(GameRecordTopic.Modules)).GetProperty("activeShipId").GetInt64());
        await Add("""{"timestamp":"2026-11-01T11:00:01Z","event":"SellShipOnRebuy","SellShipId":1}""");
        Assert.Empty((await Data(GameRecordTopic.Ships)).GetProperty("lastRecordedLoadouts").EnumerateArray());
    }
    [Theory]
    [InlineData("PriceList")]
    [InlineData("Pricelist")]
    public async Task ShipyardSupportsBothDocumentedPriceListCasings(string field)
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"Shipyard","MarketID":1000}""");
        await File.WriteAllTextAsync(Path.Combine(_root, "Shipyard.json"), JsonSerializer.Serialize(new Dictionary<string, object> {
            ["timestamp"] = "2026-11-01T11:00:00Z", ["event"] = "Shipyard", ["MarketID"] = 1000,
            [field] = new[] { new { ShipType = "sidewinder", ShipPrice = 32000 } }
        }));
        var shipyard = (await Data(GameRecordTopic.Station)).GetProperty("catalogs").GetProperty("Shipyard");
        Assert.True(shipyard.GetProperty("available").GetBoolean());
        Assert.Equal(32000, shipyard.GetProperty("observation").GetProperty("data").GetProperty(field)[0].GetProperty("ShipPrice").GetInt32());
    }
    [Fact]
    public async Task MissingCompanionInventoryIsUnknownNotEmpty()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"Backpack"}""");
        await File.WriteAllTextAsync(Path.Combine(_root, "Backpack.json"), """{"timestamp":"2026-11-01T11:00:00Z","event":"Backpack"}""");
        Assert.False((await Data(GameRecordTopic.OnFoot)).GetProperty("backpack").GetProperty("available").GetBoolean());
    }
    private async Task Market(string sidecarTime = "2026-11-01T11:00:01Z", int marketId = 1000)
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"Docked","MarketID":1000,"StarSystem":"Sol","StationName":"Test Port"}""");
        await Add("""{"timestamp":"2026-11-01T11:00:01Z","event":"Market","MarketID":1000,"StarSystem":"Sol","StationName":"Test Port"}""");
        await File.WriteAllTextAsync(Path.Combine(_root, "Market.json"), JsonSerializer.Serialize(new { timestamp = sidecarTime,
            @event = "Market", MarketID = marketId, StarSystem = "Sol", StationName = "Test Port",
            Items = new[] { new { Name = "gold", BuyPrice = 40000, Stock = 15 } } }));
    }
    [Fact]
    public async Task StationCatalogRequiresMatchingJournalEvidenceAndPreservesPrices()
    {
        await Market();
        var market = (await Data(GameRecordTopic.Station)).GetProperty("catalogs").GetProperty("Market");
        Assert.True(market.GetProperty("available").GetBoolean());
        Assert.True(market.GetProperty("atCurrentStation").GetBoolean());
        Assert.Equal(40000, market.GetProperty("observation").GetProperty("data").GetProperty("Items")[0].GetProperty("BuyPrice").GetInt32());
        await Add("""{"timestamp":"2026-11-01T12:00:00Z","event":"Undocked"}""");
        Assert.False((await Data(GameRecordTopic.Station)).GetProperty("catalogs").GetProperty("Market").GetProperty("atCurrentStation").GetBoolean());
    }
    [Theory]
    [InlineData("2026-11-01T10:00:01Z", 1000)]
    [InlineData("2026-11-01T11:00:01Z", 9999)]
    public async Task WrongCompanionFileIsRejected(string time, int id)
    {
        await Market(time, id);
        Assert.False((await Data(GameRecordTopic.Station)).GetProperty("catalogs").GetProperty("Market").GetProperty("available").GetBoolean());
    }
    [Fact]
    public async Task AnotherCommandersSidecarIsNotReturned()
    {
        await Market();
        await Add("""{"timestamp":"2026-11-01T12:00:00Z","event":"LoadGame","Commander":"Synthetic Beta","FID":"SYNTHETIC-BETA"}""");
        Assert.False((await Data(GameRecordTopic.Station)).GetProperty("catalogs").GetProperty("Market").GetProperty("available").GetBoolean());
    }
    [Fact]
    public async Task OnFootReadsCompanionInventoryAndRemovesOwnerIdentifiers()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"Backpack"}""");
        await File.WriteAllTextAsync(Path.Combine(_root, "Backpack.json"), """
            {"timestamp":"2026-11-01T11:00:00Z","event":"Backpack","Items":[{"Name":"medkit","Count":2,"OwnerID":123}],"Components":[],"Data":[],"Consumables":[]}
            """);
        var d = await Data(GameRecordTopic.OnFoot);
        Assert.True(d.GetProperty("backpack").GetProperty("available").GetBoolean());
        Assert.Contains("medkit", d.GetRawText());
        Assert.DoesNotContain("OwnerID", d.GetRawText());
    }
    [Fact]
    public async Task InvalidSnapshotDoesNotClaimAnEmptyInventory()
    {
        await Add("""{"timestamp":"2026-11-01T11:00:00Z","event":"StoredModules"}""");
        var d = await Data(GameRecordTopic.Modules);
        Assert.Equal(JsonValueKind.Null, d.GetProperty("storedModules").ValueKind);
        Assert.False(d.GetProperty("storageUnchangedSinceSnapshot").GetBoolean());
    }
    [Fact]
    public async Task MissingFolderAndCancellationAreHandledWithoutInventingData()
    {
        var source = new OnDemandGameRecords(() => Path.Combine(_root, "missing"));
        Assert.Contains("available", (await source.LookupAsync(GameRecordTopic.Modules, default)).Json);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.LookupAsync(GameRecordTopic.Modules, canceled.Token));
    }
    [Fact]
    public void TopicsAreAnAllowlistNotAnArbitraryFilePath()
    {
        Assert.Equal(GameRecordTopic.Carrier, GameRecordTopics.Parse("carrier"));
        Assert.Throws<InvalidOperationException>(() => GameRecordTopics.Parse("../settings.json"));
    }
    public void Dispose() => Directory.Delete(_root, true); // Only this test's uniquely created fixture directory.
}
