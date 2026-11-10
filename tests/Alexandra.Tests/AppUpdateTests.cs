using Alexandra.Core;
using Xunit;

namespace Alexandra.Tests;

public sealed class AppUpdateTests
{
    [Theory]
    [InlineData("https://github.com/robotdestroy/Alexandra-Releases",true)]
    [InlineData("https://github.com/owner/repo/",true)]
    [InlineData("http://github.com/owner/repo",false)]
    [InlineData("https://github.com.evil.example/owner/repo",false)]
    [InlineData("https://token@github.com/owner/repo",false)]
    [InlineData("https://github.com/owner/repo?token=secret",false)]
    [InlineData("https://github.com/owner/repo#fragment",false)]
    [InlineData("https://github.com/owner/repo/releases",false)]
    [InlineData("file:///C:/updates",false)]
    [InlineData("https://github.com:8443/owner/repo",false)]
    [InlineData(null,false)]
    public void FeedMustBeCredentialFreeHttpsRepository(string? url,bool expected) => Assert.Equal(expected,UpdateFeedPolicy.IsPublicGitHubRepository(url));

    [Fact]
    public async Task DownloadOnlyStagesUpdateUntilExplicitApply()
    {
        var backend=new FakeBackend(); var updates=new AppUpdateCoordinator(backend);
        await updates.CheckAndDownloadAsync();
        Assert.Equal(UpdatePhase.Ready,updates.State.Phase); Assert.Equal("0.1.12",updates.State.Version);
        Assert.Equal(1,backend.Downloads); Assert.Equal(0,backend.Applies);
        await updates.CheckAndDownloadAsync(); Assert.Equal(1,backend.Downloads);
        updates.ScheduleApplyAndRestart(); Assert.Equal(1,backend.Applies);
    }
    [Fact]
    public async Task MissingFeedAndUninstalledBuildsDoNotDownload()
    {
        var backend=new FakeBackend { UnavailableReason="Development build" }; var updates=new AppUpdateCoordinator(backend);
        await updates.CheckAndDownloadAsync(); Assert.Equal(UpdatePhase.Unavailable,updates.State.Phase);
        Assert.Equal(0,backend.Checks); Assert.Throws<InvalidOperationException>(updates.ScheduleApplyAndRestart);
    }
    [Fact]
    public async Task UpToDateDoesNotDownloadOrRestart()
    {
        var backend=new FakeBackend { Available=null }; var updates=new AppUpdateCoordinator(backend);
        await updates.CheckAndDownloadAsync(); Assert.Equal(UpdatePhase.Idle,updates.State.Phase); Assert.Equal(0,backend.Downloads);
    }
    [Fact]
    public async Task FailuresLeaveCurrentAppAloneAndCanRetry()
    {
        var backend=new FakeBackend { Fail=true }; var updates=new AppUpdateCoordinator(backend);
        await updates.CheckAndDownloadAsync(); Assert.Equal(UpdatePhase.Failed,updates.State.Phase); Assert.Equal(0,backend.Applies);
        backend.Fail=false; await updates.CheckAndDownloadAsync(); Assert.Equal(UpdatePhase.Ready,updates.State.Phase);
    }
    [Fact]
    public async Task ConcurrentChecksCoalesceAndCancellationDoesNotStageOrRestart()
    {
        var backend=new FakeBackend { Wait=true }; var updates=new AppUpdateCoordinator(backend);
        using var stop=new CancellationTokenSource(); var first=updates.CheckAndDownloadAsync(stop.Token);
        await updates.CheckAndDownloadAsync(); Assert.Equal(1,backend.Checks);
        stop.Cancel(); await first; Assert.Equal(UpdatePhase.Idle,updates.State.Phase);
        Assert.Equal(0,backend.Downloads); Assert.Equal(0,backend.Applies);
    }
    [Fact]
    public async Task StaleDownloadProgressCannotOverwriteReadyState()
    {
        var backend=new FakeBackend(); var updates=new AppUpdateCoordinator(backend);
        await updates.CheckAndDownloadAsync(); backend.Progress!(2);
        Assert.Equal(UpdatePhase.Ready,updates.State.Phase);
    }
    private sealed class FakeBackend : IAppUpdateBackend
    {
        public string CurrentVersion=>"0.1.11";
        public string? UnavailableReason{get;set;}
        public string? PendingVersion{get;set;}
        public string? Available="0.1.12";
        public bool Fail,Wait;
        public int Checks,Downloads,Applies;
        public Action<int>? Progress;
        public async Task<string?> FindUpdateAsync(CancellationToken token)
        {
            Checks++; if(Fail) throw new IOException("Synthetic network failure");
            if(Wait) await Task.Delay(Timeout.Infinite,token);
            return Available;
        }
        public Task DownloadAsync(Action<int> progress,CancellationToken token)
        { Downloads++; Progress=progress; progress(50); PendingVersion=Available; return Task.CompletedTask; }
        public void ScheduleApplyAndRestart()=>Applies++;
    }
}
