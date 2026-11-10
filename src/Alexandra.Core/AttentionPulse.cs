using System.Buffers.Binary;

namespace Alexandra.Core;

// Brief, quiet two-note notification, distinct from the ongoing thinking pulse. Never substitutes another voice.
public static class AttentionPulse
{
    public static byte[] CreatePcm()
    {
        const int sampleRate = 24000;
        var pcm = new byte[sampleRate]; // half a second, 16-bit mono
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            var time = i / (double)sampleRate;
            var noteTime = time < .2 ? time : time - .3;
            if (noteTime < 0 || noteTime >= .2) continue;
            var frequency = time < .2 ? 330 : 262;
            var envelope = Math.Pow(Math.Sin(Math.PI * noteTime / .2), 2);
            var value = (short)Math.Round(short.MaxValue * .035 * envelope * Math.Sin(2 * Math.PI * frequency * noteTime));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), value);
        }
        return pcm;
    }
}
