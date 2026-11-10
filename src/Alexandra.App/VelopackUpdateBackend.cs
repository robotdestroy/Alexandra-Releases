using System.Reflection;
using Alexandra.Core;
using Velopack;
using Velopack.Sources;

namespace Alexandra.App;

public sealed class VelopackUpdateBackend : IAppUpdateBackend
{
    private readonly UpdateManager? _manager;
    private UpdateInfo? _available;
    public static string AppVersion => typeof(App).Assembly.GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}.{version.Build}" : "development";
    public string CurrentVersion { get; } = AppVersion;
    public string? UnavailableReason { get; }
    public string? PendingVersion => _manager?.UpdatePendingRestart?.Version.ToString();

    public VelopackUpdateBackend()
    {
        // Publisher-supplied, public URL only. No API key, GitHub token or user-editable redirect.
        var feed = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AlexandraUpdateFeedUrl")?.Value;
        if (!UpdateFeedPolicy.IsPublicGitHubRepository(feed))
        { UnavailableReason = "This development build has no update feed. Use the friends-release installer for automatic updates."; return; }
        try
        {
            _manager = new UpdateManager(new GithubSource(feed!, accessToken: null, prerelease: false));
            CurrentVersion = _manager.CurrentVersion?.ToString() ?? AppVersion;
            if (!_manager.IsInstalled) UnavailableReason = "Automatic updates require the Alexandra Setup installer, not a development or portable copy.";
        }
        catch (Exception) { UnavailableReason = "The update service is unavailable in this copy of Alexandra."; }
    }

    public async Task<string?> FindUpdateAsync(CancellationToken token)
    {
        var available = await _manager!.CheckForUpdatesAsync().WaitAsync(token);
        token.ThrowIfCancellationRequested();
        _available = available;
        return available?.TargetFullRelease.Version.ToString();
    }
    public Task DownloadAsync(Action<int> progress, CancellationToken token) =>
        _manager!.DownloadUpdatesAsync(_available ?? throw new InvalidOperationException("No update selected."), progress, token);
    public void ScheduleApplyAndRestart()
    {
        var pending = _manager?.UpdatePendingRestart ?? throw new InvalidOperationException("No downloaded update is ready.");
        GameAutoStartService.StopForUpdate();
        try { _manager!.WaitExitThenApplyUpdates(pending, silent: false, restart: true); }
        catch { GameAutoStartService.ResumeIfEnabled(); throw; }
    }
}
