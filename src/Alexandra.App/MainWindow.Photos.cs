using System.Text.Json;
using Alexandra.Core;

namespace Alexandra.App;

public partial class MainWindow
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (SessionPhotoAnchor? Anchor, string Caption, bool Linked)> _photoNotes = new();

    private async Task<string> SaveJournalPhotoAsync(VoiceToolCall call, CancellationToken token)
    {
        var voice = _voice;
        var utterance = await voice.GetUserTranscriptAsync(call.Turn, token);
        token.ThrowIfCancellationRequested();
        if (_closed || voice != _voice || call.Turn != voice.CurrentTurn) throw new OperationCanceledException();
        if (!ScreenRequestPolicy.CanSave(utterance))
            throw new InvalidOperationException("Ask me to take or save a photo of Elite before I save an image.");
        var caption = call.Caption?.Trim() ?? "";
        if (caption.Length > 0) ReminderPolicy.ValidateExcerpt(caption, utterance, 180);
        if (!_savedScreenshots.TryGetValue(call.Turn, out var saved))
        {
            SessionPhotoAnchor? anchor = null;
            try
            {
                if (_gameSession.StartedAt is { } started)
                    anchor = await _memory.CapturePhotoAnchorAsync(DateTimeOffset.UtcNow, started, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.Write("photo.journal_unavailable", ex.GetType().Name); }
            token.ThrowIfCancellationRequested();
            if (_closed || voice != _voice || call.Turn != voice.CurrentTurn) throw new OperationCanceledException();
            saved = await _screens.SaveAsync(utterance, token);
            if (_closed || voice != _voice) throw new OperationCanceledException();
            _savedScreenshots[call.Turn] = saved; // Record a completed save even if this turn is subsequently cancelled.
            _photoNotes[call.Turn] = (anchor, caption, false);
            foreach (var old in _savedScreenshots.Keys.Where(t => t < call.Turn - 12))
            { _savedScreenshots.TryRemove(old, out _); _photoNotes.TryRemove(old, out _); }
        }
        var note = _photoNotes.GetValueOrDefault(call.Turn);
        if (!note.Linked && note.Anchor is not null)
        {
            try
            {
                note.Linked = await _memory.RecordSessionPhotoAsync(note.Anchor, saved, note.Caption, token);
                _photoNotes[call.Turn] = note;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.Write("photo.journal_unavailable", ex.GetType().Name); }
        }
        _log.Write("screenshot.saved", saved.FileName, call.Turn);
        return JsonSerializer.Serialize(new { screenshotSaved = true, fileName = saved.FileName, folder = saved.Folder,
            sessionLogLinked = note.Linked, note = note.Linked ? "Photo saved and linked in this session journal. No image upload." :
                "Photo saved locally. No active writable session journal was available; no image upload." });
    }
}
