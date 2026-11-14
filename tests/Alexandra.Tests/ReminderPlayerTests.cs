using Alexandra.Core;
using Xunit;

namespace Alexandra.Tests;

public sealed class ReminderPlayerTests
{
    private static readonly VoiceReminder Reminder = new("id", ReminderTrigger.Docked, "claim my rewards", null, DateTimeOffset.UtcNow);
    [Fact]
    public async Task RequiresQuietKeyAndSuccessfulDurableClaimBeforeAudio()
    {
        var source = new Source(); var audio = new Player(); using var player = new ReminderPlayer(source, audio);
        var claimed = false;
        await player.TryPlayAsync(Reminder, "marin", null, () => true, _ => Task.FromResult(true));
        await player.TryPlayAsync(Reminder, "marin", "synthetic", () => false, _ => Task.FromResult(true));
        Assert.Equal(0, source.Calls);
        audio.BeforePlay = () => Assert.True(claimed);
        await player.TryPlayAsync(Reminder, "marin", "synthetic", () => true, _ => { claimed = true; return Task.FromResult(true); });
        Assert.Equal(1, source.Calls); Assert.Equal(1, audio.Plays);
        Assert.Equal("claim my rewards", source.Text); Assert.Equal("marin", source.Voice);
    }
    [Fact]
    public async Task FailedClaimCannotSpeakAndFailureBacksOff()
    {
        var source = new Source(); var audio = new Player(); using var player = new ReminderPlayer(source, audio);
        await player.TryPlayAsync(Reminder, "marin", "synthetic", () => true, _ => throw new IOException("synthetic write failure"));
        await player.TryPlayAsync(Reminder, "marin", "synthetic", () => true, _ => Task.FromResult(true));
        Assert.Equal(1, source.Calls); Assert.Equal(0, audio.Plays);
    }
    [Fact]
    public async Task PttCancellationDiscardsLateProviderAudioAndDoesNotConsumeReminder()
    {
        var pending = new TaskCompletionSource<byte[]>(); var source = new Source { Fetch = _ => pending.Task };
        var audio = new Player(); using var player = new ReminderPlayer(source, audio); var claims = 0;
        var task = player.TryPlayAsync(Reminder, "marin", "synthetic", () => true, _ => { claims++; return Task.FromResult(true); });
        player.Cancel(); pending.SetResult(new byte[480]); await task;
        Assert.Equal(0, claims); Assert.Equal(0, audio.Plays); Assert.False(player.IsActive);
    }
    [Fact]
    public async Task LeavingOrCombatDuringGenerationDoesNotConsumeReminder()
    {
        var relevant = true; var source = new Source { Fetch = _ => { relevant = false; return Task.FromResult(new byte[480]); } };
        var audio = new Player(); using var player = new ReminderPlayer(source, audio); var claims = 0;
        await player.TryPlayAsync(Reminder, "marin", "synthetic", () => relevant, _ => { claims++; return Task.FromResult(true); });
        Assert.Equal(0, claims); Assert.Equal(0, audio.Plays);
    }
    [Fact]
    public async Task CombatOrSpeechDuringPlaybackStopsImmediately()
    {
        var relevant = true; var audio = new Player { Hold = true };
        using var player = new ReminderPlayer(new Source(), audio, delay: (_, _) => { relevant = false; return Task.CompletedTask; });
        await player.TryPlayAsync(Reminder, "marin", "synthetic", () => relevant, _ => Task.FromResult(true));
        Assert.Equal(1, audio.Plays); Assert.False(audio.HasUnplayedAudio);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(576002)]
    public async Task InvalidAudioNeverConsumesOrSpeaks(int bytes)
    {
        var source = new Source { Fetch = _ => Task.FromResult(new byte[bytes]) }; var audio = new Player();
        using var player = new ReminderPlayer(source, audio);
        await player.TryPlayAsync(Reminder, "marin", "synthetic", () => true, _ => throw new Exception("must not claim"));
        Assert.Equal(0, audio.Plays);
    }
    private sealed class Source : IReminderAudioSource
    {
        public int Calls; public string? Text, Voice;
        public Func<CancellationToken, Task<byte[]>> Fetch = _ => Task.FromResult(new byte[480]);
        public Task<byte[]> GenerateAsync(string text, string voice, string key, CancellationToken token)
        { Calls++; Text = text; Voice = voice; return Fetch(token); }
    }
    private sealed class Player : IStreamingAudioPlayer
    {
        public event Action<float>? Level { add { } remove { } }
        public int Plays; public bool Hold; public Action? BeforePlay;
        public bool HasUnplayedAudio { get; private set; }
        public void Append(string id, byte[] data) { BeforePlay?.Invoke(); Plays++; HasUnplayedAudio = Hold; }
        public IReadOnlyDictionary<string, int> Stop() { HasUnplayedAudio = false; return new Dictionary<string, int>(); }
        public void Dispose() => Stop();
    }
}
