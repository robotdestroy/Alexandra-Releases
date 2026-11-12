using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class JournalFileCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alex-journal-order-" + Guid.NewGuid().ToString("N"));
    public JournalFileCatalogTests() => Directory.CreateDirectory(_root);
    private string Write(string name, string at, int part = 1)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, $$"""{"timestamp":"{{at}}","event":"Fileheader","part":{{part}}}""" + "\n");
        return path;
    }
    [Fact]
    public void MixedLegacyModernAndPartsUseHeaderChronologyNotNameOrCopiedModificationTime()
    {
        var modern = Write("Journal.2026-11-11T100000.01.log", "2026-11-11T10:00:00Z");
        var legacy = Write("Journal.221031100000.01.log", "2022-10-31T10:00:00Z");
        File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddDays(1));
        var part10 = Write("Journal.2026-11-11T100000.10.log", "2026-11-11T10:00:00Z", 10);
        var part2 = Write("Journal.2026-11-11T100000.2.log", "2026-11-11T10:00:00Z", 2);
        Assert.Equal(new[] { legacy, modern, part2, part10 }, new JournalFileCatalog().GetFiles(_root));
    }
    [Fact]
    public void HeaderBeatsMisleadingFilenameAndCacheNoticesReplacement()
    {
        var a = Write("Journal.2026-11-11T100000.01.log", "2026-11-11T10:00:00Z");
        var b = Write("Journal.221031100000.01.log", "2026-11-12T10:00:00Z");
        var catalog = new JournalFileCatalog();
        Assert.Equal(new[] { a, b }, catalog.GetFiles(_root));
        Write(Path.GetFileName(b), "2022-10-31T10:00:00Z");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(new[] { b, a }, catalog.GetFiles(_root));
    }
    [Fact]
    public void PartialHeaderFallsBackToNormalizedFilenameThenIsReadWhenCompleted()
    {
        var legacy = Path.Combine(_root, "Journal.221031100000.01.log");
        File.WriteAllText(legacy, "{\"timestamp\":");
        var modern = Write("Journal.2026-11-11T100000.01.log", "2026-11-11T10:00:00Z");
        var catalog = new JournalFileCatalog();
        Assert.Equal(new[] { legacy, modern }, catalog.GetFiles(_root));
        Write(Path.GetFileName(legacy), "2026-11-12T10:00:00Z");
        Assert.Equal(new[] { modern, legacy }, catalog.GetFiles(_root));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
