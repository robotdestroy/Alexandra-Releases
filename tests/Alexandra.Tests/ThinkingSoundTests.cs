using System.Buffers.Binary;
using Alexandra.Core;
using Xunit;

namespace Alexandra.Tests;

public sealed class ThinkingSoundTests
{
    [Fact]
    public void FirstPulseStartsAtTwoSecondsAndBusyUpdatesDoNotRestartDelay()
    {
        var clock = new ManualClock(); var player = new FakePlayer();
        using var cue = new ThinkingSoundCue(player, clock);
        Assert.Equal(TimeSpan.FromSeconds(2), ThinkingSoundCue.InitialDelay);
        cue.Update(1, true, false, true, false);
        clock.Advance(1); cue.Update(1, true, false, true, false);
        clock.Advance(.999); cue.Update(1, true, false, true, false);
        Assert.Equal(0, player.Pulses);
        clock.Advance(.001);
        cue.Update(1, true, false, true, false);
        Assert.Equal(1, player.Pulses);
    }

    [Fact]
    public void RepliesFinishingBeforeTwoSecondsRemainSilent()
    {
        var clock = new ManualClock(); var player = new FakePlayer();
        using var cue = new ThinkingSoundCue(player, clock);
        cue.Update(1, true, false, true, false);
        clock.Advance(1.999); cue.Update(1, false, true, true, false);
        clock.Advance(20); cue.Update(1, false, false, true, false);
        Assert.Equal(0, player.Pulses);
    }

    [Fact]
    public void PulsesAreSpacedAndDoNotCatchUpAfterUiStalls()
    {
        var clock = new ManualClock(); var player = new FakePlayer();
        using var cue = new ThinkingSoundCue(player, clock);
        cue.Update(1, true, false, true, false);
        clock.Advance(2); cue.Update(1, true, false, true, false);
        clock.Advance(4.99); cue.Update(1, true, false, true, false);
        Assert.Equal(1, player.Pulses);
        clock.Advance(.01); cue.Update(1, true, false, true, false);
        Assert.Equal(2, player.Pulses);
        clock.Advance(90); cue.Update(1, true, false, true, false);
        for (var i = 0; i < 100; i++) cue.Update(1, true, false, true, false);
        Assert.Equal(3, player.Pulses);
    }

    [Theory]
    [InlineData(false, false, true, false)] // complete/error/cancelled
    [InlineData(true, true, true, false)]   // actual speech, including buffered reply audio
    [InlineData(true, false, false, false)] // preference disabled
    [InlineData(true, false, true, true)]   // recording/setup/shutdown
    public void StopConditionsImmediatelySilenceAndReset(bool waiting, bool speaking, bool enabled, bool blocked)
    {
        var clock = new ManualClock(); var player = new FakePlayer();
        using var cue = new ThinkingSoundCue(player, clock);
        cue.Update(1, true, false, true, false);
        clock.Advance(2); cue.Update(1, true, false, true, false);
        Assert.True(player.Playing);
        cue.Update(1, waiting, speaking, enabled, blocked);
        Assert.False(player.Playing);
        Assert.Equal(1, player.Stops);
        clock.Advance(100); cue.Update(1, waiting, speaking, enabled, blocked);
        Assert.Equal(1, player.Pulses);
        cue.Update(1, true, false, true, false);
        clock.Advance(1.99); cue.Update(1, true, false, true, false);
        Assert.Equal(1, player.Pulses);
        clock.Advance(.01); cue.Update(1, true, false, true, false);
        Assert.Equal(2, player.Pulses);
    }

    [Fact]
    public void NewTurnGetsFullDelayAndOldPulseStops()
    {
        var clock = new ManualClock(); var player = new FakePlayer();
        using var cue = new ThinkingSoundCue(player, clock);
        cue.Update(1, true, false, true, false);
        clock.Advance(2); cue.Update(1, true, false, true, false);
        cue.Update(2, true, false, true, false);
        Assert.False(player.Playing);
        clock.Advance(1.99); cue.Update(2, true, false, true, false);
        Assert.Equal(1, player.Pulses);
        clock.Advance(.01); cue.Update(2, true, false, true, false);
        Assert.Equal(2, player.Pulses);
    }

    [Fact]
    public void InterruptAndDisposeHaveNoDelayedPlayback()
    {
        var clock = new ManualClock(); var player = new FakePlayer();
        var cue = new ThinkingSoundCue(player, clock);
        cue.Update(1, true, false, true, false);
        clock.Advance(1.999); cue.Reset();
        clock.Advance(20); cue.Update(2, false, false, true, false);
        Assert.Equal(0, player.Pulses);
        cue.Update(3, true, false, true, false);
        clock.Advance(2); cue.Update(3, true, false, true, false);
        cue.Dispose(); cue.Dispose();
        clock.Advance(20); cue.Update(3, true, false, true, false);
        Assert.False(player.Playing);
        Assert.Equal(1, player.Pulses);
        Assert.Equal(1, player.Disposals);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OptionalOutputFailureDoesNotThrowOrKeepRetrying(bool failPlay, bool failStop)
    {
        var clock = new ManualClock(); var player = new FakePlayer { FailPlay = failPlay, FailStop = failStop };
        var reported = 0;
        using var cue = new ThinkingSoundCue(player, clock, _ => reported++);
        cue.Update(1, true, false, true, false);
        clock.Advance(2); cue.Update(1, true, false, true, false);
        cue.Reset();
        clock.Advance(20); cue.Update(2, true, false, true, false);
        clock.Advance(20); cue.Update(2, true, false, true, false);
        Assert.Equal(1, reported);
        Assert.Equal(1, player.Pulses);
    }

    [Fact]
    public void PulseIsQuietFiniteAndHasSoftSilentEdges()
    {
        var bytes = ThinkingPulse.CreatePcm();
        Assert.Equal(ThinkingPulse.SampleRate * 2 * ThinkingPulse.DurationSeconds, bytes.Length);
        var samples = Enumerable.Range(0, bytes.Length / 2)
            .Select(i => (int)BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2))).ToArray();
        Assert.Equal(0, samples[0]); Assert.Equal(0, samples[^1]);
        Assert.InRange(samples.Max(Math.Abs), 400, 820); // Less than 2.5% full scale, no clipping.
        Assert.True(samples.Take(240).All(s => Math.Abs(s) <= 1));
        Assert.True(samples.TakeLast(240).All(s => Math.Abs(s) <= 1));
        Assert.True(samples.Zip(samples.Skip(1), (a,b) => Math.Abs(a-b)).Max() < 60);
        Assert.True(ThinkingSoundCue.PulseInterval.TotalSeconds > ThinkingPulse.DurationSeconds);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(double seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class FakePlayer : IThinkingSoundPlayer
    {
        public int Pulses, Stops, Disposals;
        public bool Playing, FailPlay, FailStop;
        public void PlayPulse() { Pulses++; Playing = true; if (FailPlay) throw new InvalidOperationException("Synthetic output failure"); }
        public void Stop() { Stops++; Playing = false; if (FailStop) throw new InvalidOperationException("Synthetic stop failure"); }
        public void Dispose() { Disposals++; Playing = false; }
    }
}
