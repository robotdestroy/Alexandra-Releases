using Alexandra.Core;
using NAudio.Wave;

namespace Alexandra.App;

// Tracks real audio against the device playhead, excluding silence inserted during network gaps.
public sealed class StreamingAudioPlayer : IStreamingAudioPlayer
{
    private readonly object _sync = new();
    private WaveOutEvent? _output;
    private PcmQueue? _queue;
    public event Action<float>? Level;
    public void Append(string itemId, byte[] pcm)
    {
        lock (_sync)
        {
            if (_output is null)
            {
                _queue = new PcmQueue(value => Level?.Invoke(value));
                _output = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 2 };
                _output.Init(_queue);
                _output.Play();
            }
            _queue!.Append(itemId, pcm);
        }
    }
    public bool HasUnplayedAudio
    { get { lock (_sync) return _output is not null && _queue!.Unplayed(_output.GetPosition()); } }
    public IReadOnlyDictionary<string, int> Stop()
    {
        lock (_sync)
        {
            if (_output is null) return new Dictionary<string, int>();
            var played = _queue!.Played(_output.GetPosition());
            _output.Stop(); _output.Dispose(); _output = null; _queue = null;
            Level?.Invoke(0);
            return played;
        }
    }
    public void Dispose() => Stop();
    private sealed class PcmQueue(Action<float> level) : IWaveProvider
    {
        private readonly object _sync = new();
        private readonly Queue<Chunk> _chunks = [];
        private readonly List<Span> _spans = [];
        private long _physical, _queued;
        public WaveFormat WaveFormat { get; } = new(24000, 16, 1);
        public void Append(string id, byte[] bytes)
        {
            lock (_sync)
            {
                if (_queued + bytes.Length > 24000 * 2 * 180) throw new InvalidOperationException("Voice playback buffer filled. Please interrupt and try again.");
                _chunks.Enqueue(new(id, bytes)); _queued += bytes.Length;
            }
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            float peak = 0;
            lock (_sync)
            {
                var written = 0;
                while (written < count && _chunks.TryPeek(out var chunk))
                {
                    var take = Math.Min(count - written, chunk.Bytes.Length - chunk.Offset);
                    Buffer.BlockCopy(chunk.Bytes, chunk.Offset, buffer, offset + written, take);
                    _spans.Add(new(chunk.Id, _physical + written, take));
                    written += take; chunk.Offset += take; _queued -= take;
                    if (chunk.Offset == chunk.Bytes.Length) _chunks.Dequeue();
                }
                _physical += count;
                for (var i = offset; i + 1 < offset + written; i += 2)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768f));
            }
            level(peak);
            return count;
        }
        public bool Unplayed(long playhead)
        { lock (_sync) return _queued > 0 || _spans.Any(s => s.Start + s.Count > playhead); }
        public IReadOnlyDictionary<string, int> Played(long playhead)
        {
            lock (_sync) return _spans.GroupBy(s => s.Id).ToDictionary(g => g.Key,
                g => (int)(g.Sum(s => Math.Clamp(playhead - s.Start, 0, s.Count)) / 48));
        }
        private sealed class Chunk(string id, byte[] bytes)
        { public string Id { get; } = id; public byte[] Bytes { get; } = bytes; public int Offset { get; set; } }
        private sealed record Span(string Id, long Start, int Count);
    }
}

