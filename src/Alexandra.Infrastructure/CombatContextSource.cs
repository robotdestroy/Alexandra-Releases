using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

// A small, in-memory tailer for cockpit questions. No memory-file writes, history search, network or screenshots.
public sealed class CombatContextSource(Func<string> directory, TimeProvider? clock = null,
    IGameSessionSource? gameSession = null) : ICombatContextSource
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly JournalFileCatalog _journalFiles = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Cursor> _cursors = [];
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _seenOrder = [];
    private JournalContextSource.JournalSnapshot _snapshot = new();
    private string? _root;
    private bool _partial;
    private CombatContext? _latest;
    public CombatContext? Latest => Volatile.Read(ref _latest);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CombatContext> ReadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var root = Path.GetFullPath(directory());
            if (!string.Equals(root, _root, StringComparison.OrdinalIgnoreCase)) { Reset(); _root = root; }
            if (!Directory.Exists(root)) { Reset(); return PublishUnknown("Elite journal folder unavailable."); }
            var files = _journalFiles.GetFiles(root).TakeLast(3).ToArray();
            // An imported older part must be replayed before already-seen newer records.
            var newestSeen = Array.FindLastIndex(files, _cursors.ContainsKey);
            if (files.Take(Math.Max(0, newestSeen)).Any(f => !_cursors.ContainsKey(f))) Reset();
            if (files.Length == 0) { Reset(); return PublishUnknown("No Elite journal files found."); }
            foreach (var file in files)
            {
                if (_cursors.TryGetValue(file, out var cursor) &&
                    (new FileInfo(file).Length < cursor.Offset ||
                        cursor.Prefix is not null && await Prefix(file, cursor.PrefixLength, token) != cursor.Prefix))
                { Reset(); _partial = true; break; }
            }
            foreach (var removed in _cursors.Keys.Except(files).ToArray()) _cursors.Remove(removed);
            foreach (var file in files) await Tail(file, token);
            JsonElement? status = null;
            try
            {
                await using var stream = new FileStream(Path.Combine(root, "Status.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) status = doc.RootElement.Clone();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            var now = _clock.GetUtcNow();
            bool? activeSession = gameSession is null ? null : gameSession.StartedAt is { } started && started <= now &&
                _snapshot.SessionAt >= started.AddSeconds(-2) && _snapshot.SessionAt <= now;
            var combat = JsonSerializer.SerializeToElement(_snapshot.CombatView(status, now, activeSession), Json);
            var loadout = _snapshot.CombatFit();
            var available = combat.GetProperty("loaded").GetBoolean() && combat.GetProperty("statusAvailable").GetBoolean() &&
                combat.GetProperty("vehicle").GetString() is "main_ship" or "fighter";
            var evidence = JsonSerializer.Serialize(new {
                root = Hash(root.ToUpperInvariant()), sessionAt = combat.GetProperty("sessionAt"),
                loaded = combat.GetProperty("loaded"), available,
                vehicle = combat.GetProperty("vehicle"), cockpit = combat.GetProperty("cockpit"),
                target = combat.GetProperty("lastTargetScan"), hull = combat.GetProperty("lastReportedHull"),
                lastAttack = combat.GetProperty("lastAttack"), loadout, currentSystem = _snapshot.System
            });
            var callouts = CalloutObservation(combat, Hash(root.ToUpperInvariant() + "|" +
                combat.GetProperty("sessionAt").GetRawText() + "|" + combat.GetProperty("contextAt").GetRawText() +
                "|" + JsonSerializer.Serialize(loadout)), available);
            var continuity = Hash(JsonSerializer.Serialize(new {
                root = Hash(root.ToUpperInvariant()), sessionAt = combat.GetProperty("sessionAt"),
                contextAt = combat.GetProperty("contextAt"), vehicle = combat.GetProperty("vehicle"),
                gameStartedAt = gameSession?.StartedAt, currentSystem = _snapshot.System,
                shipId = _snapshot.ActiveShipId
            }));
            var diagnostic = $"Cockpit available={available}; loaded={combat.GetProperty("loaded")}; game session active={activeSession}; journals={string.Join(",", files.Select(Path.GetFileName))}; session={combat.GetProperty("sessionAt")}";
            var result = new CombatContext(JsonSerializer.Serialize(new {
                combat, loadout, currentSystem = _snapshot.System,
                historyLimited = true, historyNote = "Fast context covers up to three latest journal files; older combat history may be absent. Use commander memory for older session reviews.",
                skippedOrReplacedData = _partial,
                capabilities = new { ownShieldPercentage = false, ownShieldState = "up/down only", liveAmmunition = false },
                responsePolicy = "This is a snapshot at the time of the question. For a single cockpit question, give one short fact. For a sitrep, give two short sentences with the relevant available readings, at most 45 words. If asked for your own shield percentage, including a follow-up like 'a percentage?', say 'I can read shields up or down, but not your shield percentage.' Do not substitute a target's health or repeat shields-up as a percentage answer. No research_elite for missing private telemetry. Target and hull percentages are last reported, not live. Do not diagnose causation from a timeline alone."
            }, Json), Hash(evidence), now, available, callouts, diagnostic, _snapshot.SessionAt,
                continuity, _snapshot.GameLoaded && activeSession != false ? _snapshot.Docking : null);
            Volatile.Write(ref _latest, result); return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return PublishUnknown("Combat records unavailable; do not reuse earlier cockpit data."); }
        finally { _gate.Release(); }
    }
    private async Task Tail(string file, CancellationToken token)
    {
        if (!_cursors.TryGetValue(file, out var cursor)) _cursors[file] = cursor = new();
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
        stream.Position = cursor.Offset;
        var buffer = new byte[65536]; using var line = new MemoryStream();
        var position = cursor.Offset; var tooLong = false;
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
            for (var i = 0; i < count; i++)
            {
                position++;
                if (buffer[i] != 10)
                { if (line.Length < 4_000_000 && !tooLong) line.WriteByte(buffer[i]); else tooLong = true; continue; }
                token.ThrowIfCancellationRequested();
                if (tooLong) _partial = true;
                else
                {
                    var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).Trim('\r', '\uFEFF');
                    var id = Hash(text);
                    if (!_seen.Contains(id))
                    {
                        try { _snapshot.Apply(text); }
                        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException) { _partial = true; }
                        _seen.Add(id); _seenOrder.Enqueue(id);
                        if (_seenOrder.Count > 8192) _seen.Remove(_seenOrder.Dequeue());
                    }
                }
                cursor.Offset = position; line.SetLength(0); tooLong = false;
            }
        if (cursor.Prefix is null || cursor.PrefixLength == 0 && cursor.Offset > 0)
        { cursor.PrefixLength = (int)Math.Min(128, cursor.Offset); cursor.Prefix = await Prefix(file, cursor.PrefixLength, token); }
    }
    private void Reset() { _snapshot = new(); _cursors.Clear(); _seen.Clear(); _seenOrder.Clear(); _partial = false; }
    private static CombatCalloutObservation CalloutObservation(JsonElement combat, string scope, bool available)
    {
        var cockpit = combat.GetProperty("cockpit");
        var attack = combat.GetProperty("lastAttack");
        var hull = combat.GetProperty("lastPilotedMainHullDamage");
        DateTimeOffset? Time(JsonElement item, string field) =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var value) &&
            value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var at) ? at : null;
        var eligible = available && combat.GetProperty("vehicle").GetString() == "main_ship" &&
            CombatState.Bool(cockpit, "docked") == false && CombatState.Bool(cockpit, "supercruise") == false;
        var underAttack = CombatState.Bool(attack, "recent") == true && MemoryProgress.Text(attack, "vehicle") == "You";
        return new(scope, eligible, underAttack || CombatState.Bool(cockpit, "inDanger") == true,
            CombatState.Bool(cockpit, "shieldsUp"), Time(combat, "statusObservedAt") ?? DateTimeOffset.MinValue,
            Time(hull, "at"), CombatState.Percent(hull, "percent"), Time(combat, "lastHeatDamageAt"),
            Time(combat, "lastHeatWarningAt"), CombatState.Bool(cockpit, "overheating"));
    }
    public static string ToolResult(CombatContext context, CombatQueryKind kind)
    {
        if (kind == CombatQueryKind.Review) return context.Json;
        var root = JsonNode.Parse(context.Json)!;
        if (root["combat"] is JsonObject combat)
        {
            combat.Remove("previousSessions");
            if (combat["recentCombat"] is JsonObject summary)
            {
                var last = summary["timeline"]?.AsArray().TakeLast(4).Select(n => n?.DeepClone()).ToArray() ?? [];
                summary["timeline"] = new JsonArray(last);
                summary["timelineNote"] = "Only the latest four observed events; request review for the fuller session timeline.";
            }
        }
        return root.ToJsonString();
    }
    private CombatContext PublishUnknown(string reason)
    {
        var result = new CombatContext(JsonSerializer.Serialize(new { available = false, reason }), "unavailable", _clock.GetUtcNow(), false,
            DiagnosticState: reason);
        Volatile.Write(ref _latest, result); return result;
    }
    private static async Task<string> Prefix(string path, int length, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[length]; var count = await stream.ReadAsync(bytes, token);
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, count)));
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private sealed class Cursor { public long Offset; public int PrefixLength; public string? Prefix; }
}
