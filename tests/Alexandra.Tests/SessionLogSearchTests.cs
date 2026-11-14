using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class SessionLogSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alex-search-" + Guid.NewGuid().ToString("N"));
    public SessionLogSearchTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static string Log(string ship, string body = "") => $"# Gameplay session — 2026-11-13 10:00 UTC\n\n- Ship: {ship}\n\n## Summary\n{body}\n";
    private string Write(string name, string text) { var path = Path.Combine(_root, name + ".md"); File.WriteAllText(path, text); return path; }

    [Theory]
    [InlineData("corvette")]
    [InlineData("  CORVETTE  ")]
    [InlineData("Federal Corvette")]
    [InlineData("federation_corvette")]
    public void FindsShipUsageIncludingLegacyEscapedNames(string query)
    {
        Assert.True(SessionLogSearch.Matches(Log(@"federation\_corvette"), query));
        Assert.True(SessionLogSearch.Matches(Log("Federal Corvette"), query));
        Assert.False(SessionLogSearch.Matches(Log("Mandalay", "My goal is to buy a Federal Corvette."), query));
    }

    [Fact]
    public void FindsAnyShipUsedInASessionNotOnlyItsLastShip()
    {
        var log = new GameplaySession { Ship = @"federation\_corvette" };
        log.RecordShip("mandalay");
        log.RecordShip("Mandalay");
        Assert.Equal(new[] { "Federal Corvette", "Mandalay" }, log.ShipsUsed);
        var markdown = log.Render().Markdown;
        Assert.Contains("- Ship: Mandalay", markdown);
        Assert.True(SessionLogSearch.Matches(markdown, "Corvette"));
        Assert.True(SessionLogSearch.Matches(markdown, "Mandalay"));
        Assert.False(SessionLogSearch.Matches(markdown, "Python"));
    }

    [Theory]
    [InlineData("exphiay", true)]
    [InlineData("EXPHIAY combat", true)]
    [InlineData("combat exphiay", true)]
    [InlineData("2026-11-13", true)]
    [InlineData("meta alloys", true)]
    [InlineData("exphiay mining", false)]
    [InlineData("dec iat", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public void SearchesContentCaseInsensitivelyWithAllWordsRequired(string query, bool expected)
        => Assert.Equal(expected, SessionLogSearch.Matches(Log("Mandalay", "Combat in **Exphiay**. Collected Meta-Alloys."), query));

    [Fact]
    public void FindsPhotoCaptionsButNotPaths()
    {
        var markdown = Log("Mandalay", "First landing — [View photo](<file:///C:/private-folder/exploration-photo.png>)");
        Assert.True(SessionLogSearch.Matches(markdown, "first landing"));
        Assert.False(SessionLogSearch.Matches(markdown, "private folder"));
    }

    [Fact]
    public async Task PreservesCatalogOrderAndDoesNotModifyFiles()
    {
        var a = Write("newer", Log("Mandalay"));
        var b = Write("middle", Log(@"federation\_corvette", "Combat"));
        var c = Write("older", Log("Federal Corvette"));
        var original = File.ReadAllText(b);
        var result = await SessionLogSearch.FilterAsync([a, b, c], "Corvette", default);
        Assert.Equal(new[] { b, c }, result.Paths); Assert.Equal(0, result.UnreadableCount);
        Assert.Equal(original, File.ReadAllText(b));
        Assert.Equal(new[] { a, b, c }, (await SessionLogSearch.FilterAsync([a, b, c], "", default)).Paths);
    }

    [Fact]
    public async Task RefreshSeesNewContentAndMissingOrLockedFilesDoNotBreakOtherResults()
    {
        var a = Write("good", Log("Mandalay"));
        var locked = Write("locked", Log("Federal Corvette"));
        var missing = Path.Combine(_root, "missing.md");
        using var exclusive = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var first = await SessionLogSearch.FilterAsync([a, locked, missing], "Corvette", default);
        Assert.Empty(first.Paths); Assert.Equal(2, first.UnreadableCount);
        File.WriteAllText(a, Log("Federal Corvette"));
        Assert.Equal(new[] { a }, (await SessionLogSearch.FilterAsync([a], "Corvette", default)).Paths);
    }

    [Fact]
    public async Task SkipsOversizedFilesAndSupportsCancellation()
    {
        var path = Write("large", "");
        using (var file = File.OpenWrite(path)) file.SetLength(SessionLogSearch.MaximumFileBytes + 1);
        var result = await SessionLogSearch.FilterAsync([path], "Corvette", default);
        Assert.Empty(result.Paths); Assert.Equal(1, result.UnreadableCount);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionLogSearch.FilterAsync([path], "Corvette", cancellation.Token));
    }
}
