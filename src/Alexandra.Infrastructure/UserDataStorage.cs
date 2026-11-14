using System.Security.Cryptography;

namespace Alexandra.Infrastructure;

/// <summary>Personal data must never live in Velopack's replaceable installation directory.</summary>
public sealed class UserDataStorage
{
    public const string DirectoryName = "AlexandraData";
    private const string MigrationMarker = ".legacy-migration-v1.complete";
    private static readonly string[] Files = ["settings.json", "openai.key"];
    private static readonly string[] Directories = ["Memory", "Session Logs", "Logs"];
    private readonly string _localAppData;
    public string Root { get; }
    public string SessionLogs => Path.Combine(Root, "Session Logs");
    public string Memory => Path.Combine(Root, "Memory");
    public string Diagnostics => Path.Combine(Root, "Logs");

    public UserDataStorage(string localAppData)
    {
        _localAppData = Path.GetFullPath(localAppData);
        Root = Path.Combine(_localAppData, DirectoryName);
    }

    public void MigrateLegacyData()
    {
        RejectLink(Root);
        Directory.CreateDirectory(Root);
        // The headless watcher and the main app can start together. No process may
        // read half-migrated settings or mark another process's migration complete.
        using var migrationLock = AcquireLock();
        var marker = Path.Combine(Root, MigrationMarker);
        RejectLink(marker);
        if (File.Exists(marker)) return;

        foreach (var source in LegacySources())
        {
            RejectLink(source);
            foreach (var file in Files)
                CopyFile(source, file);
            foreach (var directory in Directories)
                CopyDirectory(source, directory);
        }
        // Written last; failed copies are retried, and later deletions are not
        // silently resurrected from an old installation on every app launch.
        File.WriteAllText(marker, "Personal data migrated outside the installation directory.\n");
    }

    private IEnumerable<string> LegacySources()
    {
        var original = Path.Combine(_localAppData, "Alexandra");
        if (Directory.Exists(original)) yield return original;

        // A fresh Setup.exe renames an existing install before invoking the new
        // app's install hook, then deletes that rollback folder. Rescue only the
        // known user-data entries, during that hook, before cleanup can remove them.
        foreach (var rollback in Directory.EnumerateDirectories(_localAppData, "Alexandra.*")
            .Where(path => IsRollbackName(Path.GetFileName(path)))
            .OrderByDescending(Directory.GetLastWriteTimeUtc))
            yield return rollback;
    }

    private static bool IsRollbackName(string name) =>
        name.Length == "Alexandra.".Length + 16 &&
        name["Alexandra.".Length..].All(char.IsAsciiLetterOrDigit);

    private void CopyDirectory(string source, string relative)
    {
        var directory = Path.Combine(source, relative);
        RejectLink(directory);
        if (!Directory.Exists(directory)) return;
        EnsureDestinationDirectory(relative);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(entry);
            var child = Path.Combine(relative, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(source, child);
            else CopyFile(source, child);
        }
    }

    private void CopyFile(string source, string relative)
    {
        var original = Path.Combine(source, relative);
        RejectLink(original);
        if (!File.Exists(original)) return;
        var destination = Path.Combine(Root, relative);
        EnsureDestinationDirectory(Path.GetDirectoryName(relative));
        RejectLink(destination);
        if (File.Exists(destination))
        {
            // Current preferences win. Keep a conflicting old file for recovery
            // rather than overwriting it or discarding either version.
            var hash = Hash(original);
            if (hash == Hash(destination)) return;
            var backupRelative = Path.Combine("Migration Backups", Path.GetFileName(source), relative + "." + hash + ".bak");
            destination = Path.Combine(Root, backupRelative);
            EnsureDestinationDirectory(Path.GetDirectoryName(backupRelative));
            RejectLink(destination);
            if (File.Exists(destination))
            {
                if (Hash(destination) != hash) throw new IOException("A migration backup could not be verified.");
                return;
            }
        }

        // Stage beside the destination; a failed/interrupted copy must not leave
        // a partial settings, memory or credential file that looks complete.
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".migrating";
        try
        {
            File.Copy(original, temporary, overwrite: false);
            if (Hash(original) != Hash(temporary)) throw new IOException("A user-data file changed during migration. Please close older copies of Alexandra and retry.");
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void EnsureDestinationDirectory(string? relative)
    {
        var path = Root;
        foreach (var part in (relative ?? "").Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            path = Path.Combine(path, part);
            RejectLink(path);
            Directory.CreateDirectory(path);
        }
    }

    private FileStream AcquireLock()
    {
        var path = Path.Combine(Root, ".migration.lock");
        RejectLink(path);
        var start = Environment.TickCount64;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 - start < 10_000) { Thread.Sleep(25); }
        }
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Automatic data migration cannot follow a linked folder or file. Your original data was not changed.");
    }
}
