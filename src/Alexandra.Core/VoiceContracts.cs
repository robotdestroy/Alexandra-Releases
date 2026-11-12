namespace Alexandra.Core;

public interface IRealtimeTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(string model, string key, CancellationToken cancellationToken);
    Task SendAsync(string json, CancellationToken cancellationToken);
    IAsyncEnumerable<string> ReceiveAsync(CancellationToken cancellationToken);
}
public sealed record VoiceFrame(int Turn, string ItemId, byte[] Pcm);
public sealed record VoiceStatus(int Turn, string Text, bool Busy, bool Attention = false);
public sealed record VoiceFault(int Turn, string Message);
public sealed record VoiceToolCall(string Name, string Question, int Turn, string? System = null, MemoryEdit? Memory = null,
    GameRecordTopic? Records = null, long? RecordShipId = null, CombatQueryKind? Combat = null);
public interface IVoiceSession : IAsyncDisposable
{
    event Action<VoiceFrame>? AudioReceived;
    event Action<VoiceStatus>? StatusChanged;
    event Action<VoiceFault>? Faulted;
    int CurrentTurn { get; }
    IReadOnlyList<Turn> History { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<int> BeginTurnAsync(IReadOnlyDictionary<string, int> playedMilliseconds);
    Task SubmitAudioAsync(int turn, byte[] pcm);
    Task InterruptAsync(IReadOnlyDictionary<string, int> playedMilliseconds);
    Task<string> GetUserTranscriptAsync(int turn, CancellationToken cancellationToken);
}
public interface IStreamingAudioPlayer : IDisposable
{
    event Action<float>? Level;
    void Append(string itemId, byte[] pcm);
    bool HasUnplayedAudio { get; }
    IReadOnlyDictionary<string, int> Stop();
}

