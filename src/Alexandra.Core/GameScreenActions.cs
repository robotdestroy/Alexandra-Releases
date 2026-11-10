namespace Alexandra.Core;

public sealed class GameScreenActions(IGameCapture capture, IGameScreenshotStore store)
{
    public Task<byte[]> InspectAsync(string userUtterance, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ScreenRequestPolicy.CanInspect(userUtterance))
            throw new InvalidOperationException("Ask about what is visible in Elite, for example 'explain this warning', before I inspect the game.");
        return capture.CaptureAsync(token);
    }

    public async Task<SavedScreenshot> SaveAsync(string userUtterance, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ScreenRequestPolicy.CanSave(userUtterance))
            throw new InvalidOperationException("Ask me to take or save a screenshot of Elite before I save an image.");
        var png = await capture.CapturePngAsync(token);
        token.ThrowIfCancellationRequested();
        return await store.SaveAsync(png, token);
    }
}
