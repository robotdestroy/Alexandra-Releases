using Alexandra.Core;
using Xunit;

namespace Alexandra.Tests;

public sealed class DestinationCopyTests
{
    [Fact]
    public async Task ExplicitCopyValidatesCanonicalSystemWithoutResearch()
    {
        var resolver = new Resolver((_, _) => Task.FromResult<string?>("Sol"));
        var clipboard = new Clipboard();
        var result = await new DestinationCopyService(resolver, clipboard).CopySystemAsync("sol", default);
        Assert.True(result.ClipboardCopied);
        Assert.Equal("Sol", result.CopiedSystem);
        Assert.Equal("copied", result.ClipboardStatus);
        Assert.Equal("Copied Sol.", result.ClipboardNote);
        Assert.Equal("Sol", clipboard.Value);
        Assert.Equal("sol", resolver.Candidate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Sol\nColonia")]
    [InlineData("Sol, Colonia")]
    [InlineData("https://inara.cz/elite/station/1/")]
    public async Task InvalidCandidatesNeverReachResolverOrClipboard(string? candidate)
    {
        var resolver = new Resolver((_, _) => throw new Exception("Must not resolve"));
        var clipboard = new Clipboard();
        var result = await new DestinationCopyService(resolver, clipboard).CopySystemAsync(candidate, default);
        Assert.Equal("invalid_system", result.ClipboardStatus);
        Assert.False(result.ClipboardCopied);
        Assert.Null(clipboard.Value);
    }

    [Theory]
    [InlineData("Test Station", null)]
    [InlineData("Copy Sol please", null)]
    [InlineData("Sol", "Seoul")]
    [InlineData("Sol", "Sol\n")]
    public async Task UnverifiedStationsProseOrSimilarMatchesNeverCopy(string candidate, string? resolved)
    {
        var clipboard = new Clipboard();
        var result = await new DestinationCopyService(new Resolver((_, _) => Task.FromResult(resolved)), clipboard)
            .CopySystemAsync(candidate, default);
        Assert.Equal("system_not_found", result.ClipboardStatus);
        Assert.False(result.ClipboardCopied);
        Assert.Null(result.CopiedSystem);
        Assert.Null(clipboard.Value);
    }

    [Fact]
    public async Task ResearchStillRequiresRetrievedSupportingEvidence()
    {
        var clipboard = new Clipboard();
        var service = new DestinationCopyService(new Resolver((_, _) => Task.FromResult<string?>("Sol")), clipboard);
        var answer = new Answer("Visit Test Port in Sol.", new("Sol", "Test Port", "https://example.com/report"), []);
        Assert.Equal("missing_evidence", (await service.CopyResearchAsync(answer, default)).ClipboardStatus);
        Assert.Null(clipboard.Value);
        var result = await service.CopyResearchAsync(answer with { Sources = [new("Report", "https://example.com/report")] }, default);
        Assert.True(result.ClipboardCopied);
        Assert.Equal("Sol", clipboard.Value); // not the station or spoken prose
        clipboard.Value = null;
        Assert.Equal("no_destination", (await service.CopyResearchAsync(answer with { Destination = null }, default)).ClipboardStatus);
        Assert.Null(clipboard.Value);
    }

    [Fact]
    public async Task ClipboardFailureIsNeverReportedAsSuccess()
    {
        var clipboard = new Clipboard { Succeeds = false };
        var result = await new DestinationCopyService(new Resolver((_, _) => Task.FromResult<string?>("Sol")), clipboard)
            .CopySystemAsync("Sol", default);
        Assert.False(result.ClipboardCopied);
        Assert.Null(result.CopiedSystem);
        Assert.Equal("clipboard_unavailable", result.ClipboardStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LookupFailuresHaveSpecificReasons(bool timeout)
    {
        var resolver = new Resolver((_, _) => timeout ? throw new OperationCanceledException() : throw new HttpRequestException());
        var clipboard = new Clipboard();
        var result = await new DestinationCopyService(resolver, clipboard).CopySystemAsync("Sol", default);
        Assert.Equal(timeout ? "lookup_timeout" : "lookup_unavailable", result.ClipboardStatus);
        Assert.Null(clipboard.Value);
    }

    [Fact]
    public async Task InterruptedLookupNeverOverwritesClipboard()
    {
        using var cancelled = new CancellationTokenSource();
        var resolver = new Resolver((_, _) => { cancelled.Cancel(); return Task.FromResult<string?>("Sol"); });
        var clipboard = new Clipboard();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DestinationCopyService(resolver, clipboard).CopySystemAsync("Sol", cancelled.Token));
        Assert.Null(clipboard.Value);
    }

    private sealed class Resolver(Func<string, CancellationToken, Task<string?>> resolve) : ISystemResolver
    {
        public string? Candidate { get; private set; }
        public Task<string?> ResolveAsync(string candidate, CancellationToken token) { Candidate = candidate; return resolve(candidate, token); }
    }
    private sealed class Clipboard : ISystemClipboard
    {
        public string? Value { get; set; }
        public bool Succeeds { get; init; } = true;
        public Task<bool> TryCopyAsync(string system, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Succeeds) Value = system;
            return Task.FromResult(Succeeds);
        }
    }
}
