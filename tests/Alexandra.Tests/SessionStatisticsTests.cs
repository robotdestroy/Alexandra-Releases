using System.Text.Json;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class SessionStatisticsTests
{
    [Fact]
    public void EmptySessionHasNoStatsAreaOrZeroRows()
    {
        Assert.Empty(new GameplaySession().SummaryStatistics());
        Assert.DoesNotContain("### Session stats", new GameplaySession().Render().Markdown);
    }
    [Fact]
    public void NonzeroStatsPrecedeProseAndDoNotInventKills()
    {
        var session = new GameplaySession { StartSystem = "Example", MissionsCompleted = 2, Jumps = 3,
            TradeEarned = 4000, CommunityGoals = new() { RewardCredits = 5000, RedeemedCombatBonds = 1000 },
            Combat = new() { BondAwards = 7, RewardCredits = 9000 } };
        var md = session.Render().Markdown;
        Assert.Contains("Credits received: 10,000 CR", md); Assert.Contains("Missions completed: 2", md);
        Assert.Contains("Combat bond awards: 7", md);
        Assert.DoesNotContain("Confirmed player kills:", md); Assert.DoesNotContain("Bounty awards:", md);
        Assert.DoesNotContain("Ship/commander losses:", md);
        Assert.True(md.IndexOf("### Session stats", StringComparison.Ordinal) < md.IndexOf("You started", StringComparison.Ordinal));
    }
    [Fact]
    public void IncomeUsesPayoutsNotAwardsBalancesOrBonusesTwice()
    {
        var stats = new SessionStatistics();
        void Apply(string s) { using var d = JsonDocument.Parse(s); stats.Apply(d.RootElement); }
        Apply("""{"event":"MissionCompleted","Reward":1000}""");
        Apply("""{"event":"RedeemVoucher","Type":"Bounty","Amount":200}""");
        Apply("""{"event":"RedeemVoucher","Type":"CombatBond","Amount":900}""");
        Apply("""{"event":"MultiSellExplorationData","TotalEarnings":1500,"BaseValue":1000,"Bonus":500}""");
        Apply("""{"event":"SellOrganicData","BioData":[{"Value":100,"Bonus":400}]}""");
        Apply("""{"event":"Bounty","TotalReward":9000}""");
        Apply("""{"event":"CommunityGoal","Bonus":9000}""");
        Apply("""{"event":"LoadGame","Credits":500000000}""");
        Assert.Equal(3200, stats.MissionRewards + stats.OtherVoucherPayments + stats.ExplorationPayments + stats.OrganicDataPayments);
    }
    [Fact]
    public void MissingNegativeAndUnredeemedAmountsAreNotIncomeAndOnlyPvpEventConfirmsKills()
    {
        var stats = new SessionStatistics();
        foreach (var s in new[] { """{"event":"MissionCompleted","Reward":-1}""",
            """{"event":"SellExplorationData","BaseValue":999,"Bonus":999}""",
            """{"event":"RedeemVoucher","Amount":"unknown"}""",
            """{"event":"FactionKillBond","Reward":500}""", """{"event":"CapShipBond","Reward":500}""" })
        { using var d = JsonDocument.Parse(s); stats.Apply(d.RootElement); }
        Assert.Equal(0, stats.MissionRewards + stats.OtherVoucherPayments + stats.ExplorationPayments);
        Assert.Equal(0, stats.ConfirmedPlayerKills);
        using var kill = JsonDocument.Parse("""{"event":"PVPKill"}"""); stats.Apply(kill.RootElement);
        Assert.Equal(1, stats.ConfirmedPlayerKills);
        Assert.Contains("Confirmed player kills: 1", new GameplaySession { Statistics = stats }.Render().Markdown);
    }
    [Fact]
    public void StatsSurviveCheckpointAndDoNotDoubleOnReplay()
    {
        var book = new SessionLogbook(); book.Configure(true, DateTimeOffset.Parse("2026-11-11T10:00:00Z"));
        using var mission = JsonDocument.Parse("""{"event":"MissionCompleted","timestamp":"2026-11-11T10:01:00Z","MissionID":1,"Reward":500}""");
        book.Apply("synthetic", mission.RootElement, null, new Dictionary<long, MissionMemory>(), []);
        book = JsonSerializer.Deserialize<SessionLogbook>(JsonSerializer.Serialize(book))!;
        Assert.False(book.Apply("synthetic", mission.RootElement, null, new Dictionary<long, MissionMemory>(), []));
        var session = Assert.Single(book.Sessions);
        Assert.Contains("Credits received: 500 CR", session.SummaryStatistics()); Assert.Equal(1, session.MissionsCompleted);
    }
}
