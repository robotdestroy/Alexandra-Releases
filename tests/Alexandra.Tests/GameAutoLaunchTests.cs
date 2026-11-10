using Alexandra.Core;
using Xunit;

namespace Alexandra.Tests;

public sealed class GameAutoLaunchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Disabled_never_queries_processes_or_launches()
    {
        var environment = new Environment { Enabled = false, Session = "game-1" };
        Assert.Equal(GameAutoLaunchResult.Disabled, new GameAutoLaunchCoordinator(environment).Check(Now));
        Assert.Equal(0, environment.Queries);
        Assert.Equal(0, environment.Launches);
    }

    [Fact]
    public void Waits_for_actual_game_and_launches_on_arrival()
    {
        var environment = new Environment();
        var watcher = new GameAutoLaunchCoordinator(environment);
        Assert.Equal(GameAutoLaunchResult.Waiting, watcher.Check(Now));
        environment.Session = "game-1";
        Assert.Equal(GameAutoLaunchResult.Launched, watcher.Check(Now.AddSeconds(3)));
        Assert.Equal(1, environment.Launches);
    }

    [Fact]
    public void Starting_windows_with_game_already_running_opens_companion()
    {
        var environment = new Environment { Session = "game-1" };
        Assert.Equal(GameAutoLaunchResult.Launched, new GameAutoLaunchCoordinator(environment).Check(Now));
        Assert.Equal(1, environment.Launches);
    }

    [Fact]
    public void Does_not_relaunch_after_user_closes_companion_in_same_session()
    {
        var environment = new Environment { Session = "game-1" };
        var watcher = new GameAutoLaunchCoordinator(environment);
        watcher.Check(Now);
        for (var i = 1; i < 100; i++) watcher.Check(Now.AddSeconds(i * 3));
        Assert.Equal(1, environment.Launches);
    }

    [Fact]
    public void Existing_companion_is_not_activated_or_replaced()
    {
        var environment = new Environment { Session = "game-1", CompanionRunning = true };
        var watcher = new GameAutoLaunchCoordinator(environment);
        Assert.Equal(GameAutoLaunchResult.AlreadyRunning, watcher.Check(Now));
        environment.CompanionRunning = false;
        Assert.Equal(GameAutoLaunchResult.Waiting, watcher.Check(Now.AddMinutes(1)));
        Assert.Equal(0, environment.Launches);
    }

    [Fact]
    public void New_game_session_can_launch_again()
    {
        var environment = new Environment { Session = "game-1" };
        var watcher = new GameAutoLaunchCoordinator(environment);
        watcher.Check(Now);
        environment.Session = null;
        watcher.Check(Now.AddSeconds(3));
        environment.Session = "game-2";
        Assert.Equal(GameAutoLaunchResult.Launched, watcher.Check(Now.AddSeconds(6)));
        Assert.Equal(2, environment.Launches);
    }

    [Fact]
    public void Fast_game_restart_between_polls_is_detected_by_session_identity()
    {
        var environment = new Environment { Session = "pid-42:start-1" };
        var watcher = new GameAutoLaunchCoordinator(environment);
        watcher.Check(Now);
        environment.Session = "pid-42:start-2";
        Assert.Equal(GameAutoLaunchResult.Launched, watcher.Check(Now.AddSeconds(3)));
        Assert.Equal(2, environment.Launches);
    }

    [Fact]
    public void Unreadable_game_state_is_not_treated_as_game_exit()
    {
        var environment = new Environment { Session = "game-1" };
        var watcher = new GameAutoLaunchCoordinator(environment);
        watcher.Check(Now);
        environment.Readable = false;
        Assert.Equal(GameAutoLaunchResult.Unknown, watcher.Check(Now.AddSeconds(3)));
        environment.Readable = true;
        Assert.Equal(GameAutoLaunchResult.Waiting, watcher.Check(Now.AddSeconds(6)));
        Assert.Equal(1, environment.Launches);
    }

    [Fact]
    public void Launch_failure_retries_slowly_instead_of_spinning()
    {
        var environment = new Environment { Session = "game-1", FailLaunch = true };
        var watcher = new GameAutoLaunchCoordinator(environment);
        Assert.Equal(GameAutoLaunchResult.RetryLater, watcher.Check(Now));
        watcher.Check(Now.AddSeconds(3));
        watcher.Check(Now.AddSeconds(29));
        Assert.Equal(1, environment.Launches);
        environment.FailLaunch = false;
        Assert.Equal(GameAutoLaunchResult.Launched, watcher.Check(Now.AddSeconds(30)));
        Assert.Equal(2, environment.Launches);
    }

    [Fact]
    public void Manual_launch_during_retry_suppresses_automatic_launch()
    {
        var environment = new Environment { Session = "game-1", FailLaunch = true };
        var watcher = new GameAutoLaunchCoordinator(environment);
        watcher.Check(Now);
        environment.CompanionRunning = true;
        Assert.Equal(GameAutoLaunchResult.AlreadyRunning, watcher.Check(Now.AddSeconds(3)));
        environment.CompanionRunning = false;
        watcher.Check(Now.AddMinutes(1));
        Assert.Equal(1, environment.Launches);
    }

    [Fact]
    public void Disabling_stops_launches_immediately()
    {
        var environment = new Environment();
        var watcher = new GameAutoLaunchCoordinator(environment);
        watcher.Check(Now);
        environment.Enabled = false;
        environment.Session = "game-1";
        Assert.Equal(GameAutoLaunchResult.Disabled, watcher.Check(Now.AddSeconds(3)));
        Assert.Equal(0, environment.Launches);
    }

    private sealed class Environment : IGameAutoLaunchEnvironment
    {
        public bool Enabled { get; set; } = true;
        public string? Session { get; set; }
        public bool Readable { get; set; } = true;
        public bool CompanionRunning { get; set; }
        public bool FailLaunch { get; set; }
        public int Launches { get; private set; }
        public int Queries { get; private set; }
        public bool TryGetGameSession(out string? session)
        { Queries++; session = Readable ? Session : null; return Readable; }
        public void LaunchCompanion()
        {
            Launches++;
            if (FailLaunch) throw new InvalidOperationException("synthetic launch failure");
        }
    }
}
