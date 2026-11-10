namespace Alexandra.Core;

public enum GameRecordTopic { Carrier, Ships, Modules, Station, OnFoot }

// Deliberately separate from background commander memory. Implementations read only when called.
public interface IGameRecordSource
{
    Task<GameContext> LookupAsync(GameRecordTopic topic, CancellationToken cancellationToken, long? shipId = null);
}

public static class GameRecordTopics
{
    public static GameRecordTopic Parse(string? value) => value switch
    {
        "carrier" => GameRecordTopic.Carrier,
        "ships" => GameRecordTopic.Ships,
        "modules" => GameRecordTopic.Modules,
        "station" => GameRecordTopic.Station,
        "on_foot" => GameRecordTopic.OnFoot,
        _ => throw new InvalidOperationException("Choose carrier, ships, modules, station or on_foot records.")
    };
}
