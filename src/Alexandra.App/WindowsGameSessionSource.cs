using System.Diagnostics;
using Alexandra.Core;

namespace Alexandra.App;

public sealed class WindowsGameSessionSource : IGameSessionSource
{
    public DateTimeOffset? StartedAt
    {
        get
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName("EliteDangerous64"); }
            catch (Exception) { return null; }
            try
            {
                using var current = Process.GetCurrentProcess();
                var running = processes.Where(p => !p.HasExited && p.SessionId == current.SessionId).ToArray();
                return running.Length == 1 ? new DateTimeOffset(running[0].StartTime.ToUniversalTime()) : null;
            }
            catch (Exception) { return null; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
}
