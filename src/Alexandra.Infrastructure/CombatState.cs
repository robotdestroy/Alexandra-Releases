using System.Globalization;
using System.Text.Json;

namespace Alexandra.Infrastructure;

public sealed record CombatObservation(DateTimeOffset At, string Event, string Detail);
public sealed record HullObservation(DateTimeOffset At, double Percent, string Vehicle);
public sealed record TargetObservation(DateTimeOffset At, bool Locked, string? Ship, string? Rank, int ScanStage,
    string? LegalStatus, double? ShieldPercent, double? HullPercent, string? Subsystem, double? SubsystemPercent);

// Only allowlisted combat facts are retained; no pilot names, player identifiers, chat or raw journal records.
public sealed class CombatSummary
{
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? LastAt { get; set; }
    public int ShieldDownReports { get; set; }
    public int HeatDamageReports { get; set; }
    public int BountyAwards { get; set; }
    public int BondAwards { get; set; }
    public decimal RewardCredits { get; set; }
    public int Losses { get; set; }
    public int Escapes { get; set; }
    public double? LowestReportedMainHull { get; set; }
    public double? LowestReportedFighterHull { get; set; }
    public List<CombatObservation> Timeline { get; set; } = [];
    public bool Trimmed { get; set; }
    public bool HasActivity => Timeline.Count > 0;
    public void Apply(JsonElement e)
    {
        var at = MemoryProgress.Time(e).ToUniversalTime();
        if (at == DateTimeOffset.MinValue) return;
        var kind = MemoryProgress.Text(e, "event");
        string? detail = null;
        switch (kind)
        {
            case "UnderAttack":
                detail = MemoryProgress.Text(e, "Target") switch {
                    "You" => "Incoming fire reported against the piloted vehicle.",
                    "Fighter" => "Incoming fire reported against the fighter.",
                    "Mothership" => "Incoming fire reported against the mothership.", _ => "Incoming fire reported; affected vehicle unknown." };
                break;
            case "ShieldState":
                var up = CombatState.Bool(e, "ShieldsUp");
                if (up is null) break;
                if (!up.Value) ShieldDownReports++;
                detail = up.Value ? "Shields restored (vehicle not specified by this event)." : "Shields down (vehicle not specified by this event).";
                break;
            case "HullDamage":
                if (CombatState.Percent(e, "Health", fraction: true) is { } hull)
                {
                    var fighter = CombatState.Bool(e, "Fighter");
                    var piloting = CombatState.Bool(e, "PlayerPilot");
                    if (fighter == false) LowestReportedMainHull = Math.Min(LowestReportedMainHull ?? 100, hull);
                    if (fighter == true) LowestReportedFighterHull = Math.Min(LowestReportedFighterHull ?? 100, hull);
                    detail = $"{(fighter == true ? "Fighter" : fighter == false ? "Main ship" : "Unidentified vehicle")} hull reported at {hull.ToString("0.#", CultureInfo.InvariantCulture)}%{(piloting == true ? " while you were piloting it" : "")}. This is a damage-threshold observation.";
                }
                break;
            case "HeatWarning": detail = "Overheating warning recorded."; break;
            case "HeatDamage": HeatDamageReports++; detail = "Heat damage recorded."; break;
            case "Bounty": case "FactionKillBond": case "CapShipBond":
                var reward = MemoryProgress.Number(e, kind == "Bounty" ? "TotalReward" : "Reward") ??
                    (kind == "Bounty" ? MemoryProgress.Number(e, "Reward") : null);
                if (kind == "Bounty") BountyAwards++; else BondAwards++;
                if (reward is >= 0) RewardCredits += reward.Value;
                detail = $"{(kind == "Bounty" ? "Bounty" : "Combat bond")} award recorded" +
                    (reward is >= 0 ? $": {reward.Value.ToString("N0", CultureInfo.InvariantCulture)} CR (awarded, not necessarily redeemed)." : "; amount unknown.");
                break;
            case "PVPKill": detail = "Player kill confirmed by the journal (identity omitted)."; break;
            case "Interdicted": detail = "Interdiction recorded" + (CombatState.Bool(e, "Submitted") == true ? "; you submitted." : "."); break;
            case "EscapeInterdiction": Escapes++; detail = "Escaped an interdiction."; break;
            case "FighterDestroyed": detail = "Ship-launched fighter destroyed."; break;
            case "Died": Losses++; detail = "Ship or commander loss recorded; cause is not inferred."; break;
        }
        if (detail is null || at == DateTimeOffset.MinValue) return;
        StartedAt ??= at; LastAt = at;
        Timeline.Add(new(at, kind!, detail));
        if (Timeline.Count > 60) { Timeline.RemoveAt(0); Trimmed = true; }
    }
    public IEnumerable<string> Markdown()
    {
        if (!HasActivity) yield break;
        yield return $"{ShieldDownReports} shield-down reports; {HeatDamageReports} heat-damage reports; {Losses} ship/commander losses; {Escapes} interdiction escapes.";
        if (LowestReportedMainHull is { } hull) yield return $"Lowest reported main-ship hull: {hull.ToString("0.#", CultureInfo.InvariantCulture)}% (threshold reading, not continuous telemetry).";
        if (LowestReportedFighterHull is { } fighter) yield return $"Lowest reported fighter hull: {fighter.ToString("0.#", CultureInfo.InvariantCulture)}%.";
        if (BountyAwards + BondAwards > 0) yield return $"{BountyAwards} bounty awards and {BondAwards} bond awards; {RewardCredits.ToString("N0", CultureInfo.InvariantCulture)} CR in reported amounts. Awards are not a total kill count or proof of redemption.";
        foreach (var entry in Timeline) yield return $"{entry.At:HH:mm:ss} UTC — {entry.Detail}";
        yield return "This is an observed event sequence, not a diagnosis of what caused damage or a loss. " +
            (Trimmed ? "Only the latest 60 combat events are shown." : "Events are grouped by gameplay session, not inferred individual fights.");
    }
}

public sealed class CombatState
{
    public static bool IsRelevantQuestion(string question) => new[] {
        "combat", "fight", "battle", "shield", "hull", "heat", "died", "death", "bounty", "bond",
        "interdict", "attack", "target", "damage", "pips"
    }.Any(term => question.Contains(term, StringComparison.OrdinalIgnoreCase));
    public bool Loaded { get; set; }
    public DateTimeOffset SessionAt { get; set; }
    public DateTimeOffset? LocationAt { get; set; }
    public TargetObservation? Target { get; set; }
    public HullObservation? MainHull { get; set; }
    public HullObservation? FighterHull { get; set; }
    public HullObservation? LastPilotedMainHullDamage { get; set; }
    public DateTimeOffset? LastHeatDamageAt { get; set; }
    public DateTimeOffset? AttackedAt { get; set; }
    public string? AttackedVehicle { get; set; }
    public CombatSummary Current { get; set; } = new();
    public List<CombatSummary> PreviousSessions { get; set; } = [];

    public void Apply(JsonElement e)
    {
        var kind = MemoryProgress.Text(e, "event");
        var at = MemoryProgress.Time(e).ToUniversalTime();
        if (at == DateTimeOffset.MinValue) return;
        if (kind == "LoadGame")
        {
            Archive(); Loaded = true; SessionAt = at; LocationAt = at;
            Target = null; MainHull = null; FighterHull = null; AttackedAt = null; AttackedVehicle = null;
            LastPilotedMainHullDamage = null; LastHeatDamageAt = null;
        }
        if (kind is "Shutdown" or "Died" or "ShipyardSwap" or "ShipyardBuy" or "Resurrect")
        { Target = null; MainHull = null; FighterHull = null; AttackedAt = null; AttackedVehicle = null; LocationAt = at;
            LastPilotedMainHullDamage = null; LastHeatDamageAt = null; }
        if (kind is "Shutdown" or "Died") Loaded = false;
        if (kind is "FSDJump" or "CarrierJump" or "SupercruiseEntry" or "SupercruiseExit" or "Docked" or "Undocked" or "Location")
        { Target = null; AttackedAt = null; LocationAt = at; LastPilotedMainHullDamage = null; LastHeatDamageAt = null; }
        if (kind is "LaunchFighter" or "DockFighter" or "FighterDestroyed" or "VehicleSwitch")
        { Target = null; FighterHull = null; AttackedAt = null; LocationAt = at; LastPilotedMainHullDamage = null; LastHeatDamageAt = null; }
        if (kind is "Repair" or "RepairAll" or "RepairDrone") { MainHull = null; LastPilotedMainHullDamage = null; }
        if (kind == "Loadout" && Percent(e, "HullHealth", true) is { } hull && Text(e, "Ship") is { } shipType)
        {
            if (shipType.Contains("fighter", StringComparison.OrdinalIgnoreCase)) FighterHull = new(at, hull, "fighter");
            else if (shipType is not ("testbuggy" or "combat_srv"))
            { MainHull = new(at, hull, "main_ship"); LastPilotedMainHullDamage = null; }
        }
        if (kind == "HullDamage" && Percent(e, "Health", true) is { } damaged)
        {
            if (Bool(e, "Fighter") == true) FighterHull = new(at, damaged, "fighter");
            if (Bool(e, "Fighter") == false) MainHull = new(at, damaged, "main_ship");
            if (Bool(e, "Fighter") == false && Bool(e, "PlayerPilot") == true)
                LastPilotedMainHullDamage = new(at, damaged, "main_ship");
        }
        if (kind == "HeatDamage") LastHeatDamageAt = at;
        if (kind == "UnderAttack")
        {
            AttackedAt = at;
            AttackedVehicle = MemoryProgress.Text(e, "Target") is "You" or "Fighter" or "Mothership" ? MemoryProgress.Text(e, "Target") : "unknown";
        }
        if (kind is "ShipTargeted" or "ShipTargetted")
        {
            var locked = Bool(e, "TargetLocked");
            var stage = (int)Math.Clamp(MemoryProgress.Number(e, "ScanStage") ?? 0, 0, 3);
            Target = locked is null ? null : new(at, locked.Value,
                locked.Value ? Text(e, "Ship_Localised") ?? Text(e, "Ship") : null,
                locked.Value && stage >= 1 ? Text(e, "PilotRank") : null, stage,
                locked.Value && stage >= 3 ? Text(e, "LegalStatus") : null,
                locked.Value && stage >= 2 ? Percent(e, "ShieldHealth") : null,
                locked.Value && stage >= 2 ? Percent(e, "HullHealth") : null,
                locked.Value && stage >= 3 ? Text(e, "Subsystem") ?? Text(e, "SubSystem") : null,
                locked.Value && stage >= 3 ? Percent(e, "SubsystemHealth") ?? Percent(e, "SubSystemHealth") : null);
        }
        Current.Apply(e);
    }
    private void Archive()
    {
        if (Current.HasActivity) { PreviousSessions.Add(Current); if (PreviousSessions.Count > 3) PreviousSessions.RemoveAt(0); }
        Current = new();
    }
    public object View(JsonElement? status, DateTimeOffset now)
    {
        var at = status is { } s ? MemoryProgress.Time(s) : DateTimeOffset.MinValue;
        var flags = status is { } st ? MemoryProgress.Number(st, "Flags") : null;
        var flags2 = status is { } st2 ? MemoryProgress.Number(st2, "Flags2") ?? 0 : 0;
        var usable = Loaded && at >= (LocationAt ?? SessionAt) && at <= now && flags is >= 0 && flags2 >= 0;
        var fresh = usable && now - at <= TimeSpan.FromSeconds(15);
        if (!usable) { flags = null; flags2 = 0; }
        bool? Flag(int bit) => flags is { } f ? (f & (1L << bit)) != 0 : null;
        var vehicle = (flags2 & 1) != 0 ? "on_foot" : Flag(26) == true ? "srv" : Flag(25) == true ? "fighter" : Flag(24) == true ? "main_ship" : "unknown";
        var ship = vehicle is "main_ship" or "fighter";
        int[]? pips = null;
        if (usable && ship && status!.Value.TryGetProperty("Pips", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 3)
        {
            var values = p.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n) ? n : -1).ToArray();
            if (values.All(x => x is >= 0 and <= 8) && values.Sum() == 12) pips = values;
        }
        var target = Loaded && ship && Target is { } t && t.At <= now && t.At >= (LocationAt ?? SessionAt) ? Target : null;
        return new {
            loaded = Loaded, sessionAt = SessionAt, contextAt = LocationAt, vehicle, statusObservedAt = usable ? at : (DateTimeOffset?)null,
            statusFresh = fresh, statusAgeSeconds = usable ? Math.Max(0, (now - at).TotalSeconds) : (double?)null,
            cockpit = !usable || !ship ? null : new {
                shieldsUp = Flag(3), hardpointsDeployed = Flag(6), flightAssistOff = Flag(5), analysisMode = Flag(27),
                docked = Flag(0), supercruise = Flag(4),
                landingGearDown = Flag(2), cargoScoopDeployed = Flag(9), silentRunning = Flag(10),
                overheating = Flag(20), inDanger = Flag(22), beingInterdicted = Flag(23),
                fsdMassLocked = Flag(16), fsdCharging = Flag(17), fsdCooldown = Flag(18),
                pips = pips is null ? null : new { systems = pips[0] / 2d, engines = pips[1] / 2d, weapons = pips[2] / 2d },
                fireGroupIndex = MemoryProgress.Number(status!.Value, "FireGroup")
            },
            lastTargetScan = target,
            targetScanRecent = target is not null && now >= target.At && now - target.At <= TimeSpan.FromSeconds(15),
            lastReportedHull = !Loaded ? null : vehicle == "main_ship" ? MainHull : vehicle == "fighter" ? FighterHull : null,
            lastPilotedMainHullDamage = !Loaded ? null : LastPilotedMainHullDamage,
            lastHeatDamageAt = !Loaded ? null : LastHeatDamageAt,
            lastAttack = !Loaded || AttackedAt is null ? null : new { at = AttackedAt, vehicle = AttackedVehicle,
                recent = now >= AttackedAt && now - AttackedAt <= TimeSpan.FromSeconds(10) },
            recentCombat = Current, previousSessions = PreviousSessions.TakeLast(2),
            limits = "Status is updated by the game on changes, not a heartbeat. statusFresh means an observation within 15 seconds, not a live server guarantee. Hull/target values are dated observations, NOT continuously measured percentages. No live ammo, weapon capacitor charge, enemy positions, exact range, damage-per-second or cause of damage is known. Awards are not a complete kill count. Only the app's optional fixed callout policy may speak proactively; no game controls."
        };
    }
    public static bool? Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    public static double? Percent(JsonElement e, string name, bool fraction = false) => e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) &&
        double.IsFinite(n) && n >= 0 && n <= (fraction ? 1 : 100) ? n * (fraction ? 100 : 1) : null;
    public static string? Text(JsonElement e, string name) => MemoryProgress.Text(e, name) is { } t ? LocalDiagnosticLog.Sanitize(t.Length > 160 ? t[..160] : t) : null;
}
