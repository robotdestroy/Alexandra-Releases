namespace Alexandra.Core;

public sealed record ControllerDevice(string Id, string Name, bool Connected);
public sealed record ControllerButtonEvent(string DeviceId, int ButtonIndex, bool IsDown, DateTimeOffset Timestamp);
public interface IControllerInputSource
{
    event EventHandler<ControllerButtonEvent>? ButtonChanged;
    event EventHandler<string>? DeviceDisconnected;
    IReadOnlyList<ControllerDevice> Devices { get; }
}
public interface IPushToTalkTrigger
{
    event EventHandler? Pressed;
    event EventHandler? Released;
    event EventHandler? Disconnected;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}
public sealed record Turn(string Question, string Answer);
public sealed record Source(string Title, string Url);
public sealed record Destination(string System, string? Station, string? EvidenceUrl);
public sealed record Answer(string Speech, Destination? Destination, IReadOnlyList<Source> Sources);
public sealed record GameContext(string? System, string Json, string Availability, string? MemoryScope = null);
public interface IGameContextSource { Task<GameContext> ReadAsync(CancellationToken cancellationToken); }
public interface IGameCapture
{
    Task<byte[]> CaptureAsync(CancellationToken cancellationToken); // JPEG, reduced for visual research.
    Task<byte[]> CapturePngAsync(CancellationToken cancellationToken); // Full resolution, only for an explicit save.
}
public sealed record SavedScreenshot(string FileName, string Folder, string? FullPath = null, DateTimeOffset? SavedAt = null);
public interface IGameScreenshotStore
{
    Task<SavedScreenshot> SaveAsync(byte[] png, CancellationToken cancellationToken);
}
public interface ISystemResolver { Task<string?> ResolveAsync(string candidate, CancellationToken cancellationToken); }
public interface ISystemClipboard
{
    Task<bool> TryCopyAsync(string system, CancellationToken cancellationToken);
}
public interface ICompanion
{
    Task<string> TranscribeAsync(byte[] wav, string? currentSystem, CancellationToken cancellationToken);
    Task<Answer> AnswerAsync(string question, GameContext context, IReadOnlyList<Turn> history,
        Func<CancellationToken, Task<byte[]>> capture, CancellationToken cancellationToken);
    Task<byte[]> SpeakAsync(string text, CancellationToken cancellationToken);
}
public interface IAudioDevice : IDisposable
{
    event Action<float>? Level;
    void Start(int device);
    Task<byte[]> StopAsync();
    Task PlayAsync(byte[] wav, CancellationToken cancellationToken);
    void StopPlayback();
}

