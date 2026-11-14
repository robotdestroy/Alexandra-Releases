using Alexandra.Infrastructure;

namespace Alexandra.App;

internal static class AppDataPaths
{
    private static readonly UserDataStorage Storage = new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private static readonly Lazy<bool> Migration = new(() =>
    {
        Storage.MigrateLegacyData();
        return true;
    });

    public static string Root => Storage.Root;
    public static string SessionLogs => Storage.SessionLogs;
    public static string Memory => Storage.Memory;
    public static string Diagnostics => Storage.Diagnostics;
    public static void Initialize() => _ = Migration.Value;
}
