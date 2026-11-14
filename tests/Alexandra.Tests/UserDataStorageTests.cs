using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class UserDataStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alex-data-tests-" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_root, "Alexandra");
    private UserDataStorage Storage => new(_root);
    private const string Marker = ".legacy-migration-v1.complete";

    public UserDataStorageTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void DataAndInstallationAreSeparateSiblingFolders()
    {
        Assert.Equal(Path.Combine(_root, "AlexandraData"), Storage.Root);
        Assert.False(Storage.Root.StartsWith(Legacy + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(Storage.Root, "Session Logs"), Storage.SessionLogs);
    }

    [Fact]
    public void NewInstallCreatesNoPersonalContentBeyondMigrationBookkeeping()
    {
        Storage.MigrateLegacyData();
        Assert.True(File.Exists(Path.Combine(Storage.Root, Marker)));
        Assert.False(Directory.Exists(Storage.SessionLogs));
        Assert.False(Directory.Exists(Legacy));
    }

    [Fact]
    public void MigratesOnlyKnownDataAndPreservesOriginalsAndEncryptedBytes()
    {
        Write(Legacy, "settings.json", "{\"SessionLogsEnabled\":true}");
        Write(Legacy, "openai.key", "synthetic-encrypted-bytes-not-a-real-key");
        Write(Legacy, "Memory/memory-test.json", "synthetic memory");
        Write(Legacy, "Session Logs/Session-test.md", "# Synthetic session");
        Write(Legacy, "Session Logs/Session-test.md.backup.bak", "previous synthetic session");
        Write(Legacy, "Logs/session-test.jsonl", "synthetic diagnostics");
        Write(Legacy, "current/app.dll", "app code");
        Write(Legacy, "packages/package.nupkg", "package");
        Write(Legacy, "unrelated.txt", "not user data");
        Storage.MigrateLegacyData();
        foreach (var relative in new[] { "settings.json", "openai.key", "Memory/memory-test.json", "Session Logs/Session-test.md", "Session Logs/Session-test.md.backup.bak", "Logs/session-test.jsonl" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(Legacy, relative)), File.ReadAllBytes(Path.Combine(Storage.Root, relative)));
        Assert.False(Directory.Exists(Path.Combine(Storage.Root, "current")));
        Assert.False(Directory.Exists(Path.Combine(Storage.Root, "packages")));
        Assert.False(File.Exists(Path.Combine(Storage.Root, "unrelated.txt")));
    }

    [Fact]
    public void FreshInstallerHookRescuesRenamedDataBeforeRollbackCleanup()
    {
        Write(Legacy, "Session Logs/Session-test.md", "personal recap");
        Write(Legacy, "Memory/memory-test.json", "mission state");
        var rollback = Path.Combine(_root, "Alexandra.A1b2C3d4E5f6G7h8");
        Directory.Move(Legacy, rollback); // Real Setup.exe behavior.
        Write(Legacy, "current/app.dll", "new version");
        Storage.MigrateLegacyData(); // --veloapp-install hook.
        Directory.Delete(rollback, recursive: true); // Installer cleans up after hook.
        Assert.Equal("personal recap", File.ReadAllText(Path.Combine(Storage.SessionLogs, "Session-test.md")));
        Assert.Equal("mission state", File.ReadAllText(Path.Combine(Storage.Memory, "memory-test.json")));
    }

    [Fact]
    public void ReinstallAndUninstallCannotRemoveMigratedData()
    {
        Write(Legacy, "Session Logs/Session-test.md", "keep me");
        Write(Legacy, "settings.json", "{\"Voice\":\"marin\"}");
        Storage.MigrateLegacyData();
        Directory.Delete(Legacy, recursive: true);
        Write(Legacy, "current/app.dll", "replacement install");
        Storage.MigrateLegacyData();
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(Storage.SessionLogs, "Session-test.md")));
        Assert.Equal("{\"Voice\":\"marin\"}", File.ReadAllText(Path.Combine(Storage.Root, "settings.json")));
    }

    [Fact]
    public void ExistingNewPreferencesWinAndConflictsHaveRecoveryCopies()
    {
        Write(Legacy, "settings.json", "old preferences");
        Write(Storage.Root, "settings.json", "new preferences");
        Write(Legacy, "Session Logs/Session-test.md", "old recap");
        Write(Storage.Root, "Session Logs/Session-test.md", "edited recap");
        Storage.MigrateLegacyData();
        Assert.Equal("new preferences", File.ReadAllText(Path.Combine(Storage.Root, "settings.json")));
        Assert.Equal("edited recap", File.ReadAllText(Path.Combine(Storage.SessionLogs, "Session-test.md")));
        var backups = Directory.GetFiles(Path.Combine(Storage.Root, "Migration Backups"), "*", SearchOption.AllDirectories);
        Assert.Equal(2, backups.Length);
        Assert.Contains(backups, path => File.ReadAllText(path) == "old preferences");
        Assert.Contains(backups, path => File.ReadAllText(path) == "old recap");
    }

    [Fact]
    public void SuccessfulMigrationDoesNotResurrectDeletedData()
    {
        Write(Legacy, "Session Logs/Session-test.md", "user later deletes this");
        Storage.MigrateLegacyData();
        File.Delete(Path.Combine(Storage.SessionLogs, "Session-test.md"));
        Storage.MigrateLegacyData();
        Assert.False(File.Exists(Path.Combine(Storage.SessionLogs, "Session-test.md")));
    }

    [Fact]
    public void FailedMigrationLeavesSourceAndRetriesWithoutPartialDestination()
    {
        Write(Legacy, "settings.json", "synthetic preferences");
        Directory.CreateDirectory(Path.Combine(Storage.Root, "settings.json")); // Cannot replace directory with file.
        Assert.ThrowsAny<IOException>(() => Storage.MigrateLegacyData());
        Assert.False(File.Exists(Path.Combine(Storage.Root, Marker)));
        Assert.Empty(Directory.GetFiles(Storage.Root, "*.migrating", SearchOption.AllDirectories));
        Assert.Equal("synthetic preferences", File.ReadAllText(Path.Combine(Legacy, "settings.json")));
        Directory.Delete(Path.Combine(Storage.Root, "settings.json"));
        Storage.MigrateLegacyData();
        Assert.Equal("synthetic preferences", File.ReadAllText(Path.Combine(Storage.Root, "settings.json")));
    }

    [Fact]
    public void IdenticalExistingFilesDoNotCreateRedundantBackups()
    {
        Write(Legacy, "settings.json", "same data");
        Write(Storage.Root, "settings.json", "same data");
        Storage.MigrateLegacyData();
        Assert.False(Directory.Exists(Path.Combine(Storage.Root, "Migration Backups")));
    }

    [Theory]
    [InlineData("Alexandra.OtherApp")]
    [InlineData("Alexandra.123")]
    [InlineData("Alexandra.123456789012345-")]
    public void DoesNotImportUnrelatedSiblingFolders(string name)
    {
        Write(Path.Combine(_root, name), "settings.json", "unrelated data");
        Storage.MigrateLegacyData();
        Assert.False(File.Exists(Path.Combine(Storage.Root, "settings.json")));
    }

    [Fact]
    public async Task ConcurrentWatcherAndAppBothSeeCompleteMigration()
    {
        Write(Legacy, "settings.json", "synthetic preferences");
        Write(Legacy, "Session Logs/Session-test.md", "synthetic recap");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Storage.MigrateLegacyData())));
        Assert.Equal("synthetic preferences", File.ReadAllText(Path.Combine(Storage.Root, "settings.json")));
        Assert.Equal("synthetic recap", File.ReadAllText(Path.Combine(Storage.SessionLogs, "Session-test.md")));
    }
}
