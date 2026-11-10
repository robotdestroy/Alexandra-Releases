using System.Buffers.Binary;

namespace Alexandra.Core;

// An original local cue: no downloaded asset, voice synthesis, network request or microphone input.
public static class ThinkingPulse
{
    public const int SampleRate = 24000;
    public const int DurationSeconds = 2;
    public const double PeakAmplitude = .025;

    public static byte[] CreatePcm()
    {
        var count = SampleRate * DurationSeconds;
        var pcm = new byte[count * sizeof(short)];
        for (var i = 0; i < count; i++)
        {
            var time = (double)i / SampleRate;
            // A slow raised-cosine swell, zero at both ends, avoids sharp attack/release clicks.
            var swell = Math.Pow(Math.Sin(Math.PI * i / (count - 1)), 2);
            var tone = .78 * Math.Sin(2 * Math.PI * 196 * time) + .22 * Math.Sin(2 * Math.PI * 294 * time);
            var sample = (short)Math.Round(short.MaxValue * PeakAmplitude * swell * tone);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), sample);
        }
        return pcm;
    }
}
