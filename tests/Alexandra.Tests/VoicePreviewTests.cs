using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class VoicePreviewTests
{
    private static Task Immediate(TimeSpan _, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    private sealed class Source : IVoicePreviewSource
    {
        public int Calls;
        public Func<string, CancellationToken, Task<byte[]>> Generate = (_, _) => Task.FromResult(new byte[480]);
        public Task<byte[]> GenerateAsync(string voice, string key, CancellationToken token)
        { Calls++; return Generate(voice, token); }
    }
    private sealed class Player : IStreamingAudioPlayer
    {
        public event Action<float>? Level { add { } remove { } }
        public List<byte[]> Played { get; } = [];
        public int Stops, Disposals;
        public bool Hold;
        public bool HasUnplayedAudio { get; private set; }
        public void Append(string itemId, byte[] pcm) { Played.Add(pcm); HasUnplayedAudio = Hold; }
        public IReadOnlyDictionary<string, int> Stop() { Stops++; HasUnplayedAudio = false; return new Dictionary<string, int>(); }
        public void Dispose() { Disposals++; }
    }
    [Theory]
    [InlineData("marin", null, true)]
    [InlineData("marin", " ", true)]
    [InlineData("marin", "synthetic-key", false)]
    [InlineData("sol", "synthetic-key", true)]
    public async Task MissingPermissionKeyOrSupportedVoiceDoesNotRequestAudio(string voice, string? key, bool consent)
    {
        var source = new Source(); var player = new Player();
        using var controller = new VoicePreviewController(source, player, Immediate);
        var status = ""; controller.StatusChanged += text => status = text;
        await controller.PreviewAsync(voice, key, consent);
        Assert.Equal(0, source.Calls); Assert.Empty(player.Played); Assert.NotEmpty(status);
    }
    [Fact]
    public async Task CacheIsPerVoiceAndCanBeCleared()
    {
        var source = new Source(); var player = new Player();
        using var controller = new VoicePreviewController(source, player, Immediate);
        Assert.Equal(0, source.Calls);
        await controller.PreviewAsync("marin", "synthetic-key", true);
        await controller.PreviewAsync("cedar", "synthetic-key", true);
        await controller.PreviewAsync("marin", "synthetic-key", true);
        Assert.Equal(2, source.Calls); Assert.Equal(3, player.Played.Count);
        controller.Cancel(clearCache: true);
        await controller.PreviewAsync("marin", "synthetic-key", true);
        Assert.Equal(3, source.Calls);
    }
    [Fact]
    public async Task RapidSelectionCancelsDebouncedRequestBeforeUsingApi()
    {
        var source = new Source(); var player = new Player();
        var firstWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waits = 0;
        using var controller = new VoicePreviewController(source, player, (_, token) =>
            ++waits == 1 ? firstWait.Task.WaitAsync(token) : Task.CompletedTask);
        var first = controller.PreviewAsync("coral", "synthetic-key", true);
        await controller.PreviewAsync("marin", "synthetic-key", true);
        await first;
        Assert.Equal(1, source.Calls); Assert.Single(player.Played);
    }
    [Fact]
    public async Task SupersededFetchCannotPlayOrReplaceNewStatusEvenIfProviderIgnoresCancellation()
    {
        var stale = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Generate = (voice, _) => voice == "coral" ? stale.Task : Task.FromResult(new byte[10]) };
        var player = new Player(); var status = "";
        using var controller = new VoicePreviewController(source, player, Immediate);
        controller.StatusChanged += text => status = text;
        var first = controller.PreviewAsync("coral", "synthetic-key", true);
        await controller.PreviewAsync("marin", "synthetic-key", true);
        stale.SetResult(new byte[20]); await first;
        Assert.Single(player.Played); Assert.Equal(10, player.Played[0].Length);
        Assert.StartsWith("marin preview finished", status);
    }
    [Fact]
    public async Task SwitchingAndClosingStopPlaybackAndDisposeOnce()
    {
        var source = new Source(); var player = new Player { Hold = true };
        var controller = new VoicePreviewController(source, player, (duration, token) =>
            duration.TotalMilliseconds == 300 ? Task.CompletedTask : Task.Delay(Timeout.Infinite, token));
        var first = controller.PreviewAsync("coral", "synthetic-key", true);
        Assert.True(player.HasUnplayedAudio);
        player.Hold = false;
        await controller.PreviewAsync("marin", "synthetic-key", true); await first;
        Assert.False(player.HasUnplayedAudio); Assert.Equal(2, player.Played.Count);
        Assert.True(player.Stops >= 3);
        player.Hold = true;
        var final = controller.PreviewAsync("marin", "synthetic-key", true);
        controller.Dispose(); await final; controller.Dispose();
        await controller.PreviewAsync("cedar", "synthetic-key", true);
        Assert.False(player.HasUnplayedAudio); Assert.Equal(1, player.Disposals); Assert.Equal(2, source.Calls);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(576002)]
    public async Task InvalidAudioIsNeverPlayedOrCached(int length)
    {
        var source = new Source { Generate = (_, _) => Task.FromResult(new byte[length]) };
        var player = new Player(); var status = "";
        using var controller = new VoicePreviewController(source, player, Immediate);
        controller.StatusChanged += text => status = text;
        await controller.PreviewAsync("marin", "synthetic-key", true);
        await controller.PreviewAsync("marin", "synthetic-key", true);
        Assert.Empty(player.Played); Assert.Equal(2, source.Calls); Assert.StartsWith("Couldn't play", status);
    }
    [Fact]
    public async Task FailureDoesNotExposeProviderMessageAndRetryWorks()
    {
        var source = new Source { Generate = (_, _) => throw new Exception("sensitive provider error") };
        var player = new Player(); var status = "";
        using var controller = new VoicePreviewController(source, player, Immediate);
        controller.StatusChanged += text => status = text;
        await controller.PreviewAsync("marin", "synthetic-key", true);
        Assert.DoesNotContain("sensitive", status);
        source.Generate = (_, _) => Task.FromResult(new byte[100]);
        await controller.PreviewAsync("marin", "synthetic-key", true);
        Assert.Single(player.Played);
    }
    [Fact]
    public async Task CancelDuringFetchDiscardsLateAudioAndStatus()
    {
        var pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Generate = (_, _) => pending.Task }; var player = new Player(); var status = "";
        using var controller = new VoicePreviewController(source, player, Immediate);
        controller.StatusChanged += text => status = text;
        var work = controller.PreviewAsync("marin", "synthetic-key", true);
        controller.Cancel(clearCache: true); pending.SetResult(new byte[100]); await work;
        Assert.Empty(player.Played); Assert.Equal("", status);
    }
    [Fact]
    public void PreviewProtocolHasFixedPhraseNoToolsOrMicrophoneAndIsOutOfConversation()
    {
        Assert.Equal("marin", VoiceCatalog.DefaultVoice);
        Assert.Equal(VoiceCatalog.DefaultVoice, VoiceCatalog.Names[0]);
        Assert.Equal("marin", RealtimeProtocol.NormalizeVoice("unsupported"));
        Assert.Equal("coral", RealtimeProtocol.NormalizeVoice("coral"));
        Assert.Equal(10, VoiceCatalog.Names.Count); Assert.Equal(10, VoiceCatalog.Names.Distinct().Count());
        foreach (var voice in VoiceCatalog.Names)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(RealtimeVoicePreviewSource.SessionUpdate(voice)));
            var session = doc.RootElement.GetProperty("session");
            Assert.Equal(voice, session.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
            Assert.Equal(24000, session.GetProperty("audio").GetProperty("output").GetProperty("format").GetProperty("rate").GetInt32());
            Assert.Equal(JsonValueKind.Null, session.GetProperty("audio").GetProperty("input").GetProperty("turn_detection").ValueKind);
            Assert.Equal("none", session.GetProperty("tool_choice").GetString());
            Assert.Empty(session.GetProperty("tools").EnumerateArray());
        }
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(RealtimeVoicePreviewSource.CreateResponse()));
        var config = response.RootElement.GetProperty("response");
        Assert.Equal("none", config.GetProperty("conversation").GetString());
        Assert.Empty(config.GetProperty("tools").EnumerateArray());
        var content = config.GetProperty("input")[0].GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        Assert.Equal("Say exactly: " + VoiceCatalog.SampleLine, content[0].GetProperty("text").GetString());
    }
    private sealed class Wire : IRealtimeTransport
    {
        private readonly Channel<string> _events = Channel.CreateUnbounded<string>();
        public List<JsonElement> Sent { get; } = [];
        public string? Model; public int Disposals;
        public string ConfirmedVoice = "marin", AudioResponseId = "preview", DoneStatus = "completed", OutputType = "message";
        public string? Transcript;
        public byte[] Audio = new byte[480]; public bool WaitForever, ServiceError;
        public bool IsConnected => true;
        public Task ConnectAsync(string model, string key, CancellationToken token) { Model = model; return Task.CompletedTask; }
        private void Emit(object value) => _events.Writer.TryWrite(JsonSerializer.Serialize(value));
        public Task SendAsync(string json, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(json); var e = doc.RootElement.Clone(); Sent.Add(e);
            if (e.GetProperty("type").GetString() == "session.update")
                Emit(new { type = "session.updated", session = new { audio = new { output = new { voice = ConfirmedVoice } } } });
            else if (!WaitForever)
            {
                if (ServiceError) Emit(new { type = "error", error = new { message = "sensitive-provider-error" } });
                else
                {
                    Emit(new { type = "response.created", response = new { id = "preview" } });
                    Emit(new { type = "response.output_audio.delta", response_id = AudioResponseId, delta = Convert.ToBase64String(Audio) });
                    Emit(new { type = "response.done", response = new { id = "preview", status = DoneStatus,
                        output = new[] { new { type = OutputType, content = new[] { new { type = "audio", transcript = Transcript } } } } } });
                }
            }
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<string> ReceiveAsync([EnumeratorCancellation] CancellationToken token)
        { await foreach (var e in _events.Reader.ReadAllAsync(token)) yield return e; }
        public ValueTask DisposeAsync() { Disposals++; _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    [Fact]
    public async Task SourceUsesConversationModelReturnsCompletePcmAndClosesConnection()
    {
        var wire = new Wire();
        var audio = await new RealtimeVoicePreviewSource(() => wire).GenerateAsync("marin", "synthetic-key", default);
        Assert.Equal(wire.Audio, audio); Assert.Equal(RealtimeProtocol.Model, wire.Model);
        Assert.Equal(2, wire.Sent.Count); Assert.Equal(1, wire.Disposals);
    }
    [Theory]
    [InlineData("voice")]
    [InlineData("response")]
    [InlineData("failed")]
    [InlineData("incomplete")]
    [InlineData("tool")]
    [InlineData("empty")]
    [InlineData("odd")]
    [InlineData("oversize")]
    [InlineData("service")]
    public async Task SourceRejectsFallbackVoicePartialOrInvalidResponses(string failure)
    {
        var wire = new Wire();
        switch (failure)
        {
            case "voice": wire.ConfirmedVoice = "coral"; break;
            case "response": wire.AudioResponseId = "wrong"; break;
            case "failed": case "incomplete": wire.DoneStatus = failure; break;
            case "tool": wire.OutputType = "function_call"; break;
            case "empty": wire.Audio = []; break;
            case "odd": wire.Audio = new byte[3]; break;
            case "oversize": wire.Audio = new byte[VoiceCatalog.MaxPcmBytes + 2]; break;
            case "service": wire.ServiceError = true; break;
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RealtimeVoicePreviewSource(() => wire).GenerateAsync("marin", "synthetic-key", default));
        Assert.DoesNotContain("sensitive", error.Message); Assert.Equal(1, wire.Disposals);
        if (failure == "voice") Assert.Single(wire.Sent);
    }
    [Fact]
    public async Task SourceCancellationClosesSocket()
    {
        var wire = new Wire { WaitForever = true }; using var cancel = new CancellationTokenSource();
        var work = new RealtimeVoicePreviewSource(() => wire).GenerateAsync("marin", "synthetic-key", cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(1, wire.Disposals);
    }
    [Theory]
    [InlineData(CombatCalloutKind.ShieldsLost)]
    [InlineData(CombatCalloutKind.LowHull)]
    [InlineData(CombatCalloutKind.CriticalHull)]
    [InlineData(CombatCalloutKind.HeatDamage)]
    [InlineData(CombatCalloutKind.Overheating)]
    public async Task CalloutsUseSelectedVoiceFixedPhraseAndNoConversationOrTools(CombatCalloutKind kind)
    {
        var wire = new Wire { ConfirmedVoice = "cedar", Transcript = CombatCalloutLines.Text(kind) };
        var audio = await new RealtimeCombatCalloutSource(() => wire).GenerateAsync(kind, "cedar", "synthetic-key", default);
        Assert.Equal(wire.Audio, audio); Assert.Equal(RealtimeProtocol.Model, wire.Model);
        var config = wire.Sent[0].GetProperty("session");
        Assert.Equal("cedar", config.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
        Assert.Empty(config.GetProperty("tools").EnumerateArray());
        var response = wire.Sent[1].GetProperty("response");
        Assert.Equal("none", response.GetProperty("conversation").GetString());
        Assert.Equal("Say exactly: " + CombatCalloutLines.Text(kind),
            response.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(2, wire.Sent.Count); Assert.Equal(1, wire.Disposals);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("Shields up.")]
    [InlineData("Shields down. Turn around and fire.")]
    public async Task OffScriptOrUnverifiedCalloutAudioIsRejected(string? transcript)
    {
        var wire = new Wire { Transcript = transcript };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RealtimeCombatCalloutSource(() => wire)
            .GenerateAsync(CombatCalloutKind.ShieldsLost, "marin", "synthetic-key", default));
        Assert.Equal(1, wire.Disposals);
    }
}
