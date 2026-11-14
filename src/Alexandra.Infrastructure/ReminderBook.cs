using System.Text.Json;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

// Stored with the same commander checkpoint and journal cursor. Never trigger from a Location snapshot.
public sealed class ReminderBook
{
    public List<VoiceReminder> Items { get; set; } = [];
    public string? Station { get; set; }
    public string? Body { get; set; }

    public bool IsValid() => Items is not null && Items.Count <= 100 && Items.All(r => r is not null &&
        !string.IsNullOrWhiteSpace(r.Id) && r.Text is { Length: > 1 and <= 180 } && Enum.IsDefined(r.Trigger) &&
        r.Target is not { Length: > 100 } && r.Status is "armed" or "ready" or "notified" && (r.Status != "ready" || r.TriggeredAt is not null)) &&
        Items.Select(r => r.Id).Distinct().Count() == Items.Count;

    public ReminderResult Edit(ReminderEdit edit, DateTimeOffset now)
    {
        if (edit.Action == "cancel")
        {
            var removed = Items.SingleOrDefault(r => r.Id == edit.Id)
                ?? throw new InvalidOperationException("That reminder wasn't found for this commander. Read reminders again.");
            Items.Remove(removed);
            return new(true, "Reminder cancelled.", removed);
        }
        var target = string.IsNullOrWhiteSpace(edit.Target) ? null : edit.Target.Trim();
        var existing = Items.FirstOrDefault(r => r.Status != "notified" && r.Trigger == edit.Trigger &&
            Same(r.Target, target) && Same(r.Text, edit.Text));
        if (existing is not null) return new(false, "That reminder is already saved.", existing);
        if (Items.Count(r => r.Status != "notified") >= 30)
            throw new InvalidOperationException("You have 30 pending reminders. Cancel one before adding another.");
        while (Items.Count >= 100) Items.Remove(Items.First(r => r.Status == "notified"));
        var reminder = new VoiceReminder(Guid.NewGuid().ToString("N")[..12], edit.Trigger!.Value, edit.Text!.Trim(), target, now);
        Items.Add(reminder);
        return new(true, "Reminder saved. I'll say it once when that event occurs and it is quiet and safe to speak.", reminder);
    }

    public void Apply(JsonElement e)
    {
        var kind = MemoryProgress.Text(e, "event");
        var at = MemoryProgress.Time(e);
        var system = MemoryProgress.Text(e, "StarSystem");
        if (kind is "LoadGame" or "Shutdown" or "Died" or "FSDJump" or "CarrierJump" ||
            kind == "StartJump" && MemoryProgress.Text(e, "JumpType") == "Hyperspace")
        { Station = null; Body = null; Rearm(_ => true); }
        else if (kind is "StartJump" or "SupercruiseEntry")
        { Station = null; Body = null; Rearm(r => r.Trigger != ReminderTrigger.SystemArrival); }
        if (kind == "Location")
        {
            Station = e.TryGetProperty("Docked", out var docked) && docked.ValueKind == JsonValueKind.True ? MemoryProgress.Text(e, "StationName") : null;
            Body = MemoryProgress.Text(e, "Body");
            // Location is a startup/respawn snapshot, never evidence of a newly completed arrival.
            Rearm(_ => true);
        }
        if (kind == "Undocked") { Station = null; Rearm(r => r.Trigger == ReminderTrigger.Docked); }
        if (kind == "Liftoff") { Body = null; Rearm(r => r.Trigger == ReminderTrigger.Touchdown); }
        if (kind == "Docked") Station = MemoryProgress.Text(e, "StationName");
        if (kind == "Touchdown") Body = MemoryProgress.Text(e, "Body");
        ReminderTrigger? trigger = kind switch {
            "Docked" => ReminderTrigger.Docked, "FSDJump" or "CarrierJump" => ReminderTrigger.SystemArrival,
            "Touchdown" => ReminderTrigger.Touchdown, _ => null
        };
        if (trigger is null) return;
        // A remotely recalled ship landing without the player aboard is not this trigger.
        if (trigger == ReminderTrigger.Touchdown && (!e.TryGetProperty("PlayerControlled", out var player) || player.ValueKind != JsonValueKind.True)) return;
        var target = trigger == ReminderTrigger.Docked ? Station : trigger == ReminderTrigger.Touchdown ? Body : system;
        for (var i = 0; i < Items.Count; i++)
        {
            var r = Items[i];
            if (r.Status == "armed" && at > r.CreatedAt && r.Trigger == trigger && (r.Target is null || Same(r.Target, target)))
                Items[i] = r with { Status = "ready", TriggeredAt = at };
        }
    }
    public void ResetLiveState() { Station = null; Body = null; Rearm(_ => true); }
    private void Rearm(Func<VoiceReminder, bool> condition)
    {
        for (var i = 0; i < Items.Count; i++)
            if (Items[i].Status == "ready" && condition(Items[i])) Items[i] = Items[i] with { Status = "armed", TriggeredAt = null };
    }
    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
