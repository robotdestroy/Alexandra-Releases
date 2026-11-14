using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class SessionLogCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alex-log-catalog-" + Guid.NewGuid().ToString("N"));
    private const string Id = "ABCDEF0123456789ABCDEF01";
    public SessionLogCatalogTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private string Write(string name, string text = "# Synthetic session")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void MissingOrEmptyFolderReturnsNothingWithoutCreatingIt()
    {
        Assert.Null(SessionLogCatalog.FindMostRecent(_root));
        var missing = Path.Combine(_root, "missing");
        Assert.Null(SessionLogCatalog.FindMostRecent(missing));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void ChoosesNewestSessionRatherThanMostRecentlyEditedFile()
    {
        var older = Write($"Session-2026-11-11-220000-{Id}.md");
        var newer = Write($"Session-2026-11-12-000100-{Id}.md");
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 12, 1));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 11, 12));
        Assert.Equal(newer, SessionLogCatalog.FindMostRecent(_root));
        Assert.Equal(new[] { newer, older }, SessionLogCatalog.List(_root));
    }

    [Fact]
    public void IncludesAnInProgressSession()
    {
        Write($"Session-2026-11-11-220000-{Id}.md", "Status: Game shutdown recorded");
        var current = Write($"Session-2026-11-12-000100-{Id}.md", "Status: Session in progress");
        Assert.Equal(current, SessionLogCatalog.FindMostRecent(_root));
    }

    [Theory]
    [InlineData("Session-2099-01-01-000000-ABCDEF0123456789ABCDEF01.md.tmp")]
    [InlineData("Session-2099-01-01-000000-ABCDEF0123456789ABCDEF01.md.20261112-000000.bak")]
    [InlineData("Session-2099-01-01-000000-ABCDEF0123456789ABCDEF01.md.bak")]
    [InlineData("Session-2099-13-01-000000-ABCDEF0123456789ABCDEF01.md")]
    [InlineData("Session-2099-02-30-000000-ABCDEF0123456789ABCDEF01.md")]
    [InlineData("Session-2099-01-01-250000-ABCDEF0123456789ABCDEF01.md")]
    [InlineData("Session-2099-01-01-000000-invalid.md")]
    [InlineData("notes.md")]
    public void IgnoresBackupsTemporaryFilesAndUnrelatedMarkdown(string name)
    {
        Write(name);
        var actual = Write($"Session-2026-11-12-000100-{Id}.md");
        Assert.Equal(actual, SessionLogCatalog.FindMostRecent(_root));
    }

    [Fact]
    public void DoesNotSearchNestedRecoveryFolders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "backups"));
        Write($"backups/Session-2099-01-01-000000-{Id}.md");
        Assert.Null(SessionLogCatalog.FindMostRecent(_root));
    }

    [Fact]
    public void UppercaseExtensionAndTiedSessionTimesAreDeterministic()
    {
        Write("Session-2026-11-12-000100-000000000000000000000001.md");
        var chosen = Write("Session-2026-11-12-000100-000000000000000000000002.MD");
        Assert.Equal(chosen, SessionLogCatalog.FindMostRecent(_root));
    }
}
