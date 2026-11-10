using Velopack;
using Velopack.Locators;
using Velopack.Sources;

// Exercises the real updater without installing, launching, or modifying the user's app/preferences.
if (args.Length != 4) throw new ArgumentException("Expected feed, current version, target version, and scratch directory.");
var feed = args[0];
var scratch = Path.GetFullPath(args[3]);
if (Directory.Exists(scratch)) throw new InvalidOperationException("Use a fresh scratch directory.");
Directory.CreateDirectory(scratch);
IUpdateSource source = Uri.TryCreate(feed, UriKind.Absolute, out var uri) && uri.Scheme == "https"
    ? new GithubSource(feed, accessToken: null, prerelease: false)
    : new SimpleFileSource(new DirectoryInfo(Path.GetFullPath(feed)));
var locator = new TestVelopackLocator("Alexandra", args[1], Path.Combine(scratch, "packages"));
var manager = new UpdateManager(source, locator: locator);
if (!manager.IsInstalled) throw new InvalidOperationException("Test installation was not recognized.");
var available = await manager.CheckForUpdatesAsync();
if (available?.TargetFullRelease.Version.ToString() != args[2]) throw new InvalidOperationException("Expected release was not discovered.");
Console.WriteLine($"Discovered {args[2]} from {args[1]}.");
await manager.DownloadUpdatesAsync(available);
if (manager.UpdatePendingRestart?.Version.ToString() != args[2]) throw new InvalidOperationException("Verified package was not staged.");
Console.WriteLine("Package downloaded and verified; pending restart recognized. No app was launched or restarted.");
var currentLocator = new TestVelopackLocator("Alexandra", args[2], Path.Combine(scratch, "current-packages"));
var current = new UpdateManager(source, locator: currentLocator);
if (await current.CheckForUpdatesAsync() is not null) throw new InvalidOperationException("Current version should not update to itself.");
Console.WriteLine("Current-version check passed.");
