using System.Text.Json;
using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed partial class RealtimeTests
{
    [Theory]
    [InlineData("live", CombatQueryKind.Live)]
    [InlineData("review", CombatQueryKind.Review)]
    public async Task CombatToolDispatchesTypedQueryWithoutResearch(string mode, CombatQueryKind expected)
    {
        var wire = new Wire(); var count = 0;
        await using var session = Session(wire, (call, _) => {
            Assert.Equal("get_combat_context", call.Name); Assert.Equal(expected, call.Combat);
            count++; return Task.FromResult("{}");
        });
        var turn = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(turn, "Check my pips");
        wire.Emit(Created(turn, "combat"));
        wire.Emit(Done("combat", new { type = "function_call", name = "get_combat_context", call_id = "combat-call",
            arguments = JsonSerializer.Serialize(new { mode, question = "Check my pips" }) }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        Assert.Equal(1, count);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"mode\":\"attack\",\"question\":\"Fire\"}")]
    [InlineData("{\"mode\":\"live\",\"question\":\"\"}")]
    [InlineData("{\"mode\":\"live\",\"question\":\"Pips\",\"keys\":\"Space\"}")]
    public async Task InvalidCombatRequestsNeverReachHost(string arguments)
    {
        var wire = new Wire(); var count = 0;
        await using var session = Session(wire, (_, _) => { count++; return Task.FromResult("{}"); });
        var turn = await session.BeginTurnAsync(Heard()); await session.SubmitTextAsync(turn, "Check my pips");
        wire.Emit(Created(turn, "combat"));
        wire.Emit(Done("combat", new { type = "function_call", name = "get_combat_context", call_id = "combat-call", arguments }));
        await Eventually(() => wire.Events.Count(e => e.GetProperty("type").GetString() == "response.create") == 2);
        Assert.Equal(0, count);
    }
    [Fact]
    public void CombatInstructionsKeepLiveAnswersLocalBriefAndAdvisory()
    {
        Assert.Contains("INSTEAD of get_game_context", RealtimeProtocol.Instructions);
        Assert.Contains("under 15 words", RealtimeProtocol.Instructions);
        Assert.Contains("Missing private telemetry cannot be recovered", RealtimeProtocol.Instructions);
        Assert.Contains("never continuous", RealtimeProtocol.Instructions);
        Assert.Contains("Never start background screenshots", RealtimeProtocol.Instructions);
        Assert.Contains("remain advisory only", RealtimeProtocol.Instructions);
    }
}
