namespace Alexandra.Core;

public interface IDiagnosticLog
{
    void Write(string kind, string message, int? turn = null);
}
public sealed class NullDiagnosticLog : IDiagnosticLog
{
    public static readonly NullDiagnosticLog Instance = new();
    public void Write(string kind, string message, int? turn = null) { }
}
public sealed record BrowserAction(string Action, string? Url = null, string? Snapshot = null,
    string? Element = null, string? Value = null);
public sealed record BrowserOption(string Value, string Label);
public sealed record BrowserControl(string Id, string Kind, string Label, string? Url,
    string? Value, IReadOnlyList<BrowserOption> Options);
public sealed record BrowserSnapshot(string Snapshot, string Url, string Title, string Text,
    IReadOnlyList<BrowserControl> Controls, string? BlockedReason = null);
public interface IResearchBrowser : IAsyncDisposable
{
    Task<BrowserSnapshot> ActAsync(BrowserAction action, CancellationToken cancellationToken);
}

