using System.Text.Json;

namespace Alexandra.Infrastructure;

// Extra confirmed payments not already tracked by trade/CG projections. Awards are not cash.
public sealed class SessionStatistics
{
    public decimal MissionRewards { get; set; }
    public decimal OtherVoucherPayments { get; set; }
    public decimal ExplorationPayments { get; set; }
    public decimal OrganicDataPayments { get; set; }
    public long ConfirmedPlayerKills { get; set; }

    public void Apply(JsonElement e)
    {
        static decimal Amount(JsonElement e, string key) => MemoryProgress.Number(e, key) is > 0 and var n ? n : 0;
        switch (MemoryProgress.Text(e, "event"))
        {
            case "MissionCompleted": MissionRewards += Amount(e, "Reward"); break;
            case "RedeemVoucher":
                if (!string.Equals(MemoryProgress.Text(e, "Type"), "CombatBond", StringComparison.OrdinalIgnoreCase))
                    OtherVoucherPayments += Amount(e, "Amount");
                break;
            case "SellExplorationData": case "MultiSellExplorationData":
                // Total includes applicable bonuses. Never add BaseValue/Bonus to it again.
                ExplorationPayments += Amount(e, "TotalEarnings"); break;
            case "SellOrganicData":
                if (e.TryGetProperty("BioData", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var item in data.EnumerateArray())
                        OrganicDataPayments += Amount(item, "Value") + Amount(item, "Bonus");
                break;
            // Bounty/FactionKillBond/CapShipBond can represent shared participation, not a unique kill.
            case "PVPKill": ConfirmedPlayerKills++; break;
        }
    }
}
