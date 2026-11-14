using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Alexandra.App;

// A packaged parent (e.g. a development launcher) can virtualize LocalAppData.
// Explorer is outside that package and cannot resolve the same apparent path.
// Obtain the physical location from the directory handle before crossing that boundary.
internal static class SessionLogFiles
{
    public static string ResolveFolder(string directory)
    {
        var path = Path.GetFullPath(directory);
        Directory.CreateDirectory(path);
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new StringBuilder(32768);
        var size = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (size >= buffer.Capacity) throw new IOException("The session-log folder path is too long.");
        var physical = buffer.ToString();
        return physical.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + physical[8..] :
            physical.StartsWith(@"\\?\", StringComparison.Ordinal) ? physical[4..] : physical;
    }
    public static void OpenFolder(string directory)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            { UseShellExecute = false };
        start.ArgumentList.Add(ResolveFolder(directory));
        Process.Start(start)?.Dispose(); // Do not block voice/UI on Explorer's message loop.
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
