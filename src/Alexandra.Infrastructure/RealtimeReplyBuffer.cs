using System.Text.Json;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

public sealed record ReplyTranscript(int Turn, string ItemId, string Text);
public sealed record ReplyOutput(IReadOnlyList<VoiceFrame> Audio, IReadOnlyList<ReplyTranscript> Transcripts);

// Realtime can speak commentary even when asked to be brief. Only final-answer items reach
// playback/history. If the phase arrives late, buffer until it is known, never guess from words.
public sealed class RealtimeReplyBuffer
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Item> _items = [];
    private int _bufferedBytes;
    private static ReplyOutput Empty => new([], []);
    public ReplyOutput Audio(VoiceFrame frame)
    {
        lock (_sync)
        {
            var item = Get(frame.ItemId);
            if (item.Phase == "commentary") return Empty;
            if (item.Phase == "final_answer") return new([frame], []);
            if (_bufferedBytes + frame.Pcm.Length > 8 * 1024 * 1024)
                throw new InvalidOperationException("The voice reply exceeded its buffering limit.");
            item.Audio.Add(frame); _bufferedBytes += frame.Pcm.Length;
            return Empty;
        }
    }
    public ReplyOutput Transcript(int turn, string id, string text)
    {
        lock (_sync)
        {
            var item = Get(id);
            if (item.Phase == "commentary") return Empty;
            var transcript = new ReplyTranscript(turn, id, text);
            if (item.Phase == "final_answer") return new([], [transcript]);
            item.Transcript = transcript;
            return Empty;
        }
    }
    public ReplyOutput Observe(JsonElement outputItem)
    {
        lock (_sync)
        {
            if (!outputItem.TryGetProperty("id", out var id) || id.GetString() is not { } value) return Empty;
            if (outputItem.TryGetProperty("phase", out var phase) && phase.GetString() is "commentary" or "final_answer")
                Get(value).Phase = phase.GetString();
            return Drain(Get(value));
        }
    }
    public ReplyOutput Complete(JsonElement output)
    {
        lock (_sync)
        {
            var audio = new List<VoiceFrame>(); var text = new List<ReplyTranscript>();
            var hasTools = output.EnumerateArray().Any(i => i.TryGetProperty("type", out var t) && t.GetString() == "function_call");
            foreach (var node in output.EnumerateArray())
            {
                var ready = Observe(node);
                audio.AddRange(ready.Audio); text.AddRange(ready.Transcripts);
            }
            foreach (var item in _items.Values)
            {
                // Legacy/no-phase responses are released only once no further tools are needed.
                item.Phase ??= hasTools ? "commentary" : "final_answer";
                var ready = Drain(item);
                audio.AddRange(ready.Audio); text.AddRange(ready.Transcripts);
            }
            Reset();
            return new(audio, text);
        }
    }
    public void Reset() { lock (_sync) { _items.Clear(); _bufferedBytes = 0; } }
    private Item Get(string id)
    {
        if (!_items.TryGetValue(id, out var item)) _items[id] = item = new();
        return item;
    }
    private ReplyOutput Drain(Item item)
    {
        if (item.Phase is null) return Empty;
        var result = item.Phase == "final_answer"
            ? new ReplyOutput(item.Audio.ToArray(), item.Transcript is { } text ? [text] : []) : Empty;
        _bufferedBytes -= item.Audio.Sum(frame => frame.Pcm.Length);
        item.Audio.Clear(); item.Transcript = null;
        return result;
    }
    private sealed class Item
    {
        public string? Phase { get; set; }
        public List<VoiceFrame> Audio { get; } = [];
        public ReplyTranscript? Transcript { get; set; }
    }
}
