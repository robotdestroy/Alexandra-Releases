using Alexandra.Core;
using Xunit;

namespace Alexandra.Tests;

public sealed class GameAutoLaunchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    [Fact]
    public void DisabledNeverQueriesProcessesOrLaunches()
    {
        var e = new Environment { Enabled = false, Session = "game-1" };
        Assert.Equal(GameAutoLaunchResult.Disabled, new GameAutoLaunchCoordinator(e).Check(Now));
        Assert.Equal(0, e.Queries); Assert.Equal(0, e.Launches);
    }
    [Fact]
    public void WaitsForGameThenRequiresReadyAcknowledgement()
    {
        var e = new Environment(); var watcher = new GameAutoLaunchCoordinator(e);
        Assert.Equal(GameAutoLaunchResult.Waiting, watcher.Check(Now));
        e.Session = "game-1";
        Assert.Equal(GameAutoLaunchResult.WaitingForReady, watcher.Check(Now.AddSeconds(3)));
        Assert.Equal(1, e.Launches);
        e.CompanionRunning = true;
        Assert.Equal(GameAutoLaunchResult.WaitingForReady, watcher.Check(Now.AddSeconds(6)));
        e.CompanionReady = true;
        Assert.Equal(GameAutoLaunchResult.Launched, watcher.Check(Now.AddSeconds(9)));
        Assert.Equal(1, e.Restores);
    }
    [Fact]
    public void ExistingWindowIsRestoredOncePerNewGameSession()
    {
        var e = new Environment { Session = "game-1", CompanionRunning = true, CompanionReady = true };
        var watcher = new GameAutoLaunchCoordinator(e);
        Assert.Equal(GameAutoLaunchResult.AlreadyRunning, watcher.Check(Now));
        watcher.Check(Now.AddSeconds(3)); Assert.Equal(1, e.Restores); Assert.Equal(0, e.Launches);
        e.Session = "game-2"; watcher.Check(Now.AddSeconds(6)); Assert.Equal(2, e.Restores);
    }
    [Fact]
    public void DoesNotReopenAfterUserClosesAcknowledgedWindow()
    {
        var e = new Environment { Session = "game-1" }; var watcher = new GameAutoLaunchCoordinator(e);
        watcher.Check(Now); e.CompanionReady = true; watcher.Check(Now.AddSeconds(3));
        e.CompanionReady = false; e.CompanionRunning = false;
        for (var i = 1; i < 100; i++) Assert.Equal(GameAutoLaunchResult.Waiting, watcher.Check(Now.AddSeconds(i * 10)));
        Assert.Equal(1, e.Launches);
    }
    [Fact]
    public void ImmediateExitOrUpdateGapRetriesWithoutTreatingProcessStartAsSuccess()
    {
        var e = new Environment { Session = "game-1" }; var watcher = new GameAutoLaunchCoordinator(e);
        watcher.Check(Now);
        Assert.Equal(GameAutoLaunchResult.RetryLater, watcher.Check(Now.AddSeconds(29)));
        Assert.Equal(GameAutoLaunchResult.WaitingForReady, watcher.Check(Now.AddSeconds(30)));
        e.CompanionReady = true;
        Assert.Equal(GameAutoLaunchResult.Launched, watcher.Check(Now.AddSeconds(33)));
        Assert.Equal(2, e.Launches);
    }
    [Fact]
    public void SlowStartupNeverSpawnsDuplicates()
    {
        var e = new Environment { Session = "game-1", CompanionRunning = true }; var watcher = new GameAutoLaunchCoordinator(e);
        for (var i = 0; i < 10; i++) Assert.Equal(GameAutoLaunchResult.WaitingForReady, watcher.Check(Now.AddMinutes(i)));
        Assert.Equal(0, e.Launches);
        e.CompanionReady = true; Assert.Equal(GameAutoLaunchResult.AlreadyRunning, watcher.Check(Now.AddMinutes(11)));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void FailedOrUnconfirmedLaunchesAreBounded(bool throwOnLaunch)
    {
        var e = new Environment { Session = "game-1", FailLaunch = throwOnLaunch }; var watcher = new GameAutoLaunchCoordinator(e);
        for (var i = 0; i < 3; i++) watcher.Check(Now.AddSeconds(30 * i));
        Assert.Equal(GameAutoLaunchResult.LaunchUnconfirmed, watcher.Check(Now.AddSeconds(90)));
        Assert.Equal(3, e.Launches);
        e.Session = "game-2"; watcher.Check(Now.AddSeconds(93)); Assert.Equal(4, e.Launches);
    }
    [Fact]
    public void UnreadableProcessListDoesNotInventGameExit()
    {
        var e = new Environment { Session = "game-1", CompanionReady = true }; var watcher = new GameAutoLaunchCoordinator(e);
        watcher.Check(Now); e.Readable = false;
        Assert.Equal(GameAutoLaunchResult.Unknown, watcher.Check(Now.AddSeconds(3)));
        e.Readable = true; Assert.Equal(GameAutoLaunchResult.Waiting, watcher.Check(Now.AddSeconds(6)));
        Assert.Equal(1, e.Restores);
    }
    [Fact]
    public void ManualLaunchDuringRetryWinsWithoutAnotherProcess()
    {
        var e = new Environment { Session = "game-1", FailLaunch = true }; var watcher = new GameAutoLaunchCoordinator(e);
        watcher.Check(Now); e.CompanionRunning = true; e.CompanionReady = true;
        Assert.Equal(GameAutoLaunchResult.Launched, watcher.Check(Now.AddSeconds(3)));
        Assert.Equal(1, e.Launches); Assert.Equal(1, e.Restores);
    }
    [Fact]
    public void RestoreFailureCanRetryAndDisableStopsAllActions()
    {
        var e = new Environment { Session = "game-1", CompanionReady = true, FailRestore = true }; var watcher = new GameAutoLaunchCoordinator(e);
        Assert.Equal(GameAutoLaunchResult.RetryLater, watcher.Check(Now));
        e.FailRestore = false; Assert.Equal(GameAutoLaunchResult.AlreadyRunning, watcher.Check(Now.AddSeconds(3)));
        e.Enabled = false; e.Session = "game-2";
        Assert.Equal(GameAutoLaunchResult.Disabled, watcher.Check(Now.AddSeconds(6))); Assert.Equal(0, e.Launches);
    }
    private sealed class Environment : IGameAutoLaunchEnvironment
    {
        public bool Enabled { get; set; } = true;
        public string? Session { get; set; }
        public bool Readable { get; set; } = true;
        public bool CompanionRunning { get; set; }
        public bool CompanionReady { get; set; }
        public bool FailLaunch { get; set; }
        public bool FailRestore { get; set; }
        public int Launches { get; private set; }
        public int Restores { get; private set; }
        public int Queries { get; private set; }
        public bool TryGetGameSession(out string? session) { Queries++; session = Readable ? Session : null; return Readable; }
        public void LaunchCompanion() { Launches++; if (FailLaunch) throw new InvalidOperationException("synthetic launch failure"); }
        public void RestoreCompanion() { if (FailRestore) throw new InvalidOperationException("synthetic restore failure"); Restores++; }
    }
}
