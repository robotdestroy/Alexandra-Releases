using Alexandra.Core;

namespace Alexandra.Infrastructure;

public sealed partial class CompanionMemory
{
    public async Task<ReminderSnapshot?> ReadRemindersAsync(CancellationToken token)
    {
        if (enabled?.Invoke() == false) return null;
        await _gate.WaitAsync(token);
        try
        {
            await SyncLocked(token);
            if (_document?.CurrentProfile is not { } scope || Current is not { } profile) return null;
            var snapshot = Snapshot(scope);
            return new(SessionScope(scope), snapshot.SessionAt, snapshot.GameLoaded, _clock.GetUtcNow(), profile.Reminders.Items.ToArray());
        }
        finally { _gate.Release(); }
    }
    public async Task<ReminderResult> EditReminderAsync(ReminderEdit edit, string transcript, string expectedScope, CancellationToken token)
    {
        if (enabled?.Invoke() == false) throw new InvalidOperationException("Turn on commander memory in Setup to use saved reminders.");
        ReminderPolicy.Validate(edit, transcript);
        await _gate.WaitAsync(token);
        try
        {
            await SyncLocked(token);
            if (Current is not { } profile || _document?.CurrentProfile is not { } scope || SessionScope(scope) != expectedScope)
                throw new InvalidOperationException("The commander changed. Please repeat your reminder request.");
            var before = profile.Reminders.Items.ToList();
            var result = profile.Reminders.Edit(edit, _clock.GetUtcNow());
            if (!result.Changed) return result;
            try { _dirty = true; await SaveLocked(token); }
            catch { profile.Reminders.Items = before; throw; }
            return result;
        }
        finally { _gate.Release(); }
    }
    public async Task<bool> ClaimReminderAsync(string scope, string id, DateTimeOffset triggeredAt, CancellationToken token)
    {
        if (enabled?.Invoke() == false) return false;
        await _gate.WaitAsync(token);
        try
        {
            await SyncLocked(token);
            if (Current is not { } profile || _document?.CurrentProfile is not { } current || SessionScope(current) != scope) return false;
            var index = profile.Reminders.Items.FindIndex(r => r.Id == id && r.Status == "ready" && r.TriggeredAt == triggeredAt);
            if (index < 0 || !Snapshot(current).GameLoaded) return false;
            var before = profile.Reminders.Items[index];
            profile.Reminders.Items[index] = before with { Status = "notified" };
            // Commit before playback: a restart must never produce repeated alerts.
            try { _dirty = true; await SaveLocked(token); }
            catch { profile.Reminders.Items[index] = before; throw; }
            return true;
        }
        finally { _gate.Release(); }
    }
}
