using System.IO;
using Alexandra.Core;
using NAudio.Wave;

namespace Alexandra.App;

// Separate from reply playback: these samples never enter voice history, the microphone, or interruption accounting.
public sealed class ThinkingSoundPlayer(byte[]? pcm = null) : IThinkingSoundPlayer
{
    private readonly byte[] _pcm = pcm ?? ThinkingPulse.CreatePcm();
    private WaveOutEvent? _output;
    private RawSourceWaveStream? _reader;
    private MemoryStream? _memory;
    private Exception? _playbackFailure;
    private bool _disposed;

    public void PlayPulse()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        if (Interlocked.Exchange(ref _playbackFailure, null) is { } failure)
            throw new InvalidOperationException("Thinking sound output is unavailable.", failure);
        _memory = new MemoryStream(_pcm, writable: false);
        _reader = new RawSourceWaveStream(_memory, new WaveFormat(ThinkingPulse.SampleRate, 16, 1));
        _output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 2 };
        _output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is { } error) Interlocked.CompareExchange(ref _playbackFailure, error, null);
        };
        try { _output.Init(_reader); _output.Play(); }
        catch { Stop(); throw; }
    }

    public void Stop()
    {
        var output = _output;
        var reader = _reader;
        var memory = _memory;
        _output = null; _reader = null; _memory = null;
        try { output?.Stop(); }
        finally
        {
            try { output?.Dispose(); }
            finally { reader?.Dispose(); memory?.Dispose(); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
