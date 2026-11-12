using Alexandra.Core;
using Xunit;

namespace Alexandra.Tests;

public sealed class CombatCalloutTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-11T10:00:00Z");
    private static CombatContext Context(int second, bool shields = true, bool active = true,
        DateTimeOffset? hullAt = null, double? hull = null, DateTimeOffset? heatAt = null, string scope = "synthetic-session",
        bool eligible = true) => new("{}", "key", Start.AddSeconds(second), true,
            new(scope, eligible, active, shields, Start.AddSeconds(second), hullAt, hull, heatAt));
    private static CombatCallout? Observe(CombatCalloutPolicy p, CombatContext c, bool enabled = true, bool quiet = true) =>
        p.Observe(c, c.ReadAt, enabled, quiet);
    private static void Poll(CombatCalloutPolicy p, int from, int to, Func<int, CombatContext>? context = null)
    { for (var i = from; i <= to; i++) Observe(p, context?.Invoke(i) ?? Context(i)); }

    [Fact]
    public void StartupDisabledAndReenabledNeverReplayWarnings()
    {
        var p = new CombatCalloutPolicy();
        Assert.Null(Observe(p, Context(0, false, hullAt: Start, hull: 20, heatAt: Start)));
        Assert.Null(Observe(p, Context(1, false, hullAt: Start, hull: 20, heatAt: Start)));
        Observe(p, Context(2), enabled: false);
        Assert.Null(Observe(p, Context(3, false, hullAt: Start.AddSeconds(3), hull: 20)));
        Assert.Null(Observe(p, Context(4, false, hullAt: Start.AddSeconds(3), hull: 20)));
    }
    [Fact]
    public void ShieldLossIsOneEdgeNotAHardpointsOrContinuousDangerAnnouncement()
    {
        var p = new CombatCalloutPolicy(); Assert.Null(Observe(p, Context(0)));
        var warning = Observe(p, Context(1, false));
        Assert.Equal(CombatCalloutKind.ShieldsLost, warning!.Kind);
        Assert.Null(Observe(p, Context(2, false)));
        Assert.Null(Observe(p, Context(3, false)));
    }
    [Fact]
    public void BusyEventsAreConsumedNotSpokenAfterReplyFinishes()
    {
        var p = new CombatCalloutPolicy(); Observe(p, Context(0));
        Assert.Null(Observe(p, Context(1, false), quiet: false));
        Assert.Null(Observe(p, Context(2, false)));
        Assert.Null(Observe(p, Context(3, false, hullAt: Start.AddSeconds(3), hull: 20), quiet: false));
        Assert.Null(Observe(p, Context(4, false, hullAt: Start.AddSeconds(3), hull: 20)));
    }
    [Fact]
    public void NoCombatNoTrustedVehicleOrStaleContextMeansSilence()
    {
        var p = new CombatCalloutPolicy(); Observe(p, Context(0));
        Assert.Null(Observe(p, Context(1, false, active: false)));
        Observe(p, Context(2));
        Assert.Null(Observe(p, Context(3, false, eligible: false)));
        Assert.Null(Observe(p, Context(4, false)));
        var old = Context(5, false);
        Assert.Null(p.Observe(old, Start.AddSeconds(9), true, true));
    }
    [Fact]
    public void ScopeChangeGapOrClockReversalRequiresANewBaseline()
    {
        var p = new CombatCalloutPolicy(); Observe(p, Context(0));
        Assert.Null(Observe(p, Context(1, false, scope: "new-session")));
        Assert.Null(Observe(p, Context(20, false, scope: "new-session")));
        Assert.Null(Observe(p, Context(19, false, scope: "new-session")));
    }
    [Theory]
    [InlineData(40, CombatCalloutKind.LowHull)]
    [InlineData(20, CombatCalloutKind.CriticalHull)]
    [InlineData(0, CombatCalloutKind.CriticalHull)]
    public void NewHullDamageCrossingsArePrioritized(double hull, CombatCalloutKind kind)
    {
        var p = new CombatCalloutPolicy(); Observe(p, Context(0));
        var at = Start.AddSeconds(1);
        var callout = Observe(p, Context(1, false, hullAt: at, hull: hull, heatAt: at));
        Assert.Equal(kind, callout!.Kind); Assert.Equal(at, callout.At);
        Assert.Null(Observe(p, Context(2, false, hullAt: at, hull: hull, heatAt: at)));
    }
    [Fact]
    public void HeatDamageOnlyAnnouncesNewRecentEvidence()
    {
        var p = new CombatCalloutPolicy(); Observe(p, Context(0));
        Assert.Equal(CombatCalloutKind.HeatDamage, Observe(p, Context(1, heatAt: Start.AddSeconds(1)))!.Kind);
        Assert.Null(Observe(p, Context(2, heatAt: Start.AddSeconds(1))));
        var fresh = new CombatCalloutPolicy(); Observe(fresh, Context(20));
        Assert.Null(Observe(fresh, Context(21, heatAt: Start)));
    }
    [Fact]
    public void OverheatingWarnsBeforeDamageOnceAndStopsWhenCooled()
    {
        CombatContext Hot(int second, bool hot) => Context(second) with {
            Callouts = Context(second).Callouts! with { Overheating = hot, HeatWarningAt = second > 0 ? Start.AddSeconds(1) : null }
        };
        var p = new CombatCalloutPolicy(); Observe(p, Hot(0, false));
        var warning = Observe(p, Hot(1, true));
        Assert.Equal(CombatCalloutKind.Overheating, warning!.Kind);
        Assert.Equal("Overheating.", CombatCalloutLines.Text(warning.Kind));
        Assert.Null(Observe(p, Hot(2, true)));
        Assert.True(CombatCalloutPolicy.IsRelevant(warning, Hot(2, true), Start.AddSeconds(2)));
        Assert.False(CombatCalloutPolicy.IsRelevant(warning, Hot(2, false), Start.AddSeconds(2)));
        Assert.Null(Observe(p, Hot(3, false)));
        Assert.Null(Observe(p, Hot(4, true))); // Cooldown, even if the flag flickers.
    }
    [Fact]
    public void RecentHeatWarningWorksBeforeSidecarUpdateButNeverReplaysOnStartupOrAfterBusy()
    {
        CombatContext Warning(int second) => Context(second) with {
            Callouts = Context(second).Callouts! with { Overheating = false, StatusAt = Start, HeatWarningAt = Start.AddSeconds(1) }
        };
        var p = new CombatCalloutPolicy(); Observe(p, Context(0));
        Assert.Equal(CombatCalloutKind.Overheating, Observe(p, Warning(1))!.Kind);
        var coldStart = new CombatCalloutPolicy(); Assert.Null(Observe(coldStart, Warning(1)));
        Assert.Null(Observe(coldStart, Warning(2)));
        var busy = new CombatCalloutPolicy(); Observe(busy, Context(0));
        Assert.Null(Observe(busy, Warning(1), quiet: false)); Assert.Null(Observe(busy, Warning(2)));
    }
    [Fact]
    public void HeatDamageTakesPriorityOverOverheatingAndCriticalHullTakesPriorityOverBoth()
    {
        var hot = Context(1, heatAt: Start.AddSeconds(1)) with {
            Callouts = Context(1, heatAt: Start.AddSeconds(1)).Callouts! with { Overheating = true, HeatWarningAt = Start.AddSeconds(1) }
        };
        var p = new CombatCalloutPolicy(); Observe(p, Context(0));
        Assert.Equal(CombatCalloutKind.HeatDamage, Observe(p, hot)!.Kind);
        var critical = new CombatCalloutPolicy(); Observe(critical, Context(0));
        Assert.Equal(CombatCalloutKind.CriticalHull, Observe(critical, hot with {
            Callouts = hot.Callouts! with { HullDamageAt = Start.AddSeconds(1), HullPercent = 20 }
        })!.Kind);
    }
    [Fact]
    public void CooldownsLimitChatterButPermitCriticalEscalationAfterFiveSeconds()
    {
        var p = new CombatCalloutPolicy(); Observe(p, Context(0));
        Assert.NotNull(Observe(p, Context(1, false)));
        Poll(p, 2, 4, i => Context(i, false));
        Assert.Null(Observe(p, Context(5, false, heatAt: Start.AddSeconds(5)))); // Global cooldown.
        var criticalAt = Start.AddSeconds(6);
        Assert.Equal(CombatCalloutKind.CriticalHull, Observe(p, Context(6, false, hullAt: criticalAt, hull: 20))!.Kind);
        Poll(p, 7, 26);
        Assert.Null(Observe(p, Context(27, false))); // Shield category's 90 seconds.
        Poll(p, 28, 91);
        Assert.Equal(CombatCalloutKind.ShieldsLost, Observe(p, Context(92, false))!.Kind);
    }
    [Fact]
    public void RecheckRejectsHealedChangedVehicleStaleAndDisabledContexts()
    {
        var warning = new CombatCallout(CombatCalloutKind.ShieldsLost, "synthetic-session", Start);
        Assert.True(CombatCalloutPolicy.IsRelevant(warning, Context(1, false), Start.AddSeconds(1)));
        Assert.False(CombatCalloutPolicy.IsRelevant(warning, Context(1), Start.AddSeconds(1)));
        Assert.False(CombatCalloutPolicy.IsRelevant(warning, Context(1, false, scope: "fighter"), Start.AddSeconds(1)));
        Assert.False(CombatCalloutPolicy.IsRelevant(warning, Context(1, false, active: false), Start.AddSeconds(1)));
        Assert.False(CombatCalloutPolicy.IsRelevant(warning, Context(9, false), Start.AddSeconds(9)));
        Assert.False(CombatCalloutPolicy.IsRelevant(warning, Context(1, false), Start.AddSeconds(5)));
        Assert.False(CombatCalloutPolicy.IsRelevant(warning, null, Start.AddSeconds(1)));
    }
    [Fact]
    public async Task PlaybackCachesPerVoiceAndKindAndDoesNotRequestWhenOff()
    {
        var source = new AudioSource(); var audio = new Player();
        using var player = new CombatCalloutPlayer(source, audio);
        var call = new CombatCallout(CombatCalloutKind.ShieldsLost, "x", Start);
        await player.TryPlayAsync(call, "marin", "synthetic-key", false, () => true);
        await player.TryPlayAsync(call, "marin", null, true, () => true);
        Assert.Equal(0, source.Calls);
        await player.TryPlayAsync(call, "marin", "synthetic-key", true, () => true);
        await player.TryPlayAsync(call, "marin", "synthetic-key", true, () => true);
        await player.TryPlayAsync(call, "cedar", "synthetic-key", true, () => true);
        await player.TryPlayAsync(call with { Kind = CombatCalloutKind.HeatDamage }, "cedar", "synthetic-key", true, () => true);
        Assert.Equal(3, source.Calls); Assert.Equal(4, audio.Plays);
        player.Cancel(clearCache: true);
        await player.TryPlayAsync(call, "marin", "synthetic-key", true, () => true);
        Assert.Equal(4, source.Calls);
    }
    [Fact]
    public async Task CancelOnPttOrSetupRejectsLateProviderAudioWithoutFallback()
    {
        var pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new AudioSource { Fetch = _ => pending.Task }; var audio = new Player();
        using var player = new CombatCalloutPlayer(source, audio);
        var task = player.TryPlayAsync(new(CombatCalloutKind.LowHull, "x", Start), "marin", "synthetic-key", true, () => true);
        Assert.True(player.IsActive); player.Cancel();
        pending.SetResult(new byte[480]); await task;
        Assert.False(player.IsActive); Assert.Equal(0, audio.Plays);
    }
    [Fact]
    public async Task EvidenceChangedDuringFetchDoesNotSpeakButCanReuseFixedClipLater()
    {
        var current = true;
        var source = new AudioSource { Fetch = _ => { current = false; return Task.FromResult(new byte[480]); } };
        var audio = new Player(); using var player = new CombatCalloutPlayer(source, audio);
        var call = new CombatCallout(CombatCalloutKind.LowHull, "x", Start);
        await player.TryPlayAsync(call, "marin", "synthetic-key", true, () => current);
        Assert.Equal(0, audio.Plays);
        current = true;
        await player.TryPlayAsync(call, "marin", "synthetic-key", true, () => current);
        Assert.Equal(1, audio.Plays); Assert.Equal(1, source.Calls);
    }
    [Fact]
    public async Task NewSpeechOrHealedStateStopsPlayingClip()
    {
        var current = true; var audio = new Player { Hold = true };
        using var player = new CombatCalloutPlayer(new AudioSource(), audio, delay: (_, _) => { current = false; return Task.CompletedTask; });
        await player.TryPlayAsync(new(CombatCalloutKind.ShieldsLost, "x", Start), "marin", "synthetic-key", true, () => current);
        Assert.Equal(1, audio.Plays); Assert.False(audio.HasUnplayedAudio);
    }
    [Fact]
    public async Task GenerationFailureBacksOffWithoutSpeakingOrRepeatedApiRequests()
    {
        var clock = new Clock(); var source = new AudioSource { Fetch = _ => throw new InvalidOperationException("Synthetic") };
        var audio = new Player(); using var player = new CombatCalloutPlayer(source, audio, clock);
        var failures = 0; player.Failed += () => failures++;
        var call = new CombatCallout(CombatCalloutKind.HeatDamage, "x", Start);
        await player.TryPlayAsync(call, "marin", "synthetic-key", true, () => true);
        await player.TryPlayAsync(call, "marin", "synthetic-key", true, () => true);
        Assert.Equal(1, source.Calls); Assert.Equal(1, failures); Assert.Equal(0, audio.Plays);
        clock.Now = Start.AddMinutes(2);
        await player.TryPlayAsync(call, "marin", "synthetic-key", true, () => true);
        Assert.Equal(2, source.Calls);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(288002)]
    public async Task InvalidOrTooLongClipsNeverPlay(int bytes)
    {
        var source = new AudioSource { Fetch = _ => Task.FromResult(new byte[bytes]) }; var audio = new Player();
        using var player = new CombatCalloutPlayer(source, audio);
        await player.TryPlayAsync(new(CombatCalloutKind.HeatDamage, "x", Start), "marin", "synthetic-key", true, () => true);
        Assert.Equal(0, audio.Plays);
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = Start; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class AudioSource : ICombatCalloutAudioSource
    {
        public int Calls;
        public Func<CancellationToken, Task<byte[]>> Fetch = _ => Task.FromResult(new byte[480]);
        public Task<byte[]> GenerateAsync(CombatCalloutKind kind, string voice, string key, CancellationToken token)
        { Calls++; return Fetch(token); }
    }
    private sealed class Player : IStreamingAudioPlayer
    {
        public event Action<float>? Level { add { } remove { } }
        public int Plays; public bool Hold; public bool HasUnplayedAudio { get; private set; }
        public void Append(string id, byte[] data) { Plays++; HasUnplayedAudio = Hold; }
        public IReadOnlyDictionary<string, int> Stop() { HasUnplayedAudio = false; return new Dictionary<string, int>(); }
        public void Dispose() => Stop();
    }
}
