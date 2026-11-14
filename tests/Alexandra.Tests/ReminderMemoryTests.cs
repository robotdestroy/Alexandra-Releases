using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class ReminderMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alex-reminder-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private string Journals => Path.Combine(_root, "journals");
    private string Store => Path.Combine(_root, "memory");
    private string Logs => Path.Combine(_root, "sessions");
    private string Journal => Path.Combine(Journals, "Journal.2026-11-12.01.log");
    private string[] _fixture = [];
    public ReminderMemoryTests() => Directory.CreateDirectory(Journals);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private CompanionMemory Create(Func<bool>? enabled = null, Func<bool>? logs = null, ISessionLogWriter? writer = null) =>
        new(() => Journals, Store, enabled, _clock, writer ?? new MarkdownSessionLogWriter(Logs), logs);
    private async Task Append(string text) => await File.AppendAllTextAsync(Journal, text + "\n");
    private async Task<CompanionMemory> Start()
    {
        var memory = Create(); await memory.SynchronizeAsync(default);
        _fixture = await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reminder-photo-session.jsonl"));
        await Append(string.Join('\n', _fixture.Take(2)));
        _clock.Now = _clock.Now.AddMinutes(2);
        await memory.SynchronizeAsync(default);
        return memory;
    }
    private async Task<VoiceReminder> Add(CompanionMemory memory, ReminderTrigger trigger = ReminderTrigger.Docked, string? target = null)
    {
        var words = trigger switch {
            ReminderTrigger.SystemArrival => "When I enter Deciat remind me to check my materials",
            ReminderTrigger.Touchdown => "When I touch down remind me to check my materials",
            _ => "When I dock" + (target is null ? "" : " at " + target) + " remind me to check my materials"
        };
        return (await memory.EditReminderAsync(new("create", trigger, "check my materials", target), words,
            (await memory.ReadRemindersAsync(default))!.Scope, default)).Reminder!;
    }
    [Fact]
    public async Task DockingReminderPersistsIsIdempotentAndIsClaimedOnlyOnce()
    {
        var memory = await Start(); var r = await Add(memory); Assert.Equal(r.Id, (await Add(memory)).Id);
        memory = Create(); Assert.Equal("armed", Assert.Single((await memory.ReadRemindersAsync(default))!.Items).Status);
        await Append(_fixture[2]); _clock.Now = _clock.Now.AddMinutes(1);
        var snapshot = (await memory.ReadRemindersAsync(default))!;
        var ready = Assert.Single(snapshot.Items); Assert.Equal("ready", ready.Status);
        Assert.True(await memory.ClaimReminderAsync(snapshot.Scope, r.Id, ready.TriggeredAt!.Value, default));
        Assert.False(await memory.ClaimReminderAsync(snapshot.Scope, r.Id, ready.TriggeredAt.Value, default));
        Assert.Equal("notified", Assert.Single((await Create().ReadRemindersAsync(default))!.Items).Status);
        await Append(_fixture[2]); // Duplicate journal line never rearms it.
        Assert.Equal("notified", Assert.Single((await Create().ReadRemindersAsync(default))!.Items).Status);
    }
    [Fact]
    public async Task OldEventsAndLocationSnapshotsNeverTriggerANewReminder()
    {
        var memory = await Start(); await Append(_fixture[2]); _clock.Now = _clock.Now.AddMinutes(2);
        await memory.SynchronizeAsync(default); await Add(memory);
        await Append("""{"timestamp":"2026-11-12T10:05:00Z","event":"Location","StarSystem":"Sol","Docked":true,"StationName":"Galileo"}""");
        Assert.Equal("armed", Assert.Single((await memory.ReadRemindersAsync(default))!.Items).Status);
        await Append(_fixture[2]); // Older reordered evidence cannot fire.
        Assert.Equal("armed", Assert.Single((await memory.ReadRemindersAsync(default))!.Items).Status);
    }
    [Fact]
    public async Task LeavingBeforeQuietRearmsRatherThanAnnouncingAnOldDock()
    {
        var memory = await Start(); var r = await Add(memory); await Append(_fixture[2]);
        var ready = (await memory.ReadRemindersAsync(default))!;
        await Append(_fixture[3]);
        Assert.False(await memory.ClaimReminderAsync(ready.Scope, r.Id, ready.Items[0].TriggeredAt!.Value, default));
        Assert.Equal("armed", Assert.Single((await memory.ReadRemindersAsync(default))!.Items).Status);
    }
    [Fact]
    public async Task MatchingSystemAndStationOnlyAndPlayerControlledLanding()
    {
        var memory = await Start();
        var wrong = await Add(memory, target: "Wrong Station");
        var system = await Add(memory, ReminderTrigger.SystemArrival, "Deciat");
        var land = await Add(memory, ReminderTrigger.Touchdown);
        await Append(string.Join('\n', _fixture.Skip(2).Take(3)));
        var snapshot = (await memory.ReadRemindersAsync(default))!;
        Assert.Equal("armed", snapshot.Items.Single(r => r.Id == wrong.Id).Status);
        Assert.Equal("ready", snapshot.Items.Single(r => r.Id == system.Id).Status);
        await Append(_fixture[5].Replace("true", "false"));
        Assert.Equal("armed", (await memory.ReadRemindersAsync(default))!.Items.Single(r => r.Id == land.Id).Status);
        await Append(_fixture[5]);
        Assert.Equal("ready", (await memory.ReadRemindersAsync(default))!.Items.Single(r => r.Id == land.Id).Status);
    }
    [Fact]
    public async Task SystemArrivalStaysPendingDuringLocalSupercruiseButNotLeavingTheSystem()
    {
        var memory = await Start(); await Add(memory, ReminderTrigger.SystemArrival, "Deciat");
        await Append(_fixture[4]);
        await Append("""{"timestamp":"2026-11-12T10:06:00Z","event":"StartJump","JumpType":"Supercruise"}""");
        await Append("""{"timestamp":"2026-11-12T10:06:05Z","event":"SupercruiseEntry"}""");
        Assert.Equal("ready", Assert.Single((await memory.ReadRemindersAsync(default))!.Items).Status);
        await Append("""{"timestamp":"2026-11-12T10:07:00Z","event":"StartJump","JumpType":"Hyperspace","StarSystem":"Sol"}""");
        Assert.Equal("armed", Assert.Single((await memory.ReadRemindersAsync(default))!.Items).Status);
    }
    [Fact]
    public async Task CommanderSwitchDisabledMemoryAndCancellationAreIsolated()
    {
        var memory = await Start(); var r = await Add(memory);
        var scope = (await memory.ReadRemindersAsync(default))!.Scope;
        await Assert.ThrowsAsync<InvalidOperationException>(() => memory.EditReminderAsync(new("cancel", Id: r.Id), "cancel that reminder", "wrong-scope", default));
        Assert.True((await memory.EditReminderAsync(new("cancel", Id: r.Id), "cancel that reminder", scope, default)).Changed);
        r = await Add(memory);
        await Append(_fixture[0].Replace("10:00:01", "10:07:00").Replace("SYNTHETIC-REMINDER-A", "SYNTHETIC-REMINDER-B").Replace("Synthetic Reminder Pilot", "Synthetic Other Pilot"));
        Assert.Empty((await memory.ReadRemindersAsync(default))!.Items);
        Assert.False(await memory.ClaimReminderAsync(scope, r.Id, _clock.Now, default));
        Assert.Null(await Create(() => false).ReadRemindersAsync(default));
    }
    [Fact]
    public async Task FailedSaveRollsBackAndNeverClaimsSuccess()
    {
        var memory = await Start(); var scope = (await memory.ReadRemindersAsync(default))!.Scope;
        var checkpoint = Assert.Single(Directory.GetFiles(Store, "memory-*.json"));
        Directory.CreateDirectory(checkpoint + ".tmp"); // Synthetic failed write, no user data.
        await Assert.ThrowsAnyAsync<Exception>(() => Add(memory));
        Directory.Delete(checkpoint + ".tmp");
        Assert.Empty((await memory.ReadRemindersAsync(default))!.Items);
    }
    [Fact]
    public async Task RequestedPhotoAddsCaptionLocationTimeAndSafeLocalLinkOnlyOnce()
    {
        var memory = await Start(); await Append(_fixture[2]); _clock.Now = _clock.Now.AddMinutes(2);
        var anchor = await memory.CapturePhotoAnchorAsync(_clock.Now, _clock.Now.AddHours(-1), default);
        Assert.NotNull(anchor);
        var path = Path.Combine(_root, "Pictures", "Photo #1.png");
        var photo = new SavedScreenshot("Photo #1.png", "Pictures / Alexandra", path, _clock.Now);
        Assert.True(await memory.RecordSessionPhotoAsync(anchor!, photo, "First [landing] <script>", default));
        Assert.True(await memory.RecordSessionPhotoAsync(anchor!, photo, "First [landing] <script>", default));
        var text = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Logs, "*.md")));
        Assert.Contains("## Photos", text); Assert.Contains("10:04:00 UTC", text);
        Assert.Contains("Sol, Galileo", text); Assert.Contains("First \\[landing\\] \\<script\\>", text);
        Assert.Contains("Photo%20%231.png", text); Assert.Contains("[View photo](<file:///", text);
        Assert.Equal(1, text.Split("[View photo]").Length - 1);
        Assert.DoesNotContain("<script>", text); Assert.DoesNotContain("SYNTHETIC-REMINDER", text);
        await Create().SynchronizeAsync(default);
        Assert.Equal(text, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Logs, "*.md"))));
    }
    [Fact]
    public async Task PhotoNeverCreatesCrossSessionLinksOrRequiresMemoryEnabled()
    {
        await Start(); var memory = Create(() => false);
        var anchor = (await memory.CapturePhotoAnchorAsync(_clock.Now, _clock.Now.AddHours(-1), default))!;
        var photo = new SavedScreenshot("test.png", "Pictures / Alexandra", Path.Combine(_root, "test.png"));
        Assert.True(await memory.RecordSessionPhotoAsync(anchor, photo, "", default));
        await Append("""{"timestamp":"2026-11-12T10:03:00Z","event":"Shutdown"}""");
        await memory.SynchronizeAsync(default);
        Assert.False(await memory.RecordSessionPhotoAsync(anchor, photo with { FileName = "next.png", FullPath = Path.Combine(_root, "next.png") }, "", default));
        Assert.Null(await Create(logs: () => false).CapturePhotoAnchorAsync(_clock.Now, _clock.Now.AddHours(-1), default));
    }
    [Theory]
    [InlineData("https://example.com/a.png")]
    [InlineData("relative.png")]
    [InlineData("\\\\server\\share\\a.png")]
    [InlineData("C:\\temp\\a.exe")]
    public async Task PhotosRejectRemoteAndNonPngPaths(string path)
    {
        var memory = await Start(); var anchor = (await memory.CapturePhotoAnchorAsync(_clock.Now, _clock.Now.AddHours(-1), default))!;
        Assert.False(await memory.RecordSessionPhotoAsync(anchor, new(Path.GetFileName(path), "ignored", path), "test", default));
    }
    [Fact]
    public async Task OlderCheckpointsWithoutReminderAndPhotoFieldsStillLoad()
    {
        await Start(); var path = Assert.Single(Directory.GetFiles(Store, "memory-*.json"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        foreach (var p in node["profiles"]!.AsObject()) p.Value!.AsObject().Remove("reminders");
        foreach (var s in node["sessionLogs"]!["sessions"]!.AsArray()) s!.AsObject().Remove("photos");
        await File.WriteAllTextAsync(path, node.ToJsonString());
        Assert.Empty((await Create().ReadRemindersAsync(default))!.Items);
        Assert.Single(Directory.GetFiles(Logs, "*.md"));
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-12T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
