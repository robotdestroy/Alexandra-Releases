using System.IO;
using System.Text.Json;
using Alexandra.Core;

namespace Alexandra.App;

public partial class MainWindow
{
    private static readonly JsonSerializerOptions ReminderJson = new(JsonSerializerDefaults.Web) {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly IReminderMemory _reminderMemory;
    private readonly IStreamingAudioPlayer _reminderAudio = new StreamingAudioPlayer();
    private readonly ReminderPlayer _reminders;
    private ReminderSnapshot? _reminderSnapshot;

    private void CheckReminder()
    {
        bool Allowed() => QuietForCallout() && !_callouts.IsActive && _settings.Value.MemoryEnabled && _settings.Value.AllowCloud;
        if (!Allowed()) { _reminders.Cancel(); return; }
        if (_reminders.IsActive || _reminderSnapshot is not { } snapshot) return;
        var reminder = snapshot.Items.FirstOrDefault(r => r.Status == "ready");
        if (reminder is null) return;
        var voice = _settings.Value.Voice;
        var gameStartedAt = _gameSession.StartedAt; // Subsequent fresh combat polls reverify the process; don't enumerate it for every audio frame.
        var claimed = false;
        bool Relevant() => Allowed() && _settings.Value.Voice == voice && _reminderSnapshot?.Scope == snapshot.Scope &&
            ReminderPolicy.CanAnnounce(_reminderSnapshot, reminder, _combat.Latest, gameStartedAt, DateTimeOffset.UtcNow, claimed);
        if (!Relevant()) return;
        try
        {
            _ = _reminders.TryPlayAsync(reminder, voice, _settings.ReadKey(), Relevant, async token => {
                if (!Relevant()) return false;
                claimed = await Task.Run(() => _reminderMemory.ClaimReminderAsync(snapshot.Scope, reminder.Id, reminder.TriggeredAt!.Value, token), token);
                if (claimed) _log.Write("reminder.notified", "A requested reminder was consumed for one-time playback.");
                return claimed;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _reminders.Cancel(); }
    }

    private async Task<string> RunReminderToolAsync(VoiceToolCall call, CancellationToken token)
    {
        var voice = _voice;
        var snapshot = await _reminderMemory.ReadRemindersAsync(token)
            ?? throw new InvalidOperationException("Enable commander memory and load your commander in Elite to use reminders.");
        if (call.Name == "read_reminders") return JsonSerializer.Serialize(new {
            reminders = snapshot.Items.Where(r => r.Status != "notified").Concat(snapshot.Items.Where(r => r.Status == "notified").TakeLast(5)),
            note = "One-time spoken reminders, not completed gameplay tasks. Notified reminders are never automatically replayed, including after interruption."
        }, ReminderJson);
        var transcript = await voice.GetUserTranscriptAsync(call.Turn, token);
        token.ThrowIfCancellationRequested();
        if (_closed || voice != _voice || call.Turn != voice.CurrentTurn) throw new OperationCanceledException();
        var result = await _reminderMemory.EditReminderAsync(call.Reminder ?? throw new InvalidOperationException("Specify a reminder."),
            transcript, snapshot.Scope, token);
        return JsonSerializer.Serialize(result, ReminderJson);
    }
}
