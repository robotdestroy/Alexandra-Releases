using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Alexandra.Core;

namespace Alexandra.App;

public sealed class AudioDevice : IAudioDevice
{
    private WaveInEvent? _input;
    private MemoryStream? _recording;
    private WaveFileWriter? _writer;
    private TaskCompletionSource? _stopped;
    private WaveOutEvent? _output;
    private readonly object _sync = new();
    public event Action<float>? Level;
    public void Start(int device)
    {
        if (_input is not null) throw new InvalidOperationException("Microphone is already recording.");
        if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException("No microphone is connected.");
        _recording = new MemoryStream();
        _writer = new WaveFileWriter(_recording, new WaveFormat(24000, 16, 1));
        _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _input = new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(24000, 16, 1), BufferMilliseconds = 40 };
        _input.DataAvailable += (_, e) => {
            lock (_sync)
            {
                _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                float level = 0;
                for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                    level = Math.Max(level, Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f));
                Level?.Invoke(level);
            }
        };
        _input.RecordingStopped += (_, e) => {
            if (e.Exception is null) _stopped?.TrySetResult();
            else _stopped?.TrySetException(e.Exception);
        };
        try { _input.StartRecording(); }
        catch { DisposeInput(); throw; }
    }
    public async Task<byte[]> StopAsync()
    {
        if (_input is null || _stopped is null) return [];
        try
        {
            _input.StopRecording();
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (_sync)
            {
                _writer?.Dispose(); // Finalize the WAV header before transcription.
                _writer = null;
                return _recording?.ToArray() ?? [];
            }
        }
        finally { DisposeInput(); Level?.Invoke(0); }
    }
    private void DisposeInput()
    {
        var input = _input;
        _input = null;
        input?.Dispose();
        lock (_sync)
        {
            _writer?.Dispose(); _writer = null; _recording = null;
        }
    }
    public async Task PlayAsync(byte[] wav, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var memory = new MemoryStream(wav);
        using var reader = new WaveFileReader(memory);
        using var output = new WaveOutEvent();
        _output = output;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) => {
            if (e.Exception is null) complete.TrySetResult(); else complete.TrySetException(e.Exception);
        };
        var meter = new MeteringSampleProvider(reader.ToSampleProvider());
        meter.StreamVolume += (_, e) => Level?.Invoke(e.MaxSampleValues.Max());
        output.Init(meter);
        using var registration = cancellationToken.Register(output.Stop);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Play();
            await complete.Task.WaitAsync(cancellationToken);
        }
        finally { output.Stop(); if (_output == output) _output = null; }
    }
    public void StopPlayback() => _output?.Stop();
    public void Dispose() { StopPlayback(); DisposeInput(); }
}

