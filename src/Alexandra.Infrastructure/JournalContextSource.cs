using System.Text.Json;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

// Read-only. Replay on demand so old ship snapshots cannot silently survive a commander/ship change.
public sealed class JournalContextSource(Func<string> directory) : IGameContextSource
{
    private static readonly HashSet<string> Snapshots =
    [
        "Loadout", "Materials", "Rank", "Progress", "Reputation", "EngineerProgress",
        "CommunityGoal", "Missions", "Cargo", "ShipLocker", "Backpack", "NavRoute",
        "ColonisationConstructionDepot", "CarrierStats", "Market", "Docked"
    ];
    public async Task<GameContext> ReadAsync(CancellationToken cancellationToken)
    {
        var path = directory();
        if (!Directory.Exists(path)) return new(null, "{}", "Elite journal folder not found.");
        var state = new JournalSnapshot();
        var skipped = 0;
        var files = Directory.GetFiles(path, "Journal.*.log").Order(StringComparer.Ordinal).ToArray();
        // Bound startup work; explicitly surface incomplete history.
        var bounded = files.TakeLast(80).ToArray();
        foreach (var file in bounded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    try { state.Apply(line); }
                    catch (JsonException) { skipped++; } // A live trailing partial line is normal.
                }
            }
            catch (IOException) { skipped++; }
        }
        foreach (var name in new[] { "Status.json", "Cargo.json", "NavRoute.json" })
        {
            try
            {
                using var stream = new FileStream(Path.Combine(path, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;
                // Sidecar must belong to the latest commander session, not yesterday's ship.
                if (Timestamp(root) >= state.SessionAt) state.Set(name, root);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return state.Build(files.Length > 80, skipped);
    }

    public sealed class JournalSnapshot
    {
        private readonly Dictionary<string, JsonElement> _facts = [];
        public string? System { get; private set; }
        private string? _commander;
        private long? _shipId;
        private DateTimeOffset? _latest;
        public DateTimeOffset SessionAt { get; private set; } = DateTimeOffset.MaxValue;
        public string Export() => JsonSerializer.Serialize(new SnapshotState(_facts, System, _commander, _shipId, _latest, SessionAt));
        public static JournalSnapshot Restore(string json)
        {
            var saved = JsonSerializer.Deserialize<SnapshotState>(json) ?? throw new JsonException("Missing snapshot.");
            var state = new JournalSnapshot { System = saved.System, _commander = saved.Commander,
                _shipId = saved.ShipId, _latest = saved.Latest, SessionAt = saved.SessionAt };
            foreach (var pair in saved.Facts) state._facts[pair.Key] = pair.Value.Clone();
            return state;
        }
        public sealed record SnapshotState(Dictionary<string, JsonElement> Facts, string? System, string? Commander,
            long? ShipId, DateTimeOffset? Latest, DateTimeOffset SessionAt);
        public void Apply(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return;
            var kind = e.TryGetProperty("event", out var eventName) ? eventName.GetString() : null;
            var at = Timestamp(e);
            if (_latest is not null && at < _latest) return;
            if (kind == "LoadGame")
            {
                var commander = e.TryGetProperty("Commander", out var c) ? c.GetString() : null;
                if (commander != _commander) { _facts.Clear(); System = null; _shipId = null; }
                _commander = commander;
                SessionAt = at;
                var shipId = e.TryGetProperty("ShipID", out var s) ? s.GetInt64() : (long?)null;
                if (shipId != _shipId) _facts.Remove("Loadout");
                _shipId = shipId;
                // Avoid uploading commander identity, FID, credits or raw chat.
                Set("Ship", JsonSerializer.SerializeToElement(new {
                    Type = e.TryGetProperty("Ship", out var ship) ? ship.GetString() : null, ShipId = shipId, timestamp = at
                }));
            }
            if (kind is "Location" or "FSDJump" or "CarrierJump")
            {
                if (e.TryGetProperty("StarSystem", out var system)) System = system.GetString();
                _facts.Remove("Docked");
            }
            if (kind is "Undocked" or "SupercruiseEntry") _facts.Remove("Docked");
            if (kind is "ShipyardSwap" or "ShipyardBuy")
            {
                _facts.Remove("Loadout");
                _facts.Remove("Ship");
            }
            if (kind == "Shutdown") _facts.Remove("Status.json");
            if (kind is "MaterialCollected" or "MaterialDiscarded" or "MaterialTrade" or "EngineerCraft" or "Synthesis")
                _facts.Remove("Materials"); // Unknown is safer than a stale count until the next complete snapshot.
            if (kind is "MissionAccepted" or "MissionCompleted" or "MissionAbandoned" or "MissionFailed")
                _facts.Remove("Missions");
            if (kind == "Loadout")
            {
                _shipId = e.TryGetProperty("ShipID", out var id) ? id.GetInt64() : null;
                _facts.Remove("Ship");
            }
            if (kind is not null && Snapshots.Contains(kind)) Set(kind, e);
            if (at != DateTimeOffset.MinValue) _latest = at;
        }
        public void Set(string name, JsonElement value) => _facts[name] = value.Clone();
        public void Remove(string name) => _facts.Remove(name);
        public GameContext Build(bool limited = false, int skipped = 0) =>
            new(System, JsonSerializer.Serialize(new {
                currentSystem = System, observedAt = _latest, sessionAt = SessionAt,
                historyLimited = limited, skippedRecords = skipped, facts = _facts
            }), _latest is null ? "No readable journal events." : $"Last journal event: {_latest:O}");
    }

    private static DateTimeOffset Timestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTimeOffset(out var at)
            ? at : DateTimeOffset.MinValue;
}

