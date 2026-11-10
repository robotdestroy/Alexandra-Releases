using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class RealtimeReplyBufferTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static VoiceFrame Frame(string id = "a") => new(1, id, new byte[48]);

    [Fact]
    public void KnownFinalAnswerStreamsImmediately()
    {
        var buffer = new RealtimeReplyBuffer();
        buffer.Observe(Json("""{"id":"a","phase":"final_answer"}"""));
        Assert.Single(buffer.Audio(Frame()).Audio);
        Assert.Single(buffer.Transcript(1, "a", "One ton.").Transcripts);
        Assert.Empty(buffer.Complete(Json("[]")).Audio);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommentaryIsSuppressedWhetherItsPhaseArrivesEarlyOrLate(bool early)
    {
        var buffer = new RealtimeReplyBuffer();
        var item = Json("""{"id":"a","phase":"commentary"}""");
        if (early) buffer.Observe(item);
        Assert.Empty(buffer.Audio(Frame()).Audio);
        Assert.Empty(buffer.Transcript(1, "a", "Let me check.").Transcripts);
        Assert.Empty(buffer.Observe(item).Audio);
        var finished = buffer.Complete(Json("[]"));
        Assert.Empty(finished.Audio); Assert.Empty(finished.Transcripts);
    }
    [Fact]
    public void LateFinalPhaseReleasesBufferedAudioExactlyOnce()
    {
        var buffer = new RealtimeReplyBuffer();
        buffer.Audio(Frame()); buffer.Audio(Frame()); buffer.Transcript(1, "a", "One ton.");
        var item = Json("""{"id":"a","phase":"final_answer"}""");
        var ready = buffer.Observe(item);
        Assert.Equal(2, ready.Audio.Count); Assert.Single(ready.Transcripts);
        Assert.Empty(buffer.Observe(item).Audio);
        Assert.Empty(buffer.Complete(Json("[]")).Transcripts);
    }
    [Fact]
    public void PhaseCanArriveOnlyInFinalResponseAlongsideCommentary()
    {
        var buffer = new RealtimeReplyBuffer();
        buffer.Audio(Frame("c")); buffer.Transcript(1, "c", "Checking.");
        buffer.Audio(Frame("a")); buffer.Transcript(1, "a", "One ton.");
        var result = buffer.Complete(Json("""[{"id":"c","phase":"commentary"},{"id":"a","phase":"final_answer"}]"""));
        Assert.Equal("a", Assert.Single(result.Audio).ItemId);
        Assert.Equal("One ton.", Assert.Single(result.Transcripts).Text);
    }
    [Theory]
    [InlineData("[]", 1)]
    [InlineData("[{\"type\":\"function_call\"}]", 0)]
    public void LegacyResponsesWaitUntilToolsAreFinished(string output, int expected)
    {
        var buffer = new RealtimeReplyBuffer();
        Assert.Empty(buffer.Audio(Frame()).Audio);
        buffer.Transcript(1, "a", "Text");
        var result = buffer.Complete(Json(output));
        Assert.Equal(expected, result.Audio.Count); Assert.Equal(expected, result.Transcripts.Count);
    }
    [Fact]
    public void InterruptionClearsPendingAudioAndText()
    {
        var buffer = new RealtimeReplyBuffer();
        buffer.Audio(Frame()); buffer.Transcript(1, "a", "Old answer"); buffer.Reset();
        var result = buffer.Complete(Json("[]"));
        Assert.Empty(result.Audio); Assert.Empty(result.Transcripts);
    }
    [Fact]
    public void UnknownPhaseBufferIsBounded()
    {
        var buffer = new RealtimeReplyBuffer();
        Assert.Throws<InvalidOperationException>(() => buffer.Audio(new(1, "a", new byte[8 * 1024 * 1024 + 1])));
    }
}
