using System.IO;
using Alexandra.App;
using Alexandra.Core;

internal static class AutoStartProbe
{
    public static void Run(Action<bool, string> check)
    {
        const string executable = @"C:\Program Files\Alexandra\current\Alexandra.App.exe";
        var registration = new Registration();
        var watcher = new Watcher();
        var service = new GameAutoStartService(executable, registration, watcher);
        check(!new Settings().OpenWithElite, "auto-open is opt-in for new and existing profiles");
        check(!System.Text.Json.JsonSerializer.Deserialize<Settings>("{}")!.OpenWithElite,
            "old profiles do not silently gain a Windows startup entry");
        service.Apply(true);
        check(registration.Command == "\"" + executable + "\" --watch-elite" && watcher.Starts == 1,
            "startup command quotes paths and launches only the headless watcher");
        var writes = registration.Writes;
        service.Apply(true);
        check(registration.Writes == writes && watcher.Starts == 2,
            "startup reconciliation does not rewrite an unchanged Run entry");
        service.Apply(false);
        check(registration.Command is null && watcher.Stops >= 2,
            "opting out removes only the app registration and stops its watcher");
        registration.Command = "old registration";
        watcher.FailStart = true;
        try { service.Apply(true); check(false, "launch failure should surface"); }
        catch (IOException) { check(registration.Command == "old registration", "failed helper launch restores prior startup registration"); }
        watcher.FailStart = false;
        foreach (var invalid in new[] { "relative.exe", "C:\\bad\"path.exe", "C:\\folder\\wrong.dll", "C:\\" + new string('x', 260) + ".exe" })
        {
            try { _ = GameAutoStartService.StartupCommand(invalid); check(false, "invalid startup path rejected"); }
            catch (InvalidOperationException) { check(true, "invalid or overlong startup path rejected"); }
        }
        var directory = Path.Combine(Path.GetTempPath(), "Alexandra-AutoStartProbe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(directory);
            store.Value.OpenWithElite = true;
            store.Save();
            check(new SettingsStore(directory).Value.OpenWithElite, "auto-open preference survives restarts");
            store.Value.OpenWithElite = false;
            store.Save();
            check(!new SettingsStore(directory).Value.OpenWithElite, "auto-open opt-out survives restarts");
            var saveService = new SaveService(() => check(new SettingsStore(directory).Value.OpenWithElite,
                "opt-in is persisted before the helper starts"));
            GameAutoStartPreference.Save(store, saveService, true);
            check(saveService.Calls == 1 && new SettingsStore(directory).Value.OpenWithElite,
                "Save applies the auto-open preference and starts its service");
            var failedSave = new SaveService(() => throw new IOException("synthetic registration failure"));
            try { GameAutoStartPreference.Save(store, failedSave, false); check(false, "registration failure must surface"); }
            catch (IOException)
            {
                check(store.Value.OpenWithElite && new SettingsStore(directory).Value.OpenWithElite,
                    "failed auto-open change restores the in-memory and saved preference");
            }
        }
        finally
        {
            var file = Path.Combine(directory, "settings.json");
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private sealed class Registration : IStartupRegistration
    {
        public string? Command { get; set; }
        public int Writes { get; private set; }
        public string? Read() => Command;
        public void Write(string? command) { Writes++; Command = command; }
    }
    private sealed class Watcher : IGameWatcherHost
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool FailStart { get; set; }
        public void EnsureStarted(string executable)
        {
            Starts++;
            if (FailStart) throw new IOException("synthetic process start failure");
        }
        public void Stop() => Stops++;
    }
    private sealed class SaveService(Action apply) : IGameAutoStartService
    {
        public int Calls { get; private set; }
        public void Apply(bool enabled) { Calls++; apply(); }
    }
}
