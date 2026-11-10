using System.Runtime.InteropServices;

namespace Alexandra.Infrastructure;

public static class WindowsKnownFolderPaths
{
    private static readonly Guid SavedGamesFolderId =
        new("4C5C32FF-BB9D-43B0-BF5A-5C105C7E3C65");

    public static string? TryGetSavedGamesPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var folderId = SavedGamesFolderId;
        var result = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pathPointer);
        if (result != 0 || pathPointer == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(pathPointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        ref Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);
}
