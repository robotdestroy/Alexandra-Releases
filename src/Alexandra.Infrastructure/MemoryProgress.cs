using System.Text.Json;

namespace Alexandra.Infrastructure;

// Deterministic projections from journal evidence. No model can write these balances or mission states.
public sealed class MemoryProgress
{
    public bool MaterialsKnown { get; set; }
    public bool MissionsKnown { get; set; }
    public Dictionary<string, MaterialBalance> Materials { get; set; } = [];
    public Dictionary<long, MissionMemory> Missions { get; set; } = [];
    public Dictionary<long, JsonElement> Construction { get; set; } = [];
    public DateTimeOffset? MaterialsAt { get; set; }
    public DateTimeOffset? MissionsAt { get; set; }
    public List<VisitMemory> Visits { get; set; } = [];
    public List<JournalMilestone> Milestones { get; set; } = [];
    public bool HistoryTrimmed { get; set; }

    public void Apply(JsonElement e, string? system)
    {
        var kind = Text(e, "event");
        var at = Time(e);
        switch (kind)
        {
            case "Location": case "FSDJump": case "CarrierJump":
                if (Text(e, "StarSystem") is { } arrived)
                {
                    var station = Text(e, "StationName");
                    var visit = new VisitMemory(arrived, station, at);
                    if (Visits.LastOrDefault() is not { } previous || previous.System != arrived || previous.Station != station)
                        Visits.Add(visit);
                }
                break;
            case "Docked":
                if (system is not null) Visits.Add(new(system, Text(e, "StationName"), at));
                break;
            case "Materials":
                Materials.Clear();
                MaterialsKnown = new[] { "Raw", "Manufactured", "Encoded" }.All(c => e.TryGetProperty(c, out var a) && a.ValueKind == JsonValueKind.Array);
                foreach (var category in new[] { "Raw", "Manufactured", "Encoded" })
                    if (e.TryGetProperty(category, out var array) && array.ValueKind == JsonValueKind.Array)
                        foreach (var item in array.EnumerateArray())
                            if (Text(item, "Name") is { } name && Number(item, "Count") is { } count && count >= 0)
                                Materials[Key(name)] = new(name, Text(item, "Name_Localised") ?? name, category, Math.Max(0, count));
                            else MaterialsKnown = false;
                MaterialsAt = at;
                break;
            case "MaterialCollected": case "MaterialDiscarded":
                Adjust(Text(e, "Name"), Text(e, "Category"), Number(e, "Count"), kind == "MaterialDiscarded" ? -1 : 1);
                MaterialsAt = at;
                break;
            case "MaterialTrade":
                foreach (var (field, direction) in new[] { ("Paid", -1), ("Received", 1) })
                    if (e.TryGetProperty(field, out var material))
                        Adjust(Text(material, "Material"), Text(material, "Category"), Number(material, "Quantity"), direction);
                    else MaterialsKnown = false;
                MaterialsAt = at;
                break;
            case "Synthesis": case "EngineerCraft":
                var property = kind == "Synthesis" ? "Materials" : "Ingredients";
                if (e.TryGetProperty(property, out var used) && used.ValueKind == JsonValueKind.Array)
                    foreach (var item in used.EnumerateArray()) Adjust(Text(item, "Name"), Text(item, "Category"), Number(item, "Count"), -1);
                else MaterialsKnown = false;
                MaterialsAt = at;
                break;
            case "MaterialDiscovered": break; // Discovery isn't inventory.
            case "EngineerContribution":
                // Some contribution variants omit a usable inventory delta. Don't keep a false balance.
                MaterialsKnown = false; MaterialsAt = at;
                break;
            case "Missions":
                if (!e.TryGetProperty("Active", out var active) || active.ValueKind != JsonValueKind.Array) { MissionsKnown = false; break; }
                var current = new Dictionary<long, MissionMemory>();
                MissionsKnown = true;
                foreach (var item in active.EnumerateArray())
                {
                    if (Number(item, "MissionID") is not { } id) { MissionsKnown = false; continue; }
                    Missions.TryGetValue(id, out var previous);
                    current[id] = Mission(item, previous, at);
                }
                Missions = current; MissionsAt = at;
                break;
            case "MissionAccepted":
                if (Number(e, "MissionID") is { } accepted) Missions[accepted] = Mission(e, null, at);
                MissionsAt = at;
                break;
            case "MissionRedirected":
                if (Number(e, "MissionID") is { } redirected && Missions.TryGetValue(redirected, out var moving))
                    Missions[redirected] = moving with { DestinationSystem = Text(e, "NewDestinationSystem") ?? moving.DestinationSystem,
                        DestinationStation = Text(e, "NewDestinationStation") ?? moving.DestinationStation, UpdatedAt = at };
                MissionsAt = at;
                break;
            case "CargoDepot":
                if (Number(e, "MissionID") is { } delivery && Missions.TryGetValue(delivery, out var mission))
                    Missions[delivery] = mission with { Required = Number(e, "TotalItemsToDeliver") ?? mission.Required,
                        Delivered = Number(e, "ItemsDelivered") ?? mission.Delivered, UpdatedAt = at };
                MissionsAt = at;
                break;
            case "MissionCompleted": case "MissionFailed": case "MissionAbandoned":
                if (Number(e, "MissionID") is { } ended)
                {
                    Missions.TryGetValue(ended, out var old);
                    Milestones.Add(new(kind, old?.Name ?? Text(e, "Name") ?? "Mission", system, at));
                    Missions.Remove(ended);
                }
                MissionsAt = at;
                break;
            case "ColonisationConstructionDepot":
                if (Number(e, "MarketID") is { } market) Construction[market] = e.Clone();
                break;
            case "ColonisationSystemClaim": case "ColonisationSystemClaimRelease":
            case "ColonisationSystemClaimFailed": case "ColonisationBeaconDeployed":
                Milestones.Add(new(kind, Text(e, "StarSystem") ?? system ?? "Unknown system", system, at));
                break;
        }
        if (Visits.Count > 1000) { Visits.RemoveRange(0, Visits.Count - 1000); HistoryTrimmed = true; }
        if (Milestones.Count > 300) { Milestones.RemoveRange(0, Milestones.Count - 300); HistoryTrimmed = true; }
    }

    private void Adjust(string? name, string? category, long? amount, int direction)
    {
        if (name is null || amount is null || amount < 0) { MaterialsKnown = false; return; }
        if (!MaterialsKnown) return; // A delta without a complete baseline does not establish a total.
        Materials.TryGetValue(Key(name), out var prior);
        var next = (prior?.Count ?? 0) + amount.Value * direction;
        if (next < 0) { MaterialsKnown = false; return; }
        Materials[Key(name)] = new(name, prior?.DisplayName ?? name, prior?.Category ?? category ?? "Unknown", next);
    }
    public static string Key(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    public static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static long? Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    public static DateTimeOffset Time(JsonElement e) => Text(e, "timestamp") is { } s && DateTimeOffset.TryParse(s, out var at) ? at : DateTimeOffset.MinValue;
    private static MissionMemory Mission(JsonElement e, MissionMemory? previous, DateTimeOffset at)
    {
        DateTimeOffset? expiry = Text(e, "Expiry") is { } raw && DateTimeOffset.TryParse(raw, out var date) ? date : previous?.ExpiresAt;
        if (Number(e, "Expires") is { } seconds && seconds >= 0 && seconds < 31536000) expiry = at.AddSeconds(seconds);
        return new(Number(e, "MissionID")!.Value, Text(e, "LocalisedName") ?? previous?.Name ?? Text(e, "Name") ?? "Mission",
            Text(e, "DestinationSystem") ?? previous?.DestinationSystem, Text(e, "DestinationStation") ?? previous?.DestinationStation,
            Text(e, "Commodity_Localised") ?? Text(e, "Commodity") ?? previous?.Commodity,
            Number(e, "Count") ?? previous?.Required, previous?.Delivered, expiry, at);
    }
}
public sealed record MaterialBalance(string Name, string DisplayName, string Category, long Count);
public sealed record MissionMemory(long Id, string Name, string? DestinationSystem, string? DestinationStation,
    string? Commodity, long? Required, long? Delivered, DateTimeOffset? ExpiresAt, DateTimeOffset UpdatedAt);
public sealed record VisitMemory(string System, string? Station, DateTimeOffset At);
public sealed record JournalMilestone(string Event, string Detail, string? System, DateTimeOffset At);

