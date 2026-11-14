using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

// Deterministic journal-to-prose projection. No cloud calls, invented achievements or raw conversation.
public sealed class SessionLogbook
{
    public DateTimeOffset? RecordingSince { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset LatestEventAt { get; set; }
    public List<GameplaySession> Sessions { get; set; } = [];
    public Dictionary<string, SessionWatermark> Watermarks { get; set; } = [];

    public bool Configure(bool enabled, DateTimeOffset now)
    {
        if (enabled == Enabled) return false;
        Enabled = enabled;
        if (enabled) RecordingSince = now.ToUniversalTime(); // No import of years of history or periods explicitly disabled.
        else foreach (var session in Sessions.Where(s => s.EndReason is null))
        { session.Incomplete = true; Close(session, "Logging paused; game exit not observed", null); }
        return true;
    }

    public void MarkGap()
    {
        foreach (var session in Sessions.Where(s => s.EndReason is null))
        { session.Incomplete = true; session.Revision++; }
    }

    public bool Apply(string scope, JsonElement e, string? system, IReadOnlyDictionary<long, MissionMemory> missions,
        IEnumerable<MemoryItem> goals, JsonElement? communityBaseline = null)
    {
        var at = MemoryProgress.Time(e).ToUniversalTime();
        if (!Enabled || RecordingSince is null || at < RecordingSince) return false;
        if (at < LatestEventAt) { MarkGap(); return false; }
        var kind = MemoryProgress.Text(e, "event");
        if (kind is null) return false;
        var fingerprint = Hash(e.GetRawText());
        if (!Watermarks.TryGetValue(scope, out var watermark)) Watermarks[scope] = watermark = new();
        if (at < watermark.At || at == watermark.At && watermark.Seen.Contains(fingerprint)) return false;
        if (at > watermark.At) { watermark.At = at; watermark.Seen.Clear(); }
        watermark.Seen.Add(fingerprint);
        LatestEventAt = at;

        var session = Sessions.LastOrDefault(s => s.Scope == scope && s.EndReason is null);
        if (kind == "LoadGame")
        {
            foreach (var active in Sessions.Where(s => s.EndReason is null))
                Close(active, "Next game session observed; previous exit time unknown", null);
            session = Start(scope, at, false, system, goals.Where(g => g.UpdatedAt <= at), missions);
            session.RecordShip(Clean(MemoryProgress.Text(e, "Ship_Localised") ?? MemoryProgress.Text(e, "Ship") ?? "Unknown ship"));
        }
        else if (session is null)
        {
            // Only gameplay evidence can start a partial session, not file headers, chat or menu records.
            if (kind is not ("Location" or "FSDJump" or "CarrierJump" or "Docked" or "Undocked" or "Loadout" or
                "MarketBuy" or "MarketSell" or "MissionAccepted" or "MissionCompleted" or "MaterialCollected" or
                "EngineerCraft" or "ColonisationContribution" or "UnderAttack" or "HullDamage" or "ShieldState" or
                "HeatWarning" or "HeatDamage" or "Bounty" or "FactionKillBond" or "CapShipBond" or "Died" or
                "Interdicted" or "EscapeInterdiction" or "FighterDestroyed" or "PVPKill" or
                "CommunityGoalJoin" or "CommunityGoalReward" or "CommunityGoalDiscard" or "RedeemVoucher" or
                "SellExplorationData" or "MultiSellExplorationData" or "SellOrganicData")) return false;
            session = Start(scope, at, true, system, goals.Where(g => g.UpdatedAt <= at), missions);
        }
        if (session.CommunityGoals.Goals.Count == 0 && communityBaseline is { } baseline)
            session.CommunityGoals.Apply(baseline);
        if (at < session.LastEventAt) return false;
        session.LastEventAt = at;
        session.Revision++;
        session.Combat.Apply(e);
        session.CommunityGoals.Apply(e);
        session.Statistics.Apply(e);
        string? Text(string key) => MemoryProgress.Text(e, key);
        long Count(string key)
        {
            if (MemoryProgress.Number(e, key) is { } n && n >= 0) return n;
            session.Incomplete = true; return 0;
        }
        string Quantity(string key) => MemoryProgress.Number(e, key) is { } n && n >= 0 ? n.ToString(CultureInfo.InvariantCulture) : "unknown";
        string Commodity() => Clean(Text("Type_Localised") ?? Text("Type") ?? "unknown commodity");
        switch (kind)
        {
            case "Location": case "FSDJump": case "CarrierJump":
                if (Text("StarSystem") is { } arrived)
                {
                    session.StartSystem ??= Clean(arrived); session.EndSystem = Clean(arrived);
                    if (!session.Systems.Contains(Clean(arrived)) && session.Systems.Count < 200) session.Systems.Add(Clean(arrived));
                    if (kind != "Location") session.Jumps++;
                }
                if (kind == "Location" && Text("StationName") is { } locationStation &&
                    e.TryGetProperty("Docked", out var docked) && docked.ValueKind == JsonValueKind.True) session.LastStation = Clean(locationStation);
                else session.LastStation = null;
                break;
            case "Docked":
                session.EndSystem = system is null ? session.EndSystem : Clean(system);
                session.LastStation = Clean(Text("StationName") ?? "Unknown station");
                Highlight(session, at, $"Docked at {session.LastStation} in {Clean(system ?? "unknown system")}.");
                break;
            case "Undocked": session.LastStation = null; break;
            case "Loadout":
                if ((Text("Ship_Localised") ?? Text("Ship")) is { } ship) session.RecordShip(Clean(ship));
                break;
            case "MarketBuy":
                session.Bought += Count("Count"); session.TradeSpent += Count("TotalCost");
                Highlight(session, at, $"Bought {Quantity("Count")} t of {Commodity()}{Place(session)}."); break;
            case "MarketSell":
                session.Sold += Count("Count"); session.TradeEarned += Count("TotalSale");
                Highlight(session, at, $"Sold {Quantity("Count")} t of {Commodity()}{Place(session)}."); break;
            case "MissionAccepted": case "MissionCompleted": case "MissionFailed": case "MissionAbandoned": case "CargoDepot":
                if (MemoryProgress.Number(e, "MissionID") is { } id)
                {
                    var old = missions.GetValueOrDefault(id);
                    var name = Clean(Text("LocalisedName") ?? old?.Name ?? Text("Name") ?? $"Mission {id}");
                    var detail = kind switch
                    {
                        "MissionAccepted" => $"Started: {name}",
                        "MissionCompleted" => $"Completed: {name}",
                        "MissionFailed" => $"Failed: {name}",
                        "MissionAbandoned" => $"Abandoned: {name}",
                        _ => $"Progress: {name} — {Quantity("ItemsDelivered")} / {Quantity("TotalItemsToDeliver")} items delivered (reported total)"
                    };
                    Add(session.MissionChanges, $"{at:HH:mm} UTC — {detail}");
                    if (kind == "MissionAccepted") session.MissionsStarted++;
                    if (kind == "MissionCompleted") session.MissionsCompleted++;
                }
                break;
            case "MaterialCollected":
                var material = Clean(Text("Name_Localised") ?? Text("Name") ?? "unknown material");
                if (session.Materials.Count < 200 || session.Materials.ContainsKey(material))
                    session.Materials[material] = session.Materials.GetValueOrDefault(material) + Count("Count");
                break;
            case "EngineerCraft":
                Highlight(session, at, $"Engineering work: {Clean(Text("BlueprintName") ?? "blueprint not recorded")}, level {Quantity("Level")}{Place(session)}."); break;
            case "ColonisationContribution":
                if (e.TryGetProperty("Contributions", out var contributions) && contributions.ValueKind == JsonValueKind.Array)
                    foreach (var item in contributions.EnumerateArray())
                        Highlight(session, at, $"Construction delivery: {MemoryProgress.Number(item, "Amount")?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} t of {Clean(MemoryProgress.Text(item, "Name_Localised") ?? MemoryProgress.Text(item, "Name") ?? "unknown commodity")}{Place(session)}.");
                break;
            case "Died": Highlight(session, at, "Ship or commander loss recorded."); break;
            case "Shutdown": Close(session, "Game shutdown recorded", at); break;
        }
        return true;
    }

    public void RefreshOpen(string scope, IReadOnlyDictionary<long, MissionMemory> missions, IEnumerable<MemoryItem> goals, bool includeClosed = false)
    {
        var session = Sessions.LastOrDefault(s => s.Scope == scope && (includeClosed || s.EndReason is null));
        if (session is null) return;
        session.OpenMissions = missions.Values.Where(m => m.UpdatedAt <= session.LastEventAt).Take(40).Select(MissionText).ToList();
        session.OpenGoals = goals.Where(g => g.Kind == "goal" && g.Status == "active" && (!includeClosed || g.UpdatedAt <= session.LastEventAt)).Take(40).Select(g => Clean(g.Text)).ToList();
    }

    public void RecordGoal(string scope, MemoryItem item, string action, DateTimeOffset now)
    {
        if (!Enabled || item.Kind != "goal") return;
        var session = Sessions.LastOrDefault(s => s.Scope == scope && s.EndReason is null);
        if (session is null) return; // Notes made outside gameplay don't invent a session.
        var label = action switch { "save" => "Started", "complete" => "Completed (you confirmed)", "cancel" => "Cancelled", _ => "Progress / update (you stated)" };
        Add(session.GoalChanges, $"{now.UtcDateTime:HH:mm} UTC — {label}: {Clean(item.Text)}");
        session.OpenGoals.RemoveAll(g => g == Clean(item.Text));
        if (item.Status == "active" && session.OpenGoals.Count < 40) session.OpenGoals.Add(Clean(item.Text));
        session.Revision++;
    }

    public SessionLogAnchor? CaptureAnchor(string scope, DateTimeOffset spokenAt, string? system,
        IReadOnlyDictionary<long, MissionMemory> missions, IEnumerable<MemoryItem> goals, JsonElement? communityBaseline)
    {
        if (!Enabled || RecordingSince is null || spokenAt < RecordingSince) return null;
        var session = Sessions.LastOrDefault(s => s.Scope == scope && s.EndReason is null);
        // Caller has verified a loaded game session. Speech need not wait for the next journal event.
        session ??= Start(scope, spokenAt, true, system, goals, missions);
        if (spokenAt < session.StartedAt) return null;
        if (session.CommunityGoals.Goals.Count == 0 && communityBaseline is { } baseline) session.CommunityGoals.Apply(baseline);
        return new(scope, session.Id, spokenAt);
    }

    public bool RecordIntent(SessionLogAnchor anchor, string transcript)
    {
        if (!Enabled || RecordingSince is null || anchor.SpokenAt < RecordingSince) return false;
        var session = Sessions.SingleOrDefault(s => s.Scope == anchor.Scope && s.Id == anchor.SessionId && s.EndReason is null);
        if (session is null || anchor.SpokenAt < session.StartedAt) return false;
        var changed = session.AddStatedPlans(transcript, anchor.SpokenAt);
        if (changed) session.Revision++;
        return changed;
    }

    public bool RecordPhoto(SessionLogAnchor anchor, SessionPhoto photo)
    {
        if (!Enabled || RecordingSince is null || anchor.SpokenAt < RecordingSince) return false;
        var session = Sessions.SingleOrDefault(s => s.Scope == anchor.Scope && s.Id == anchor.SessionId && s.EndReason is null);
        if (session is null || anchor.SpokenAt < session.StartedAt) return false;
        if (session.Photos.Any(p => p.FileUri == photo.FileUri)) return true;
        if (session.Photos.Count >= 200) return false;
        session.Photos.Add(photo); session.Revision++;
        return true;
    }

    // Watermarks prevent replay duplication; only live or unexported sessions need full projections.
    // Markdown files remain until the user removes them.
    public void Compact()
    {
        Sessions.RemoveAll(s => s.EndReason is not null && s.WrittenRevision == s.Revision);
    }
    public bool IsValid() => Sessions is not null && Watermarks is not null &&
        Watermarks.Values.All(w => w is not null && w.Seen is not null) &&
        Sessions.All(s => s is not null && Regex.IsMatch(s.Id ?? "", "^[A-F0-9]{24}$") &&
            !string.IsNullOrWhiteSpace(s.Scope) && s.Revision > 0 && s.WrittenRevision >= 0 && s.WrittenRevision <= s.Revision &&
            s.StartedAt <= s.LastEventAt && s.Systems is not null && s.ShipsUsed is not null && s.ShipsUsed.All(ship => !string.IsNullOrWhiteSpace(ship)) &&
            s.Highlights is not null && s.MissionChanges is not null &&
            s.GoalChanges is not null && s.OpenGoals is not null && s.OpenMissions is not null && s.Materials is not null &&
            s.Combat is not null && s.Combat.Timeline is not null && s.Statistics is not null && s.StatedPlans is not null &&
            s.Photos is not null && s.Photos.All(p => p is not null && !string.IsNullOrWhiteSpace(p.Caption) &&
                Uri.TryCreate(p.FileUri, UriKind.Absolute, out var u) && u.IsFile && !u.IsUnc) &&
            s.StatedPlans.All(p => p is not null && !string.IsNullOrWhiteSpace(p.Text)) &&
            s.CommunityGoals is not null && s.CommunityGoals.Goals is not null && s.CommunityGoals.Events is not null &&
            s.CommunityGoals.Goals.Values.All(g => g is not null && !string.IsNullOrWhiteSpace(g.Title) &&
                (g.InitialContribution is null) == (g.LatestContribution is null)));
    private GameplaySession Start(string scope, DateTimeOffset at, bool partial, string? system,
        IEnumerable<MemoryItem> goals, IReadOnlyDictionary<long, MissionMemory> missions)
    {
        var session = new GameplaySession { Id = Hash(scope + "|" + at.ToString("O"))[..24], Scope = scope,
            StartedAt = at, LastEventAt = at, Incomplete = partial,
            StartSystem = partial && system is not null ? Clean(system) : null,
            EndSystem = partial && system is not null ? Clean(system) : null,
            OpenGoals = goals.Where(g => g.Kind == "goal" && g.Status == "active").Take(40).Select(g => Clean(g.Text)).ToList(),
            OpenMissions = missions.Values.Where(m => m.UpdatedAt <= at).Take(40).Select(MissionText).ToList() };
        Sessions.Add(session); return session;
    }
    private static void Close(GameplaySession session, string reason, DateTimeOffset? ended)
    { session.EndReason = reason; session.EndedAt = ended; session.Revision++; }
    private static string MissionText(MissionMemory m) => Clean(m.Name) +
        (m.DestinationSystem is { } system ? $" — {Clean(system)}" : "") +
        (m.Delivered is { } count ? $" ({count} / {m.Required?.ToString(CultureInfo.InvariantCulture) ?? "?"} delivered)" : "");
    private static string Place(GameplaySession session) => session.LastStation is { } station ? $" at {station}" : "";
    private static void Highlight(GameplaySession session, DateTimeOffset at, string text) => Add(session.Highlights, $"{at:HH:mm} UTC — {text}");
    private static void Add(List<string> list, string text) { if (list.Count == 300) list.RemoveAt(0); list.Add(text); }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Clean(string text)
    {
        text = Regex.Replace(LocalDiagnosticLog.Sanitize(text), @"[\p{Cc}\p{Cf}\s]+", " ").Trim();
        if (text.Length > 800) text = text[..800];
        // Treat journal/user strings as text, not active Markdown/HTML or external image URLs.
        return Regex.Replace(text, @"([\\`*_{}\[\]<>()#!|])", @"\$1");
    }
}

public sealed record StatedSessionPlan(DateTimeOffset At, string Text);
public sealed class SessionWatermark
{
    public DateTimeOffset At { get; set; }
    public HashSet<string> Seen { get; set; } = [];
}
public sealed class GameplaySession
{
    public string Id { get; set; } = "";
    public string Scope { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastEventAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public bool Incomplete { get; set; }
    public int Revision { get; set; } = 1;
    public int WrittenRevision { get; set; }
    public string? Ship { get; set; }
    public List<string> ShipsUsed { get; set; } = [];
    public void RecordShip(string ship)
    {
        // Preserve the last ship in older checkpoints before recording a later switch.
        foreach (var observed in new[] { Ship, ship }.OfType<string>())
        {
            var name = SessionLogPresentation.ShipName(observed);
            if (!ShipsUsed.Contains(name, StringComparer.OrdinalIgnoreCase) && ShipsUsed.Count < 200) ShipsUsed.Add(name);
        }
        Ship = ship;
    }
    public string? StartSystem { get; set; }
    public string? EndSystem { get; set; }
    public string? LastStation { get; set; }
    public long Jumps { get; set; }
    public long Bought { get; set; }
    public long Sold { get; set; }
    public decimal TradeSpent { get; set; }
    public decimal TradeEarned { get; set; }
    public int MissionsStarted { get; set; }
    public int MissionsCompleted { get; set; }
    public List<string> Systems { get; set; } = [];
    public List<string> Highlights { get; set; } = [];
    public List<string> MissionChanges { get; set; } = [];
    public List<string> GoalChanges { get; set; } = [];
    public List<string> OpenGoals { get; set; } = [];
    public List<string> OpenMissions { get; set; } = [];
    public Dictionary<string, long> Materials { get; set; } = [];
    public CombatSummary Combat { get; set; } = new();
    public SessionCommunityGoals CommunityGoals { get; set; } = new();
    public SessionStatistics Statistics { get; set; } = new();
    public List<StatedSessionPlan> StatedPlans { get; set; } = [];
    public List<SessionPhoto> Photos { get; set; } = [];

    public bool AddStatedPlans(string transcript, DateTimeOffset at)
    {
        var changed = false;
        foreach (var excerpt in SessionIntentPolicy.Extract(transcript))
        {
            var text = SessionLogbook.Clean(excerpt);
            if (StatedPlans.Count >= 24 || StatedPlans.Any(p => string.Equals(p.Text, text, StringComparison.OrdinalIgnoreCase))) continue;
            StatedPlans.Add(new(at.ToUniversalTime(), text)); changed = true;
        }
        return changed;
    }

    public IEnumerable<string> SummaryStatistics()
    {
        static string N(decimal n) => n.ToString("N0", CultureInfo.InvariantCulture);
        var received = TradeEarned + CommunityGoals.RewardCredits + CommunityGoals.RedeemedCombatBonds +
            Statistics.MissionRewards + Statistics.OtherVoucherPayments + Statistics.ExplorationPayments + Statistics.OrganicDataPayments;
        if (received > 0) yield return $"Credits received: {N(received)} CR";
        if (Statistics.ConfirmedPlayerKills > 0) yield return $"Confirmed player kills: {N(Statistics.ConfirmedPlayerKills)}";
        if (Combat.BountyAwards > 0) yield return $"Bounty awards: {N(Combat.BountyAwards)}";
        if (Combat.BondAwards > 0) yield return $"Combat bond awards: {N(Combat.BondAwards)}";
        if (MissionsCompleted > 0) yield return $"Missions completed: {N(MissionsCompleted)}";
        if (CommunityGoals.RewardsClaimed > 0) yield return $"Community-goal rewards claimed: {N(CommunityGoals.RewardsClaimed)}";
        if (Jumps > 0) yield return $"Jumps: {N(Jumps)}";
        if (Sold > 0) yield return $"Cargo sold: {N(Sold)} t";
        if (Materials.Values.Sum() is > 0 and var materials) yield return $"Materials collected: {N(materials)} units";
        if (Combat.Losses > 0) yield return $"Ship/commander losses: {N(Combat.Losses)}";
    }

    public SessionLogFile Render()
    {
        var b = new StringBuilder();
        b.AppendLine($"# Gameplay session — {StartedAt:yyyy-MM-dd HH:mm} UTC").AppendLine();
        b.AppendLine($"- Status: {EndReason ?? "Open — game shutdown not yet observed"}");
        b.AppendLine($"- Recorded from: {StartedAt:yyyy-MM-dd HH:mm:ss} UTC");
        b.AppendLine($"- Last game activity: {LastEventAt:yyyy-MM-dd HH:mm:ss} UTC");
        if (EndedAt is { } ended) b.AppendLine($"- Ended: {ended:yyyy-MM-dd HH:mm:ss} UTC");
        b.AppendLine($"- Recorded span: {Math.Max(0, (LastEventAt - StartedAt).TotalMinutes):0} minutes");
        if (Ship is not null) b.AppendLine($"- Ship: {SessionLogPresentation.ShipName(Ship)}");
        if (ShipsUsed.Count > 1) b.AppendLine($"- Ships used: {string.Join("; ", ShipsUsed)}");
        b.AppendLine().AppendLine("## Summary").AppendLine();
        var statistics = SummaryStatistics().ToArray();
        if (statistics.Length > 0)
        {
            b.AppendLine("### Session stats").AppendLine();
            foreach (var stat in statistics) b.AppendLine("- " + stat);
            b.AppendLine();
        }
        var recap = new List<string>();
        if (StatedPlans.Count > 0) recap.Add("Your stated session plans: " + string.Join("; ", StatedPlans.Take(4).Select(p => "“" + p.Text + "”")) + ".");
        recap.AddRange(CommunityGoals.Summary());
        if (StartSystem is not null) recap.Add(Incomplete ? $"Your first recorded location was {StartSystem}." : $"You started in {StartSystem}.");
        if (Jumps > 0) recap.Add($"You made {Jumps} jump{(Jumps == 1 ? "" : "s")} and visited {Systems.Count} recorded system{(Systems.Count == 1 ? "" : "s")}.");
        if (Bought > 0 || Sold > 0) recap.Add($"You bought {Bought} t and sold {Sold} t of commodities.");
        if (MissionsStarted > 0 || MissionsCompleted > 0) recap.Add($"You accepted {MissionsStarted} mission{(MissionsStarted == 1 ? "" : "s")} and completed {MissionsCompleted}.");
        if (Materials.Count > 0) recap.Add($"You collected {Materials.Values.Sum()} engineering material units across {Materials.Count} types.");
        if (GoalChanges.Count > 0) recap.Add($"You recorded {GoalChanges.Count} update{(GoalChanges.Count == 1 ? "" : "s")} to your personal goals.");
        if (EndSystem is not null) recap.Add($"Your last recorded location was {EndSystem}{(LastStation is null ? "" : $", docked at {LastStation}")}.");
        b.AppendLine(recap.Count == 0 ? "No supported activity highlights have been recorded yet." : string.Join(" ", recap));
        Section(b, "Stated session plans", StatedPlans.Select(p => $"{p.At:HH:mm} UTC — You said: “{p.Text}” (intent, not proof of completion)."));
        Section(b, "Saved goals and missions", GoalChanges.Concat(MissionChanges), "No saved personal-goal or mission changes recorded.");
        Section(b, "Community goals and redeemed bonds", CommunityGoals.Details());
        Section(b, "Highlights", Highlights, "No additional highlights recorded.");
        Section(b, "Photos", Photos.Select(p => $"{p.At:HH:mm:ss} UTC — {p.Caption}" +
            (p.System is null ? "" : $" — {p.System}") + (p.Station is null ? "" : $", {p.Station}") +
            $" — [View photo](<{p.FileUri.Replace("<", "%3C").Replace(">", "%3E")}>)"));
        Section(b, "Combat after-action", Combat.Markdown());
        if (Materials.Count > 0) Section(b, "Materials collected", Materials.OrderBy(p => p.Key).Select(p => $"{p.Key}: +{p.Value} collected (not current inventory)"));
        if (TradeSpent > 0 || TradeEarned > 0)
            Section(b, "Trade credits", [$"Spent: {TradeSpent.ToString("N0", CultureInfo.InvariantCulture)} CR", $"Received: {TradeEarned.ToString("N0", CultureInfo.InvariantCulture)} CR",
                "Recorded market transactions only; not total profit or a complete credit balance."]);
        Section(b, "Systems visited", Systems);
        Section(b, "Pick up next time", OpenGoals.Select(g => "Personal goal: " + g).Concat(OpenMissions.Select(m => "Mission last seen active: " + m)), "No open goals or missions recorded.");
        b.AppendLine().AppendLine("## About this log").AppendLine();
        if (Incomplete) b.AppendLine("Coverage is partial: recording began mid-session, was paused, or some journal history was unreadable.");
        b.AppendLine("Generated locally from Elite journal evidence, explicitly saved goals and short first-person gameplay-plan excerpts from your speech. Requested photos are linked to local PNGs with your captions; no automatic screenshots, image uploads, extra summary API calls, audio or full conversation transcripts. Stated plans and captions are not game facts. Personal goal completion is user-confirmed; travel alone does not prove it.");
        b.AppendLine("Times are UTC. Open-session status is last-known, not proof the game is still running. Lists are bounded to keep long sessions readable. Missing data is not a completed goal.");
        b.AppendLine("Session stats show only positive recorded values. Credits received are supported payouts and sales, not net profit or a full balance change. Combat awards are not a unique ship-kill count; only journal-confirmed player kills are counted as kills.");
        b.AppendLine("This file is automatically refreshed. Keep personal edits in a separate file; completed logs are retained until you remove them.");
        return new(Id, StartedAt, b.ToString());
    }
    private static void Section(StringBuilder b, string title, IEnumerable<string> items, string? empty = null)
    {
        var lines = items.ToArray();
        if (lines.Length == 0 && empty is null) return;
        b.AppendLine().AppendLine("## " + title).AppendLine();
        if (lines.Length == 0) b.AppendLine(empty);
        else foreach (var line in lines) b.AppendLine("- " + line);
    }
}
