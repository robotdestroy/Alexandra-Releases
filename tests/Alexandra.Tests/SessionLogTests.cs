using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class SessionLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alex-session-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private string Journals => Path.Combine(_root, "journals");
    private string Store => Path.Combine(_root, "memory");
    private string Logs => Path.Combine(_root, "sessions");
    private string Journal => Path.Combine(Journals, "Journal.2026-11-11.01.log");
    public SessionLogTests() => Directory.CreateDirectory(Journals);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private CompanionMemory Create(Func<bool>? logsEnabled = null, ISessionLogWriter? writer = null, Func<bool>? memoryEnabled = null) =>
        new(() => Journals, Store, memoryEnabled, _clock, writer ?? new MarkdownSessionLogWriter(Logs), logsEnabled);
    private async Task Append(string text) => await File.AppendAllTextAsync(Journal, text + "\n");
    private async Task Fixture() => await File.AppendAllTextAsync(Journal, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gameplay-session.jsonl")));
    private string[] Files() => Directory.Exists(Logs) ? Directory.GetFiles(Logs, "*.md").Order().ToArray() : [];
    private async Task<string> OnlyLog() => await File.ReadAllTextAsync(Assert.Single(Files()));
    private async Task Shutdown() => await Append("""{"timestamp":"2026-11-11T10:20:00Z","event":"Shutdown"}""");
    private async Task Load(string at = "11:01:00", string fid = "SYNTHETIC-LOG-A") => await Append(
        $$"""{"timestamp":"2026-11-11T{{at}}Z","event":"LoadGame","Commander":"Synthetic Log Pilot","FID":"{{fid}}","Ship":"mandalay","ShipID":1}""");

    [Fact]
    public async Task RetainsShipsUsedAcrossChangesAndAppRestarts()
    {
        await File.AppendAllTextAsync(Journal, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "session-ship-switches.jsonl")));
        await Create().SynchronizeAsync(default);
        Assert.Contains("- Ships used: Federal Corvette; Mandalay", await OnlyLog());
        await Append("""{"timestamp":"2026-11-11T10:02:00Z","event":"Loadout","Ship":"python","ShipID":3}""");
        await Create().SynchronizeAsync(default);
        var markdown = await OnlyLog();
        Assert.Contains("- Ship: Python", markdown);
        Assert.Contains("- Ships used: Federal Corvette; Mandalay; Python", markdown);
        Assert.True(SessionLogSearch.Matches(markdown, "Corvette"));
    }

    [Fact]
    public async Task WritesReadableRecapAndMissionProgressWithoutRawIdentity()
    {
        await Fixture(); await Shutdown(); await Create().SynchronizeAsync(default);
        var md = await OnlyLog();
        Assert.Contains("You started in Sol.", md);
        Assert.Contains("You made 1 jump and visited 2 recorded systems.", md);
        Assert.Contains("You bought 20 t and sold 20 t", md);
        Assert.Contains("100,000 CR", md); Assert.Contains("150,000 CR", md);
        Assert.Contains("Started: Deliver supplies", md);
        Assert.Contains("12 / 20 items delivered", md);
        Assert.Contains("Completed: Deliver supplies", md);
        Assert.Contains("Construction delivery: 10 t of Steel", md);
        Assert.Contains("Iron: +3 collected", md);
        Assert.Contains("Game shutdown recorded", md);
        Assert.DoesNotContain("Mission last seen active: Deliver supplies", md);
        Assert.DoesNotContain("Synthetic Log Pilot", md);
        Assert.DoesNotContain("SYNTHETIC-LOG-A", md);
        Assert.DoesNotContain("\"event\"", md);
    }

    [Fact]
    public async Task AppRestartAndDuplicateSyncKeepTheSameFileAndCounts()
    {
        await Fixture(); await Create().SynchronizeAsync(default);
        var path = Assert.Single(Files()); var original = await File.ReadAllTextAsync(path);
        await Create().SynchronizeAsync(default);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        await Shutdown(); await Create().SynchronizeAsync(default);
        Assert.Equal(path, Assert.Single(Files()));
        Assert.Contains("Game shutdown recorded", await OnlyLog());
        Assert.Contains("You bought 20 t", await OnlyLog());
    }

    [Fact]
    public async Task CompletedSessionsWhileAlexandraWasClosedCatchUpSeparately()
    {
        await Create().SynchronizeAsync(default); // Persist activation, even with no journals yet.
        await Fixture(); await Shutdown(); await Load();
        await Append("""{"timestamp":"2026-11-11T11:02:00Z","event":"Location","StarSystem":"Deciat"}""");
        await Append("""{"timestamp":"2026-11-11T11:03:00Z","event":"Shutdown"}""");
        _clock.Now = _clock.Now.AddDays(1);
        await Create().SynchronizeAsync(default);
        Assert.Equal(2, Files().Length);
        Assert.Contains("You started in Deciat.", await File.ReadAllTextAsync(Files()[1]));
    }

    [Fact]
    public async Task FirstEnableDoesNotExportOldHistoryAndNewMidSessionActivityIsPartial()
    {
        await Fixture(); _clock.Now = _clock.Now.AddMinutes(15);
        await Create().SynchronizeAsync(default); Assert.Empty(Files());
        await Append("""{"timestamp":"2026-11-11T10:16:00Z","event":"FSDJump","StarSystem":"Deciat"}""");
        await Create().SynchronizeAsync(default);
        Assert.Contains("Coverage is partial", await OnlyLog());
        Assert.DoesNotContain("Bought 20", await OnlyLog());
    }

    [Fact]
    public async Task GoalStartProgressAndExplicitCompletionAreRecorded()
    {
        await Fixture(); var memory = Create(); await memory.SynchronizeAsync(default);
        _clock.Now = _clock.Now.AddMinutes(13);
        var goal = await memory.EditAsync(new("save", "goal", Text: "upgrade my explorer"), "My goal is to upgrade my explorer", default);
        Assert.Contains("Started: upgrade my explorer", await OnlyLog());
        Assert.Contains("Personal goal: upgrade my explorer", await OnlyLog());
        _clock.Now = _clock.Now.AddMinutes(1);
        await memory.EditAsync(new("update", "goal", goal.Item!.Id, "gathered three iron for my explorer"), "Update my goal: gathered three iron for my explorer", default);
        Assert.Contains("Progress / update", await OnlyLog());
        Assert.DoesNotContain("Personal goal: upgrade my explorer", await OnlyLog());
        await memory.EditAsync(new("complete", "goal", goal.Item.Id), "I finished that goal", default);
        Assert.Contains("Completed (you confirmed): gathered three iron", await OnlyLog());
        Assert.DoesNotContain("Personal goal:", await OnlyLog());
    }

    [Fact]
    public async Task TravelDoesNotCompleteFreeformGoalsAndNewGoalIsCarriedForward()
    {
        await Fixture(); var memory = Create(); await memory.SynchronizeAsync(default);
        _clock.Now = _clock.Now.AddMinutes(13);
        await memory.EditAsync(new("save", "goal", Text: "visit Deciat to upgrade my ship"), "My goal is to visit Deciat to upgrade my ship", default);
        await Append("""{"timestamp":"2026-11-11T10:14:00Z","event":"FSDJump","StarSystem":"Deciat"}""");
        await Shutdown(); await Create().SynchronizeAsync(default);
        var md = await OnlyLog();
        Assert.Contains("Personal goal: visit Deciat to upgrade my ship", md);
        Assert.DoesNotContain("Completed (you confirmed)", md);
    }

    [Fact]
    public async Task MissingShutdownDoesNotInventEndTime()
    {
        await Fixture(); await Load(); await Create().SynchronizeAsync(default);
        Assert.Equal(2, Files().Length);
        var first = await File.ReadAllTextAsync(Files()[0]);
        Assert.Contains("previous exit time unknown", first);
        Assert.DoesNotContain("- Ended:", first);
        Assert.Contains("Last game activity: 2026-11-11 10:12:00 UTC", first);
    }

    [Fact]
    public async Task JournalPartRolloverDoesNotSplitSessionOrLoseEvents()
    {
        await Fixture(); await Create().SynchronizeAsync(default);
        await File.WriteAllTextAsync(Path.Combine(Journals, "Journal.2026-11-11.02.log"), """
            {"timestamp":"2026-11-11T10:13:00Z","event":"Fileheader","part":2}
            {"timestamp":"2026-11-11T10:14:00Z","event":"MarketBuy","Type":"steel","Count":5,"TotalCost":500}
            {"timestamp":"2026-11-11T10:15:00Z","event":"Shutdown"}

            """);
        await Create().SynchronizeAsync(default);
        Assert.Contains("You bought 25 t", await OnlyLog());
        Assert.Contains("Game shutdown recorded", await OnlyLog());
    }

    [Fact]
    public async Task CommanderChangesKeepGoalsAndFilesSeparate()
    {
        await Fixture(); var memory = Create(); await memory.SynchronizeAsync(default);
        _clock.Now = _clock.Now.AddMinutes(13);
        await memory.EditAsync(new("save", "goal", Text: "my private goal"), "My goal is my private goal", default);
        await Load(fid: "SYNTHETIC-LOG-B");
        await Append("""{"timestamp":"2026-11-11T11:02:00Z","event":"Location","StarSystem":"Achenar"}""");
        await memory.SynchronizeAsync(default);
        Assert.Equal(2, Files().Length);
        Assert.DoesNotContain("my private goal", await File.ReadAllTextAsync(Files()[1]));
    }

    [Fact]
    public async Task DisableAndReenablePreserveFilesButDoNotImportDisabledActivity()
    {
        var enabled = false; var memory = Create(() => enabled);
        await Fixture(); await memory.SynchronizeAsync(default); Assert.Empty(Files());
        enabled = true; _clock.Now = _clock.Now.AddMinutes(13); await memory.SynchronizeAsync(default);
        await Append("""{"timestamp":"2026-11-11T10:14:00Z","event":"MarketBuy","Type":"steel","Count":5,"TotalCost":500}""");
        await memory.SynchronizeAsync(default); Assert.Single(Files());
        enabled = false; await memory.SynchronizeAsync(default);
        Assert.Contains("Logging paused", await OnlyLog());
        await Append("""{"timestamp":"2026-11-11T10:15:00Z","event":"MarketBuy","Type":"gold","Count":100,"TotalCost":500}""");
        await memory.SynchronizeAsync(default);
        enabled = true; _clock.Now = _clock.Now.AddMinutes(3); await memory.SynchronizeAsync(default);
        await Append("""{"timestamp":"2026-11-11T10:17:00Z","event":"Docked","StationName":"New port"}""");
        await memory.SynchronizeAsync(default);
        Assert.Equal(2, Files().Length);
        Assert.DoesNotContain("100 t", string.Join("\n", Files().Select(File.ReadAllText)));
    }

    [Fact]
    public async Task LogsWorkWithMemoryAndCloudDisabled()
    {
        await Fixture(); await Create(memoryEnabled: () => false).SynchronizeAsync(default);
        Assert.Contains("You bought 20 t", await OnlyLog()); // No HTTP or voice dependency in this feature.
    }

    [Fact]
    public async Task ExportFailureDoesNotBreakMemoryAndRetriesWithoutLosingSession()
    {
        var writer = new RecoveringWriter(new MarkdownSessionLogWriter(Logs));
        await Fixture(); var memory = Create(writer: writer);
        await memory.SynchronizeAsync(default);
        Assert.NotNull(memory.SessionLogError); Assert.Empty(Files());
        Assert.Equal("Alpha Centauri", (await memory.ReadAsync(default)).System);
        writer.Fail = false; await memory.SynchronizeAsync(default);
        Assert.Null(memory.SessionLogError); Assert.Contains("You bought 20 t", await OnlyLog());
    }

    [Fact]
    public async Task PartialTrailingLineIsNotCountedUntilItIsComplete()
    {
        await Fixture(); var memory = Create(); await memory.SynchronizeAsync(default);
        const string line = """{"timestamp":"2026-11-11T10:14:00Z","event":"MarketBuy","Type":"steel","Count":5,"TotalCost":500}""";
        await File.AppendAllTextAsync(Journal, line[..50]); await memory.SynchronizeAsync(default);
        Assert.Contains("You bought 20 t", await OnlyLog());
        await File.AppendAllTextAsync(Journal, line[50..] + "\n"); await Create().SynchronizeAsync(default);
        Assert.Contains("You bought 25 t", await OnlyLog());
    }

    [Fact]
    public async Task RebuildingMemoryDoesNotDoubleCountPreviouslyLoggedActivity()
    {
        await Fixture(); var memory = Create(); await memory.SynchronizeAsync(default);
        await File.WriteAllTextAsync(Path.Combine(Journals, "Journal.2026-11-10.01.log"), """{"timestamp":"2026-11-10T10:00:00Z","event":"Fileheader","part":1}""" + "\n");
        await memory.SynchronizeAsync(default);
        Assert.Contains("You bought 20 t", await OnlyLog());
        Assert.Contains("Coverage is partial", await OnlyLog());
    }

    [Fact]
    public async Task ClosedLogDoesNotChangeWhenGoalsAreEditedOutsideGameplay()
    {
        await Fixture(); await Shutdown(); var memory = Create(); await memory.SynchronizeAsync(default);
        var before = await OnlyLog(); _clock.Now = _clock.Now.AddHours(1);
        await memory.EditAsync(new("save", "goal", Text: "future expedition"), "My goal is future expedition", default);
        Assert.Equal(before, await OnlyLog());
    }

    [Fact]
    public async Task DeletedCompletedLogStaysDeletedOnOrdinaryRestart()
    {
        await Fixture(); await Shutdown(); await Create().SynchronizeAsync(default);
        File.Delete(Assert.Single(Files())); await Create().SynchronizeAsync(default); Assert.Empty(Files());
    }

    [Fact]
    public async Task IgnoresChatContentAndEscapesMarkdownInSelectedText()
    {
        await Fixture();
        await Append("""{"timestamp":"2026-11-11T10:13:00Z","event":"ReceiveText","Message":"PRIVATE CHAT DO NOT RETAIN"}""");
        await Append("""{"timestamp":"2026-11-11T10:14:00Z","event":"MarketBuy","Type":"![remote](https://example.com/image)\n<script>x</script>","Count":1,"TotalCost":1}""");
        await Create().SynchronizeAsync(default); var md = await OnlyLog();
        Assert.DoesNotContain("PRIVATE CHAT", md); Assert.DoesNotContain("![remote]", md); Assert.DoesNotContain("<script>", md);
        Assert.Contains("\\!\\[remote\\]", md);
    }

    [Fact]
    public async Task WriterRejectsPathTraversalAndReplacesAtomically()
    {
        var writer = new MarkdownSessionLogWriter(Logs);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(new("../outside", _clock.Now, "bad"), default));
        var file = new SessionLogFile(new string('A', 24), _clock.Now, "first");
        await writer.WriteAsync(file, default); await writer.WriteAsync(file with { Markdown = "second" }, default);
        Assert.Equal("second", await OnlyLog()); Assert.Empty(Directory.GetFiles(Logs, "*.tmp"));
    }

    [Fact]
    public async Task MalformedJournalMarksCoveragePartialWithoutCrashing()
    {
        await Fixture(); await Append("{broken}"); await Shutdown(); await Create().SynchronizeAsync(default);
        Assert.Contains("Coverage is partial", await OnlyLog());
    }

    [Fact]
    public async Task UnknownDeliveryQuantityIsNotReportedAsZero()
    {
        await Fixture(); await Append("""{"timestamp":"2026-11-11T10:13:00Z","event":"CargoDepot","MissionID":456}""");
        await Create().SynchronizeAsync(default);
        Assert.Contains("unknown / unknown items delivered", await OnlyLog());
    }

    [Fact]
    public async Task ExistingMemoryCheckpointUpgradesWithoutErasingGoals()
    {
        await Fixture();
        var old = new CompanionMemory(() => Journals, Store, clock: _clock);
        await old.SynchronizeAsync(default);
        await old.EditAsync(new("save", "goal", Text: "outfit my explorer"), "My goal is to outfit my explorer", default);
        _clock.Now = _clock.Now.AddMinutes(13);
        var upgraded = Create(); await upgraded.SynchronizeAsync(default); Assert.Empty(Files());
        await Append("""{"timestamp":"2026-11-11T10:14:00Z","event":"Docked","StationName":"Example Depot"}""");
        await upgraded.SynchronizeAsync(default);
        Assert.Contains("Personal goal: outfit my explorer", await OnlyLog());
        Assert.Contains("outfit my explorer", (await upgraded.ReadAsync(default)).Json);
    }

    [Fact]
    public async Task FailedExportCanBeRecoveredAfterProcessRestart()
    {
        await Fixture(); await Shutdown();
        await Create(writer: new RecoveringWriter(new MarkdownSessionLogWriter(Logs))).SynchronizeAsync(default);
        Assert.Empty(Files()); await Create().SynchronizeAsync(default);
        Assert.Contains("Game shutdown recorded", await OnlyLog());
    }
    [Fact]
    public async Task JournalOrderMigrationPreservesGoalsAndLogsWithoutCountingSessionTwice()
    {
        await Fixture(); var memory = Create(); await memory.SynchronizeAsync(default);
        await memory.EditAsync(new("save", "goal", Text: "outfit my explorer"), "My goal is to outfit my explorer", default);
        var originalLog = Assert.Single(Files());
        var checkpoint = Assert.Single(Directory.GetFiles(Store, "memory-*.json"));
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(checkpoint))!;
        json.AsObject().Remove("journalOrderVersion"); // Existing 0.1.25 checkpoint.
        await File.WriteAllTextAsync(checkpoint, json.ToJsonString());
        var context = await Create().ReadAsync(default);
        Assert.Contains("outfit my explorer", context.Json);
        Assert.Equal(originalLog, Assert.Single(Files()));
        var migrated = await OnlyLog();
        Assert.Contains("You bought 20 t and sold 20 t", migrated);
        Assert.Contains("Personal goal: outfit my explorer", migrated);
        await Create().SynchronizeAsync(default);
        Assert.Equal(migrated, await OnlyLog());
        Assert.Equal(1, System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(checkpoint))!["journalOrderVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task OffsetTimestampsAreDisplayedInUtc()
    {
        await Append("""{"timestamp":"2026-11-11T12:01:00+02:00","event":"LoadGame","Commander":"Synthetic Pilot","FID":"SYNTHETIC-TIME"}""");
        await Append("""{"timestamp":"2026-11-11T12:02:00+02:00","event":"Shutdown"}""");
        await Create().SynchronizeAsync(default);
        Assert.Contains("Recorded from: 2026-11-11 10:01:00 UTC", await OnlyLog());
        Assert.Contains("Ended: 2026-11-11 10:02:00 UTC", await OnlyLog());
    }

    [Fact]
    public async Task CorruptSessionCheckpointIsPreservedWithVisibleError()
    {
        await Fixture(); await Create().SynchronizeAsync(default);
        var path = Assert.Single(Directory.GetFiles(Store, "memory-*.json"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        node["sessionLogs"]!["sessions"] = null;
        var broken = node.ToJsonString(); await File.WriteAllTextAsync(path, broken);
        var restarted = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.SynchronizeAsync(default));
        Assert.NotNull(restarted.SessionLogError);
        Assert.Equal(broken, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CommunityRewardsProgressAndRedemptionsAppearWithoutCountingBonusesOrAwardsAsPayouts()
    {
        await Append(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "community-goal-session.jsonl")));
        await Create().SynchronizeAsync(default);
        var md = await OnlyLog();
        Assert.Contains("collected 5,000,000 CR in community-goal rewards", md);
        Assert.Contains("increased from 800 to 4,000 (+3,200)", md);
        Assert.Contains("redeemed 3,200 CR in combat bonds", md);
        Assert.Contains("3,000 CR in reported amounts", md); // Awards remain separate.
        Assert.DoesNotContain("9,000,000", md); Assert.DoesNotContain("7,000,000", md);
        await Create().SynchronizeAsync(default); Assert.Equal(md, await OnlyLog());
    }

    [Fact]
    public async Task AutomaticPlansPersistWithoutDebugLogsOrLongTermMemoryAndKeepOriginalScope()
    {
        await Load("10:00:01");
        var memory = Create(memoryEnabled: () => false);
        await memory.SynchronizeAsync(default);
        _clock.Now = _clock.Now.AddMinutes(1);
        var anchor = await memory.CaptureSessionLogAnchorAsync(_clock.Now, _clock.Now.AddMinutes(-5), default);
        Assert.NotNull(anchor);
        Assert.True(await memory.RecordSessionIntentAsync(anchor!, "I'm going to do the new community goal combat", default));
        var md = await OnlyLog(); Assert.Contains("Your stated session plans", md);
        Assert.DoesNotContain("Personal goal:", md);
        await Create(memoryEnabled: () => false).SynchronizeAsync(default);
        Assert.Equal(md, await OnlyLog());
        await Append("""{"timestamp":"2026-11-11T10:02:00Z","event":"LoadGame","Commander":"Different Pilot","FID":"SYNTHETIC-OTHER"}""");
        Assert.False(await memory.RecordSessionIntentAsync(anchor!, "I'm mining gold", default));
        Assert.DoesNotContain("mining gold", string.Join("\n", await Task.WhenAll(Files().Select(path => File.ReadAllTextAsync(path)))));
    }

    [Fact]
    public async Task DisabledOrStoppedOrDifferentGameCannotCaptureSessionPlan()
    {
        await Load("10:00:01"); var memory = Create(logsEnabled: () => false);
        _clock.Now = _clock.Now.AddMinutes(1);
        Assert.Null(await memory.CaptureSessionLogAnchorAsync(_clock.Now, _clock.Now.AddMinutes(-5), default));
        var enabled = Create(); await enabled.SynchronizeAsync(default);
        Assert.Null(await enabled.CaptureSessionLogAnchorAsync(_clock.Now, _clock.Now, default));
        await Shutdown(); _clock.Now = _clock.Now.AddHours(1);
        Assert.Null(await enabled.CaptureSessionLogAnchorAsync(_clock.Now, _clock.Now.AddHours(-2), default));
    }

    [Fact]
    public async Task PlanCanStartPartialSessionBeforeNextJournalEventWithCommunityBaseline()
    {
        await Load("09:50:00");
        await Append("""{"timestamp":"2026-11-11T09:51:00Z","event":"CommunityGoal","CurrentGoals":[{"CGID":5,"Title":"Synthetic goal","PlayerContribution":100}]}""");
        var memory = Create(); await memory.SynchronizeAsync(default); Assert.Empty(Files());
        _clock.Now = _clock.Now.AddMinutes(1);
        var anchor = await memory.CaptureSessionLogAnchorAsync(_clock.Now, _clock.Now.AddHours(-1), default);
        Assert.NotNull(anchor);
        Assert.True(await memory.RecordSessionIntentAsync(anchor!, "I'm turning in community goals", default));
        await Append("""{"timestamp":"2026-11-11T10:02:00Z","event":"CommunityGoal","CurrentGoals":[{"CGID":5,"Title":"Synthetic goal","PlayerContribution":250}]}""");
        await memory.SynchronizeAsync(default);
        var md = await OnlyLog(); Assert.Contains("from 100 to 250 (+150)", md);
        Assert.Contains("turning in community goals", md); Assert.Contains("Coverage is partial", md);
    }

    [Fact]
    public void FirstContributionSnapshotAndGlobalCompletionDoNotInventSessionProgress()
    {
        var goals = new SessionCommunityGoals();
        using var entry = JsonDocument.Parse("""{"event":"CommunityGoal","CurrentGoals":[{"CGID":1,"Title":"Example","PlayerContribution":1500,"Bonus":9000,"IsComplete":true}]}""");
        goals.Apply(entry.RootElement); goals.Apply(entry.RootElement);
        Assert.Empty(goals.Summary()); Assert.Equal(0, goals.RewardCredits);
        Assert.Contains("no increase observed", string.Join("\n", goals.Details()));
    }

    [Fact]
    public void ReplayDoesNotDoubleCountCommunityRewardOrBondRedemption()
    {
        var book = new SessionLogbook(); book.Configure(true, DateTimeOffset.Parse("2026-11-11T10:00:00Z"));
        foreach (var json in new[] {
            """{"timestamp":"2026-11-11T10:01:00Z","event":"CommunityGoalReward","CGID":1,"Name":"Example","Reward":5000}""",
            """{"timestamp":"2026-11-11T10:02:00Z","event":"RedeemVoucher","Type":"CombatBond","Amount":800}""" })
        {
            using var entry = JsonDocument.Parse(json);
            Assert.True(book.Apply("synthetic", entry.RootElement, "Sol", new Dictionary<long, MissionMemory>(), []));
            Assert.False(book.Apply("synthetic", entry.RootElement, "Sol", new Dictionary<long, MissionMemory>(), []));
        }
        var goals = Assert.Single(book.Sessions).CommunityGoals;
        Assert.Equal(5000, goals.RewardCredits); Assert.Equal(1, goals.RewardsClaimed);
        Assert.Equal(800, goals.RedeemedCombatBonds);
    }

    [Fact]
    public async Task PreviousCheckpointWithoutNewSessionFieldsLoadsSafely()
    {
        await Load("10:00:01"); await Create().SynchronizeAsync(default);
        var path = Assert.Single(Directory.GetFiles(Store, "memory-*.json"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var session = node["sessionLogs"]!["sessions"]![0]!.AsObject();
        session.Remove("statedPlans"); session.Remove("communityGoals");
        await File.WriteAllTextAsync(path, node.ToJsonString());
        await Create().SynchronizeAsync(default);
        Assert.Single(Files()); Assert.Contains("Gameplay session", await OnlyLog());
    }

    [Fact]
    public void CommunityDecreasesMalformedValuesAndUnknownRewardsAreNotInvented()
    {
        var goals = new SessionCommunityGoals();
        void Apply(string json) => goals.Apply(JsonDocument.Parse(json).RootElement);
        Apply("""{"event":"CommunityGoal","CurrentGoals":[{"CGID":1,"Title":"Example","PlayerContribution":100,"Bonus":9000}]}""");
        Apply("""{"event":"CommunityGoal","CurrentGoals":[{"CGID":1,"Title":"Example","PlayerContribution":10}]}""");
        Apply("""{"event":"CommunityGoalReward","CGID":1,"Name":"Example"}""");
        Apply("""{"event":"RedeemVoucher","Type":"CombatBond","Amount":-1}""");
        Assert.Empty(goals.Summary()); Assert.Equal(0, goals.RewardCredits); Assert.Equal(0, goals.RedeemedCombatBonds);
        Assert.Contains("amount unknown", string.Join("\n", goals.Details()));
        Assert.Contains("total decreased/reset", string.Join("\n", goals.Details()));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-11T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class RecoveringWriter(ISessionLogWriter inner) : ISessionLogWriter
    {
        public bool Fail { get; set; } = true;
        public Task WriteAsync(SessionLogFile file, CancellationToken token) => Fail ? throw new IOException("Synthetic disk error") : inner.WriteAsync(file, token);
    }
}
