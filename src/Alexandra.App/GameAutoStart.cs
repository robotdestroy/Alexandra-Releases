using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using Alexandra.Core;
using Microsoft.Win32;

namespace Alexandra.App;

public interface IStartupRegistration
{
    string? Read();
    void Write(string? command);
}

public interface IGameWatcherHost
{
    void EnsureStarted(string executable);
    void Stop();
}

public sealed class GameAutoStartService : IGameAutoStartService
{
    public const string WatchArgument = "--watch-elite";
    private readonly string _executable;
    private readonly IStartupRegistration _registration;
    private readonly IGameWatcherHost _watcher;

    public GameAutoStartService(string? executable = null, IStartupRegistration? registration = null,
        IGameWatcherHost? watcher = null)
    {
        _executable = executable ?? Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        _registration = registration ?? new WindowsStartupRegistration();
        _watcher = watcher ?? new GameWatcherHost();
    }

    public static string StartupCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"', '\r', '\n']) >= 0 ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Auto-open needs a valid Alexandra application path.");
        var command = $"\"{executable}\" {WatchArgument}";
        if (command.Length > 260)
            throw new InvalidOperationException("Install Alexandra in a shorter folder path to enable auto-open.");
        return command;
    }

    public void Apply(bool enabled)
    {
        var previous = _registration.Read();
        var command = enabled ? StartupCommand(_executable) : null;
        try
        {
            if (!string.Equals(previous, command, StringComparison.OrdinalIgnoreCase))
            {
                // Release an old version's files before pointing startup at this copy.
                _watcher.Stop();
                _registration.Write(command);
            }
            if (enabled) _watcher.EnsureStarted(_executable);
            else _watcher.Stop();
        }
        catch
        {
            try { _registration.Write(previous); } catch (Exception) { }
            throw;
        }
    }

    public static void ResumeIfEnabled()
    {
        try
        {
            if (new SettingsStore().Value.OpenWithElite) new GameAutoStartService().Apply(true);
        }
        catch (Exception) { /* An optional startup helper must never prevent the normal app opening. */ }
    }

    public static void StopForUpdate() => new GameWatcherHost().Stop();

    public static void RemoveForUninstall()
    {
        try { new GameWatcherHost().Stop(); }
        finally { new WindowsStartupRegistration().Write(null); }
    }
}

public static class GameAutoStartPreference
{
    public static void Save(SettingsStore store, IGameAutoStartService service, bool enabled)
    {
        var previous = store.Value.OpenWithElite;
        try
        {
            store.Value.OpenWithElite = enabled;
            store.Save(); // The headless process must see the opt-in before it starts.
            service.Apply(enabled);
        }
        catch
        {
            store.Value.OpenWithElite = previous;
            store.Save();
            throw;
        }
    }
}

public sealed class WindowsStartupRegistration : IStartupRegistration
{
    public const string ValueName = "Alexandra - open with Elite";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }
    public void Write(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (command is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, command, RegistryValueKind.String);
        // Never change Windows StartupApproved or another application's entries.
    }
}

public sealed class GameWatcherHost : IGameWatcherHost
{
    private const string MutexName = @"Local\Alexandra.EliteWatcher.SingleInstance";
    private const string StopName = @"Local\Alexandra.EliteWatcher.Stop";

    public void EnsureStarted(string executable)
    {
        if (SingleInstanceCoordinator.IsHeld(MutexName)) return;
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            Arguments = GameAutoStartService.WatchArgument,
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("The background helper could not start.");
    }

    public void Stop()
    {
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName);
        stop.Set();
        var deadline = Stopwatch.StartNew();
        while (SingleInstanceCoordinator.IsHeld(MutexName))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(4))
                throw new InvalidOperationException("The background helper is still stopping. Try again in a moment.");
            Thread.Sleep(25);
        }
    }

    public static void Run()
    {
        using var mutex = new Mutex(false, MutexName);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) return;
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName);
            stop.Reset();
            var coordinator = new GameAutoLaunchCoordinator(new WindowsGameAutoLaunchEnvironment());
            do
            {
                try
                {
                    if (coordinator.Check(DateTimeOffset.UtcNow) == GameAutoLaunchResult.Disabled) return;
                }
                catch (Exception) { /* Retry unreadable preferences/process state without UI or repeated launches. */ }
            } while (!stop.WaitOne(TimeSpan.FromSeconds(3)));
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }
}

public sealed class WindowsGameAutoLaunchEnvironment : IGameAutoLaunchEnvironment
{
    public bool Enabled => new SettingsStore().Value.OpenWithElite;
    public bool CompanionRunning => SingleInstanceCoordinator.IsRunning;

    public bool TryGetGameSession(out string? session)
    {
        session = null;
        var unknown = false;
        Process[] processes;
        try { processes = Process.GetProcessesByName("EliteDangerous64"); }
        catch (Exception) { return false; }
        using var current = Process.GetCurrentProcess();
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.SessionId == current.SessionId)
                    {
                        var identity = $"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}";
                        if (session is null || string.CompareOrdinal(identity, session) < 0) session = identity;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or SecurityException)
                { unknown = true; }
            }
        }
        return session is not null || !unknown;
    }

    public void LaunchCompanion()
    {
        var executable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            // A racing/manual launch must not pull focus away from the game.
            Arguments = "--auto-open",
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Alexandra could not open.");
    }
}
