using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class CombatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alex-combat-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private string Journal => Path.Combine(_root, "Journal.2026-11-11.01.log");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public CombatTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private CombatContextSource Source() => new(() => _root, _clock);
    private async Task Fixture()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-journal.jsonl"), Journal);
        await Status();
    }
    private Task Status(string time = "10:00:12", long flags = (1L << 24) | 8, string pips = "[4,4,4]") =>
        File.WriteAllTextAsync(Path.Combine(_root, "Status.json"),
            $$"""{"timestamp":"2026-11-11T{{time}}Z","event":"Status","Flags":{{flags}},"Pips":{{pips}},"FireGroup":1}""");
    private Task Append(string line) => File.AppendAllTextAsync(Journal, line + "\n");
    private static JsonElement Root(CombatContext c) => JsonDocument.Parse(c.Json).RootElement.Clone();
    private static JsonElement Combat(CombatContext c) => Root(c).GetProperty("combat");
    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static void Apply(CombatState state, string json) => state.Apply(Element(json));
    private static JsonElement View(CombatState state, string? status, DateTimeOffset? now = null) =>
        JsonSerializer.SerializeToElement(state.View(status is null ? null : Element(status), now ?? DateTimeOffset.Parse("2026-11-11T10:00:12Z")), Json);

    [Fact]
    public async Task ReadsCockpitTargetFitAndSeparatedHullWithoutIdentity()
    {
        await Fixture(); var result = await Source().ReadAsync(default);
        Assert.True(result.LiveAvailable);
        var combat = Combat(result);
        Assert.True(combat.GetProperty("cockpit").GetProperty("shieldsUp").GetBoolean());
        Assert.Equal(2, combat.GetProperty("cockpit").GetProperty("pips").GetProperty("weapons").GetDouble());
        Assert.Equal(60, combat.GetProperty("lastReportedHull").GetProperty("percent").GetDouble());
        Assert.Equal(70, combat.GetProperty("lastTargetScan").GetProperty("shieldPercent").GetDouble());
        Assert.Equal("Wanted", combat.GetProperty("lastTargetScan").GetProperty("legalStatus").GetString());
        Assert.Equal(20, combat.GetProperty("recentCombat").GetProperty("lowestReportedFighterHull").GetDouble());
        Assert.Equal(325000, combat.GetProperty("recentCombat").GetProperty("rewardCredits").GetDecimal());
        Assert.Equal("anaconda", Root(result).GetProperty("loadout").GetProperty("shipType").GetString());
        Assert.DoesNotContain("Synthetic Combat Pilot", result.Json);
        Assert.DoesNotContain("SYNTHETIC-COMBAT-A", result.Json);
        Assert.DoesNotContain("Synthetic Enemy Never Export", result.Json);
        Assert.DoesNotContain("VictimFaction", result.Json);
        Assert.True(result.Callouts!.Eligible); Assert.True(result.Callouts.CombatActive);
        Assert.Equal(60, result.Callouts.HullPercent); // NPC fighter damage must not trigger a main-ship callout.
    }
    [Theory]
    [InlineData("[0,4,8]", true)]
    [InlineData("[4,4,4]", true)]
    [InlineData("[0,0,0]", false)]
    [InlineData("[9,1,2]", false)]
    [InlineData("[4,4]", false)]
    [InlineData("[4,\"4\",4]", false)]
    public async Task ValidatesHalfPipsIncludingZero(string pips, bool valid)
    {
        await Fixture(); await Status(pips: pips);
        var pip = Combat(await Source().ReadAsync(default)).GetProperty("cockpit").GetProperty("pips");
        Assert.Equal(valid, pip.ValueKind == JsonValueKind.Object);
        if (pips == "[0,4,8]") { Assert.Equal(0, pip.GetProperty("systems").GetDouble()); Assert.Equal(4, pip.GetProperty("weapons").GetDouble()); }
    }
    [Theory]
    [InlineData("09:59:59", 16777224L)]
    [InlineData("10:00:30", 16777224L)]
    [InlineData("10:00:12", -1L)]
    [InlineData("10:00:12", 0L)]
    public async Task InvalidFuturePreSessionOrUnknownVehicleIsNotLive(string time, long flags)
    {
        await Fixture(); await Status(time, flags);
        Assert.False((await Source().ReadAsync(default)).LiveAvailable);
    }
    [Fact]
    public async Task AgedStatusIsExplicitlyNotLiveAndNotAHeartbeat()
    {
        await Fixture(); var source = Source(); await source.ReadAsync(default);
        _clock.Now = _clock.Now.AddMinutes(1);
        var result = await source.ReadAsync(default);
        Assert.False(result.LiveAvailable);
        Assert.False(Combat(result).GetProperty("statusFresh").GetBoolean());
        Assert.Contains("not a heartbeat", result.Json);
        File.Delete(Path.Combine(_root, "Status.json"));
        Assert.Equal(JsonValueKind.Null, Combat(await source.ReadAsync(default)).GetProperty("cockpit").ValueKind);
    }
    [Fact]
    public async Task FighterStatusDoesNotReuseMainShipHull()
    {
        await Fixture(); await Status(flags: 1L << 25);
        Assert.Equal(20, Combat(await Source().ReadAsync(default)).GetProperty("lastReportedHull").GetProperty("percent").GetDouble());
        await Status(flags: 1L << 26);
        var srv = await Source().ReadAsync(default);
        Assert.False(srv.LiveAvailable);
        Assert.Equal(JsonValueKind.Null, Combat(srv).GetProperty("lastReportedHull").ValueKind);
    }
    [Theory]
    [InlineData("SupercruiseEntry")]
    [InlineData("Docked")]
    [InlineData("VehicleSwitch")]
    [InlineData("ShipyardSwap")]
    [InlineData("Died")]
    [InlineData("Shutdown")]
    public async Task ContextTransitionsClearTargetsAndRejectPreTransitionStatus(string kind)
    {
        await Fixture(); var source = Source(); await source.ReadAsync(default);
        await Append($$"""{"timestamp":"2026-11-11T10:00:13Z","event":"{{kind}}"}""");
        _clock.Now = _clock.Now.AddSeconds(2);
        var result = await source.ReadAsync(default);
        Assert.False(result.LiveAvailable);
        Assert.Equal(JsonValueKind.Null, Combat(result).GetProperty("lastTargetScan").ValueKind);
    }
    [Fact]
    public async Task TargetLossAndScanStagesDontReusePriorTargetDetails()
    {
        await Fixture(); var source = Source(); await source.ReadAsync(default);
        await Append("""{"timestamp":"2026-11-11T10:00:12Z","event":"ShipTargeted","TargetLocked":true,"ScanStage":0,"Ship":"eagle","HullHealth":50,"LegalStatus":"Wanted"}""");
        var target = Combat(await source.ReadAsync(default)).GetProperty("lastTargetScan");
        Assert.Equal("eagle", target.GetProperty("ship").GetString());
        Assert.Equal(JsonValueKind.Null, target.GetProperty("hullPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("legalStatus").ValueKind);
        await Append("""{"timestamp":"2026-11-11T10:00:12Z","event":"ShipTargeted","TargetLocked":false}""");
        target = Combat(await source.ReadAsync(default)).GetProperty("lastTargetScan");
        Assert.False(target.GetProperty("locked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, target.GetProperty("ship").ValueKind);
    }
    [Fact]
    public async Task IncrementalPartialLineAndDuplicateReadsAreSafe()
    {
        await Fixture(); var source = Source(); var before = await source.ReadAsync(default);
        var line = """{"timestamp":"2026-11-11T10:00:12Z","event":"Bounty","TotalReward":100}""";
        await File.AppendAllTextAsync(Journal, line[..30]);
        Assert.Equal(before.EvidenceKey, (await source.ReadAsync(default)).EvidenceKey);
        await File.AppendAllTextAsync(Journal, line[30..] + "\n" + line + "\n");
        var after = await source.ReadAsync(default);
        Assert.Equal(325100, Combat(after).GetProperty("recentCombat").GetProperty("rewardCredits").GetDecimal());
        Assert.Equal(after.Json, (await source.ReadAsync(default)).Json);
    }
    [Fact]
    public async Task CommanderSwitchAndJournalReplacementDiscardPreviousRecords()
    {
        await Fixture(); var source = Source(); await source.ReadAsync(default);
        await Append("""{"timestamp":"2026-11-11T10:00:12Z","event":"Commander","Name":"Different Synthetic Pilot"}""");
        var switched = await source.ReadAsync(default);
        Assert.False(switched.LiveAvailable); Assert.DoesNotContain("python", switched.Json);
        await File.WriteAllTextAsync(Journal, """{"timestamp":"2026-11-11T10:00:12Z","event":"LoadGame","Commander":"New Synthetic Pilot","Ship":"sidewinder"}""" + "\n");
        var replaced = await source.ReadAsync(default);
        Assert.DoesNotContain("anaconda", replaced.Json);
        Assert.True(Root(replaced).GetProperty("skippedOrReplacedData").GetBoolean());
    }
    [Fact]
    public async Task InvalidJournalLineDoesNotLoseLaterEvidence()
    {
        await Fixture(); var source = Source(); await source.ReadAsync(default);
        await Append("{ malformed");
        await Append("""{"timestamp":"2026-11-11T10:00:12Z","event":"HullDamage","Health":0.4,"Fighter":false}""");
        var result = await source.ReadAsync(default);
        Assert.Equal(40, Combat(result).GetProperty("lastReportedHull").GetProperty("percent").GetDouble());
        Assert.True(Root(result).GetProperty("skippedOrReplacedData").GetBoolean());
    }
    [Fact]
    public async Task LiveToolPayloadOmitsOlderSessionTimelines()
    {
        await Fixture(); var source = Source();
        var result = await source.ReadAsync(default);
        var live = JsonDocument.Parse(CombatContextSource.ToolResult(result, CombatQueryKind.Live)).RootElement;
        Assert.False(live.GetProperty("combat").TryGetProperty("previousSessions", out _));
        Assert.Equal(4, live.GetProperty("combat").GetProperty("recentCombat").GetProperty("timeline").GetArrayLength());
        Assert.Equal(result.Json, CombatContextSource.ToolResult(result, CombatQueryKind.Review));
    }
    [Fact]
    public void InvalidHullAndFighterLoadoutDoNotOverwriteMainHull()
    {
        var state = new CombatState();
        Apply(state, """{"timestamp":"2026-11-11T10:00:00Z","event":"LoadGame"}""");
        Apply(state, """{"timestamp":"2026-11-11T10:00:01Z","event":"Loadout","Ship":"anaconda","HullHealth":0.8}""");
        Apply(state, """{"timestamp":"2026-11-11T10:00:02Z","event":"Loadout","Ship":"empire_fighter","HullHealth":0.2}""");
        Apply(state, """{"timestamp":"2026-11-11T10:00:03Z","event":"HullDamage","Health":20,"Fighter":false}""");
        Assert.Equal(80, state.MainHull!.Percent); Assert.Equal(20, state.FighterHull!.Percent);
        Apply(state, """{"timestamp":"2026-11-11T10:00:04Z","event":"RepairAll"}""");
        Assert.Null(state.MainHull);
    }
    [Fact]
    public void CombatSummaryKeepsBoundedTimelineAndDoesNotInventKillsOrCause()
    {
        var summary = new CombatSummary();
        for (var i = 0; i < 80; i++) summary.Apply(Element("""{"timestamp":"2026-11-11T10:00:01Z","event":"HeatDamage"}"""));
        summary.Apply(Element("""{"timestamp":"2026-11-11T10:00:02Z","event":"Bounty"}"""));
        Assert.Equal(60, summary.Timeline.Count); Assert.Equal(80, summary.HeatDamageReports);
        Assert.Equal(0, summary.RewardCredits); Assert.Equal(1, summary.BountyAwards);
        var md = string.Join("\n", summary.Markdown());
        Assert.Contains("amount unknown", md); Assert.Contains("not a total kill count", md);
        Assert.Contains("not a diagnosis", md); Assert.Contains("latest 60", md);
    }
    [Fact]
    public async Task CombatSurvivesMemoryRestartAndAddsSessionMarkdown()
    {
        await Fixture();
        var clock = new TestClock { Now = DateTimeOffset.Parse("2026-11-11T09:59:00Z") };
        var store = Path.Combine(_root, "memory"); var logs = Path.Combine(_root, "logs");
        CompanionMemory Memory() => new(() => _root, store, clock: clock, sessionLogs: new MarkdownSessionLogWriter(logs));
        await Memory().SynchronizeAsync(default);
        var recalled = await Memory().RecallAsync("Review combat", default);
        Assert.Contains("325000", recalled.Json); Assert.DoesNotContain("Synthetic Enemy Never Export", recalled.Json);
        Assert.DoesNotContain("recentCombat", (await Memory().RecallAsync("Where am I?", default)).Json);
        var md = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(logs, "*.md")));
        Assert.Contains("## Combat after-action", md);
        Assert.Contains("325,000 CR", md); Assert.Contains("main-ship hull: 60%", md);
        Assert.DoesNotContain("Synthetic Combat Pilot", md);
    }
    [Fact]
    public async Task EquipmentChangesInvalidateFitAndLiveReplyEvidence()
    {
        await Fixture(); var source = Source(); var before = await source.ReadAsync(default);
        await Append("""{"timestamp":"2026-11-11T10:00:12Z","event":"ModuleSell","Slot":"HugeHardpoint1"}""");
        var changed = await source.ReadAsync(default);
        Assert.Equal(JsonValueKind.Null, Root(changed).GetProperty("loadout").GetProperty("modules").ValueKind);
        Assert.NotEqual(before.EvidenceKey, changed.EvidenceKey);
    }
    [Theory]
    [InlineData(16777225L)] // docked main ship
    [InlineData(16777240L)] // supercruise main ship
    [InlineData(33554440L)] // fighter
    public async Task DockedSupercruiseAndFighterAreNotEligibleForAutomaticCallouts(long flags)
    {
        await Fixture(); await Status(flags: flags);
        Assert.False((await Source().ReadAsync(default)).Callouts!.Eligible);
    }
    [Fact]
    public async Task RepairAndLoadoutClearThresholdWarningEvidence()
    {
        await Fixture(); var source = Source(); await source.ReadAsync(default);
        await Append("""{"timestamp":"2026-11-11T10:00:12Z","event":"Loadout","Ship":"anaconda","HullHealth":1,"ShipID":1}""");
        Assert.Null((await source.ReadAsync(default)).Callouts!.HullPercent);
        await Append("""{"timestamp":"2026-11-11T10:00:12Z","event":"HullDamage","Health":0.2,"Fighter":false,"PlayerPilot":true}""");
        Assert.Equal(20, (await source.ReadAsync(default)).Callouts!.HullPercent);
        await Append("""{"timestamp":"2026-11-11T10:00:12Z","event":"RepairAll"}""");
        Assert.Null((await source.ReadAsync(default)).Callouts!.HullPercent);
    }
    [Fact]
    public async Task ChangedJournalFolderDoesNotReuseAnyPriorObservation()
    {
        await Fixture(); var folder = _root; var source = new CombatContextSource(() => folder, _clock);
        await source.ReadAsync(default);
        folder = Path.Combine(_root, "empty"); Directory.CreateDirectory(folder);
        var unknown = await source.ReadAsync(default);
        Assert.False(unknown.LiveAvailable); Assert.DoesNotContain("python", unknown.Json);
    }
    [Fact]
    public async Task NewSessionArchivesCombatWithoutReusingItsTargetOrHull()
    {
        await Fixture(); var source = Source(); await source.ReadAsync(default);
        await Append("""{"timestamp":"2026-11-11T10:00:12Z","event":"LoadGame","Commander":"Synthetic Combat Pilot","Ship":"anaconda","ShipID":1}""");
        var combat = Combat(await source.ReadAsync(default));
        Assert.Equal(0, combat.GetProperty("recentCombat").GetProperty("bountyAwards").GetInt32());
        Assert.Equal(1, combat.GetProperty("previousSessions")[0].GetProperty("bountyAwards").GetInt32());
        Assert.Equal(JsonValueKind.Null, combat.GetProperty("lastReportedHull").ValueKind);
        Assert.Equal(JsonValueKind.Null, combat.GetProperty("lastTargetScan").ValueKind);
    }
    [Fact]
    public void CombatEventCanStartAMarkedPartialSessionLog()
    {
        var book = new SessionLogbook();
        book.Configure(true, _clock.Now.AddMinutes(-1));
        Assert.True(book.Apply("synthetic-scope", Element("""{"timestamp":"2026-11-11T10:00:12Z","event":"UnderAttack","Target":"You"}"""), null, new Dictionary<long, MissionMemory>(), []));
        var session = Assert.Single(book.Sessions);
        Assert.True(session.Incomplete); Assert.True(session.Combat.HasActivity);
    }
    [Fact]
    public void SnapshotMigrationAllowsPreCombatCheckpoints()
    {
        var snapshot = JournalContextSource.JournalSnapshot.Restore("""{"Facts":{},"System":"Sol","Commander":null,"ShipId":null,"Latest":null,"SessionAt":"2026-11-11T10:00:00Z"}""");
        var combat = JsonSerializer.SerializeToElement(snapshot.CombatView(null, _clock.Now), Json);
        Assert.False(combat.GetProperty("loaded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, combat.GetProperty("cockpit").ValueKind);
    }
    [Fact]
    public void LiveReplyGuardRejectsChangedAgedOrUnpolledEvidenceOnlyForItsTurn()
    {
        var now = _clock.Now; var context = new CombatContext("{}", "same", now, true);
        var guard = new CombatReplyGuard();
        guard.Arm(3, context, CombatQueryKind.Live);
        Assert.True(guard.CanPlay(3, context, now));
        Assert.False(guard.CanPlay(3, context with { EvidenceKey = "new" }, now));
        Assert.False(guard.CanPlay(3, null, now));
        Assert.False(guard.CanPlay(3, context, now.AddSeconds(4)));
        Assert.False(guard.CanPlay(3, context with { ReadAt = now.AddSeconds(13) }, now.AddSeconds(13)));
        Assert.True(guard.CanPlay(4, null, now));
        guard.Arm(3, context, CombatQueryKind.Review);
        Assert.True(guard.CanPlay(3, null, now.AddMinutes(1)));
        guard.Arm(3, context, CombatQueryKind.Live); guard.Clear();
        Assert.True(guard.CanPlay(3, null, now));
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-11T10:00:12Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
