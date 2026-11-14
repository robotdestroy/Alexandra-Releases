using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class SessionLogPresentationTests
{
    [Theory]
    [InlineData("federation_corvette", "Federal Corvette")]
    [InlineData(@"federation\_corvette", "Federal Corvette")]
    [InlineData("FEDERATION_CORVETTE", "Federal Corvette")]
    [InlineData("Federal Corvette", "Federal Corvette")]
    [InlineData("mandalay", "Mandalay")]
    [InlineData("federation_dropship_mkii", "Federal Assault Ship")]
    [InlineData("panthermkii", "Panther Clipper MkII")]
    [InlineData("type9_military", "Type-10 Defender")]
    [InlineData("diamondbackxl", "Diamondback Explorer")]
    [InlineData("typex_2", "Alliance Crusader")]
    [InlineData("typex_3", "Alliance Challenger")]
    [InlineData("python_nx", "Python MkII")]
    [InlineData("Corvette fédérale", "Corvette fédérale")]
    [InlineData(@"future\_ship", @"future\_ship")]
    public void UsesKnownGameNamesWithoutGuessingUnknownOrOverwritingLocalisedNames(string symbol, string expected)
        => Assert.Equal(expected, SessionLogPresentation.ShipName(symbol));

    [Fact]
    public void NewExportsUseProperShipNameAndKeepRecordedSpanWithoutDisclaimer()
    {
        var session = new GameplaySession
        {
            StartedAt = DateTimeOffset.Parse("2026-11-13T10:00:00Z"),
            LastEventAt = DateTimeOffset.Parse("2026-11-13T10:45:00Z"),
            Ship = @"federation\_corvette"
        };
        var markdown = session.Render().Markdown;
        Assert.Contains("- Ship: Federal Corvette", markdown);
        Assert.Contains("- Recorded span: 45 minutes", markdown);
        Assert.DoesNotContain("not an active-play timer", markdown);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void LegacyFormattingIsScopedIdempotentAndPreservesBodyAndLineEndings(string newline)
    {
        var header = string.Join(newline, "# Gameplay session — 2026-11-13 10:00 UTC", "",
            "- Recorded span: 45 minutes (not an active-play timer)", @"- Ship: federation\_corvette", "");
        var body = string.Join(newline, "", "## Summary", "", @"- Ship: federation\_corvette",
            "My note: (not an active-play timer)", "[View photo](<file:///C:/Pictures/test.png>)");
        var actual = SessionLogPresentation.Format(header + body);
        Assert.Equal(header.Replace(" (not an active-play timer)", "").Replace(@"federation\_corvette", "Federal Corvette") + body, actual);
        Assert.Equal(actual, SessionLogPresentation.Format(actual));
    }

    [Fact]
    public void DoesNotRewriteUnrelatedMarkdown()
    {
        const string text = "# Notes\n- Ship: federation_corvette\n- Recorded span: 45 minutes (not an active-play timer)\n## Summary\n";
        Assert.Equal(text, SessionLogPresentation.Format(text));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void HeaderExtractionKeepsEveryFieldAndExactSummaryBody(string newline)
    {
        var body = string.Join(newline, "## Summary", "", "### Session stats", "- Credits received: 1,000 CR",
            "", "My **unchanged** summary.", "## Photos", "[View photo](photo.png)");
        var markdown = string.Join(newline, "# Gameplay session — 2026-11-13 10:00 UTC", "",
            "- Status: Open — game shutdown not yet observed", "- Recorded from: 2026-11-13 10:00:00 UTC",
            "- Recorded span: 45 minutes (not an active-play timer)", @"- Ship: federation\_corvette",
            "- Ships used: Federal Corvette; Mandalay", "- Future field: Still retained", "", body);
        var header = Assert.IsType<SessionLogHeader>(SessionLogPresentation.ReadHeader(markdown));
        Assert.Equal("Gameplay session — 2026-11-13 10:00 UTC", header.Title);
        Assert.Equal(6, header.Fields.Count);
        Assert.Contains(new SessionLogHeaderField("Ship", "Federal Corvette"), header.Fields);
        Assert.Contains(new SessionLogHeaderField("Recorded span", "45 minutes"), header.Fields);
        Assert.Contains(new SessionLogHeaderField("Status", "Open — game shutdown not yet observed"), header.Fields);
        Assert.Contains(new SessionLogHeaderField("Future field", "Still retained"), header.Fields);
        Assert.Equal(body, header.Body);
    }

    [Theory]
    [InlineData("# Notes\n- Ship: Mandalay\n## Summary")]
    [InlineData("# Gameplay session — date\n- Ship: Mandalay")]
    [InlineData("# Gameplay session — date\n- Ship: Mandalay\n## Summary notes")]
    [InlineData("# Gameplay session — date\n## Summary")]
    [InlineData("# Gameplay session — date\n- Ship: \n## Summary")]
    [InlineData("# Gameplay session — date\n- Ship: Mandalay\nMy handwritten note\n## Summary")]
    [InlineData("# Gameplay session — date\n- Ship: Mandalay\n### Custom heading\n## Summary")]
    public void UnrecognisedHeadersFallBackWithoutConsumingContent(string markdown)
        => Assert.Null(SessionLogPresentation.ReadHeader(markdown));
}
