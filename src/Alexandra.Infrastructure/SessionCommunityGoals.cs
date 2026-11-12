using System.Globalization;
using System.Text.Json;

namespace Alexandra.Infrastructure;

// Contribution snapshots are baselines/totals, never additive deliveries. Bonus is NOT a payout.
public sealed class SessionCommunityGoals
{
    public Dictionary<long, SessionCommunityGoal> Goals { get; set; } = [];
    public List<string> Events { get; set; } = [];
    public decimal RewardCredits { get; set; }
    public decimal RedeemedCombatBonds { get; set; }
    public int RewardsClaimed { get; set; }

    public void Apply(JsonElement e)
    {
        var at = MemoryProgress.Time(e).ToUniversalTime();
        var kind = MemoryProgress.Text(e, "event");
        if (kind == "CommunityGoal" && e.TryGetProperty("CurrentGoals", out var goals) && goals.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in goals.EnumerateArray())
            {
                if (Get(entry, "Title") is not { } goal) continue;
                if (MemoryProgress.Number(entry, "PlayerContribution") is { } count && count >= 0)
                {
                    goal.InitialContribution ??= count;
                    if (goal.LatestContribution is { } last && count < last) goal.Decreased = true;
                    goal.LatestContribution = count;
                }
                goal.ObservedAt = at;
            }
        }
        else if (kind is "CommunityGoalReward" or "CommunityGoalJoin" or "CommunityGoalDiscard")
        {
            if (Get(e, "Name") is not { } goal) return;
            var action = kind == "CommunityGoalJoin" ? "Joined" : kind == "CommunityGoalDiscard" ? "Opted out of" : "Claimed reward for";
            var reward = kind == "CommunityGoalReward" ? MemoryProgress.Number(e, "Reward") : null;
            if (kind == "CommunityGoalReward")
            {
                RewardsClaimed++;
                if (reward is >= 0) { RewardCredits += reward.Value; goal.RewardCredits += reward.Value; }
            }
            Add($"{at:HH:mm} UTC — {action}: {goal.Title}" + (kind == "CommunityGoalReward" ?
                reward is >= 0 ? $" — {N(reward.Value)} CR received." : " — amount unknown." : "."));
        }
        else if (kind == "RedeemVoucher" && string.Equals(MemoryProgress.Text(e, "Type"), "CombatBond", StringComparison.OrdinalIgnoreCase))
        {
            var amount = MemoryProgress.Number(e, "Amount");
            if (amount is >= 0) RedeemedCombatBonds += amount.Value;
            var faction = MemoryProgress.Text(e, "Faction");
            Add($"{at:HH:mm} UTC — Redeemed " + (amount is >= 0 ? $"{N(amount.Value)} CR in combat bonds" : "combat bonds (amount unknown)") +
                (faction is null ? "." : $" for {SessionLogbook.Clean(faction)}."));
        }
    }

    public IEnumerable<string> Summary()
    {
        if (RewardCredits > 0) yield return $"You collected {N(RewardCredits)} CR in community-goal rewards.";
        foreach (var goal in Goals.Values.Where(g => !g.Decreased && g.InitialContribution is { } first && g.LatestContribution > first).Take(4))
            yield return $"Your recorded contribution to {goal.Title} increased from {N(goal.InitialContribution!.Value)} to {N(goal.LatestContribution!.Value)} (+{N(goal.LatestContribution.Value - goal.InitialContribution.Value)}).";
        if (RedeemedCombatBonds > 0) yield return $"You redeemed {N(RedeemedCombatBonds)} CR in combat bonds.";
    }
    public IEnumerable<string> Details()
    {
        foreach (var entry in Events) yield return entry;
        foreach (var goal in Goals.Values.Where(g => g.LatestContribution is not null))
        {
            var change = goal.Decreased ? "; total decreased/reset — session increase unknown" :
                goal.LatestContribution == goal.InitialContribution ? "; no increase observed" : $"; +{N(goal.LatestContribution!.Value - goal.InitialContribution!.Value)} observed";
            yield return $"{goal.Title}: contribution {N(goal.InitialContribution!.Value)} → {N(goal.LatestContribution!.Value)}{change}.";
        }
        if (Goals.Count > 0 || Events.Count > 0)
            yield return "Contribution totals are snapshots, not necessarily credits or completed personal goals. Only reward/redemption events count as received credits; listed bonuses do not.";
    }
    private SessionCommunityGoal? Get(JsonElement e, string titleField)
    {
        if (MemoryProgress.Number(e, "CGID") is not { } id || id < 0) return null;
        if (!Goals.TryGetValue(id, out var goal))
        { if (Goals.Count >= 40) return null; Goals[id] = goal = new() { Title = $"Community goal {id}" }; }
        if (MemoryProgress.Text(e, titleField) is { } title) goal.Title = SessionLogbook.Clean(title);
        return goal;
    }
    private void Add(string text) { if (Events.Count == 100) Events.RemoveAt(0); Events.Add(text); }
    private static string N(decimal number) => number.ToString("N0", CultureInfo.InvariantCulture);
}
public sealed class SessionCommunityGoal
{
    public string Title { get; set; } = "Community goal";
    public long? InitialContribution { get; set; }
    public long? LatestContribution { get; set; }
    public bool Decreased { get; set; }
    public decimal RewardCredits { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}
