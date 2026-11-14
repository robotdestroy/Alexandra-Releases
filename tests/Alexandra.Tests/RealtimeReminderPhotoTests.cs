using System.Text.Json;
using Alexandra.Core;
using Xunit;

namespace Alexandra.Tests;

public sealed partial class RealtimeTests
{
    [Theory]
    [InlineData("save_screenshot", "{\"caption\":\"first landing\"}")]
    [InlineData("read_reminders", "{}")]
    [InlineData("get_landing_pad", "{\"pad\":24}")]
    [InlineData("get_landing_pad", "{}")]
    [InlineData("update_reminder", "{\"action\":\"create\",\"trigger\":\"docked\",\"text\":\"claim rewards\"}")]
    public async Task ReminderAndCaptionToolsReachHostAsTypedArguments(string name, string arguments)
    {
        var wire = new Wire(); VoiceToolCall? seen = null;
        await using var session = Session(wire, (call, _) => { seen = call; return Task.FromResult("{}"); });
        var turn = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(turn, "Synthetic request");
        wire.Emit(Created(turn, "feature"));
        wire.Emit(Done("feature", new { type = "function_call", name, call_id = "feature-call", arguments }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        Assert.NotNull(seen); Assert.Equal(name, seen!.Name);
        if (name == "save_screenshot") Assert.Equal("first landing", seen.Caption);
        if (name == "update_reminder") Assert.Equal(ReminderTrigger.Docked, seen.Reminder!.Trigger);
        if (name == "get_landing_pad") Assert.Equal(arguments == "{}" ? null : (int?)24, seen.LandingPad);
    }
    [Theory]
    [InlineData("save_screenshot", "{\"path\":\"C:/secret.png\"}")]
    [InlineData("save_screenshot", "{\"caption\":3}")]
    [InlineData("get_landing_pad", "{\"pad\":0}")]
    [InlineData("get_landing_pad", "{\"pad\":65}")]
    [InlineData("get_landing_pad", "{\"pad\":24.5}")]
    [InlineData("get_landing_pad", "{\"pad\":\"two four\"}")]
    [InlineData("get_landing_pad", "{\"station\":\"Invented\"}")]
    [InlineData("read_reminders", "{\"deleteAll\":true}")]
    [InlineData("update_reminder", "{\"action\":\"create\",\"trigger\":\"buy\"}")]
    [InlineData("update_reminder", "{\"action\":\"create\",\"keys\":\"Enter\"}")]
    public async Task NewToolsRejectUnsupportedArgumentsBeforeHost(string name, string arguments)
    {
        var wire = new Wire(); var calls = 0;
        await using var session = Session(wire, (_, _) => { calls++; return Task.FromResult("{}"); });
        var turn = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(turn, "Synthetic request");
        wire.Emit(Created(turn, "feature"));
        wire.Emit(Done("feature", new { type = "function_call", name, call_id = "feature-call", arguments }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        Assert.Equal(0, calls);
    }
}
