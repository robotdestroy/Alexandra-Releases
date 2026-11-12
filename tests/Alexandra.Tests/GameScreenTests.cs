using Alexandra.Core;
using Alexandra.Infrastructure;
using Xunit;

namespace Alexandra.Tests;

public sealed class GameScreenTests
{
    [Theory]
    [InlineData("Take a screenshot", true)]
    [InlineData("Take a photo", true)]
    [InlineData("Alexandra, could you take a photo please?", true)]
    [InlineData("Take a photo of this", true)]
    [InlineData("Snap a photo", true)]
    [InlineData("Take a picture", true)]
    [InlineData("Don't take a photo", false)]
    [InlineData("Do not take a photo", false)]
    [InlineData("How do I take a photo?", false)]
    [InlineData("Which key do I use to take a photo?", false)]
    [InlineData("Did you take a photo?", false)]
    [InlineData("Take a photo of my desktop", false)]
    [InlineData("Take a photo of the browser", false)]
    [InlineData("Could you take a screenshot of my current situation?", true)]
    [InlineData("Save a picture of this", true)]
    [InlineData("Screenshot please", true)]
    [InlineData("Take a screenshot and explain this warning.", true)]
    [InlineData("Explain this warning", false)]
    [InlineData("What does this mean?", false)]
    [InlineData("How do I take a screenshot?", false)]
    [InlineData("Which key takes a screenshot?", false)]
    [InlineData("Don't take a screenshot", false)]
    [InlineData("Take a screenshot of my desktop", false)]
    [InlineData("Save a picture of the browser", false)]
    [InlineData("I saved a screenshot yesterday", false)]
    [InlineData("Did you take a screenshot?", false)]
    [InlineData("", false)]
    public void OnlyExplicitSaveRequestsAllowPersistence(string words, bool allowed) =>
        Assert.Equal(allowed, ScreenRequestPolicy.CanSave(words));

    [Theory]
    [InlineData("What am I looking at?", true)]
    [InlineData("Explain this warning", true)]
    [InlineData("What does this mean?", true)]
    [InlineData("Read this panel please", true)]
    [InlineData("Help me with this", true)]
    [InlineData("Why is that warning showing?", true)]
    [InlineData("Explain this warning but don't save a screenshot", true)]
    [InlineData("Can you look at the game and explain what I'm seeing?", true)]
    [InlineData("Explain this without a screenshot", false)]
    [InlineData("Explain this without taking a photo", false)]
    [InlineData("Explain this without a photo", false)]
    [InlineData("Take a photo", false)]
    [InlineData("Take a photo and explain this warning", true)]
    [InlineData("Don't screenshot this", false)]
    [InlineData("Where can I buy gold?", false)]
    [InlineData("What is engineering?", false)]
    [InlineData("How do I take a screenshot?", false)]
    [InlineData("Don't look at my screen", false)]
    [InlineData("Explain this without taking a screenshot", false)]
    [InlineData("Explain this in my browser", false)]
    public void VisualReferencesAuthorizeOnlyRelevantGameInspection(string words, bool allowed) =>
        Assert.Equal(allowed, ScreenRequestPolicy.CanInspect(words));

    [Fact]
    public async Task InspectionNeverSavesAndSaveNeverUsesAnalysisImage()
    {
        var capture = new Capture(); var store = new Store(); var screens = new GameScreenActions(capture, store);
        Assert.Equal(new byte[] { 1, 2, 3 }, await screens.InspectAsync("Explain this warning", default));
        Assert.Equal(1, capture.JpegCalls); Assert.Equal(0, capture.PngCalls); Assert.Equal(0, store.Calls);
        var saved = await screens.SaveAsync("Take a screenshot", default);
        Assert.Equal("test.png", saved.FileName);
        Assert.Equal(1, capture.JpegCalls); Assert.Equal(1, capture.PngCalls); Assert.Equal(1, store.Calls);
    }
    [Theory]
    [InlineData("Take a photo")]
    [InlineData("Take a picture")]
    [InlineData("Could you take a photo of this?")]
    public async Task PhotoAliasSavesTheSameGamePngWithoutAnalysis(string words)
    {
        var capture = new Capture(); var store = new Store();
        var saved = await new GameScreenActions(capture, store).SaveAsync(words, default);
        Assert.Equal("test.png", saved.FileName);
        Assert.Equal(1, capture.PngCalls); Assert.Equal(1, store.Calls); Assert.Equal(0, capture.JpegCalls);
    }
    [Fact]
    public void VoiceInstructionsExplicitlyMapPhotosToTheExistingScreenshotTool()
    {
        Assert.Contains("\"Take a photo\"", RealtimeProtocol.Instructions);
        Assert.Contains("\"Photo saved.\" after success", RealtimeProtocol.Instructions);
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(RealtimeProtocol.SessionUpdate("marin")));
        var tool = doc.RootElement.GetProperty("session").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "save_screenshot");
        Assert.Contains("'take a photo'", tool.GetProperty("description").GetString());
    }

    [Fact]
    public async Task ModelToolSelectionCannotAuthorizeAnUnrequestedCapture()
    {
        var capture = new Capture(); var store = new Store(); var screens = new GameScreenActions(capture, store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => screens.SaveAsync("Where can I buy gold?", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => screens.InspectAsync("Hello tonight", default));
        Assert.Equal(0, capture.JpegCalls + capture.PngCalls + store.Calls);
    }

    [Fact]
    public async Task CancellationAfterCapturePreventsSaving()
    {
        using var cancellation = new CancellationTokenSource();
        var capture = new Capture { Captured = cancellation.Cancel }; var store = new Store();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GameScreenActions(capture, store).SaveAsync("Take a screenshot", cancellation.Token));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task CaptureFailureDoesNotClaimOrCreateAScreenshot()
    {
        var capture = new Capture { Captured = () => throw new InvalidOperationException("Elite is not visible") };
        var store = new Store();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GameScreenActions(capture, store).SaveAsync("Take a screenshot", default));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task StoreCreatesDistinctVerifiedPngFilesAndNoPartialFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Alexandra-screen-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new GameScreenshotStore(folder);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
            var one = await store.SaveAsync(png, default); var two = await store.SaveAsync(png, default);
            Assert.NotEqual(one.FileName, two.FileName);
            Assert.EndsWith(".png", one.FileName);
            Assert.Equal(png, await File.ReadAllBytesAsync(Path.Combine(folder, one.FileName)));
            Assert.Equal(2, Directory.GetFiles(folder).Length);
            Assert.Empty(Directory.GetFiles(folder, "*.part"));
            Assert.Equal("Pictures / Alexandra", one.Folder);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task CancelledOrInvalidImagesCreateNoFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Alexandra-screen-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new GameScreenshotStore(folder);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync([1, 2, 3], default));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync([], new CancellationToken(true)));
            Assert.False(Directory.Exists(folder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private sealed class Capture : IGameCapture
    {
        public int JpegCalls { get; private set; }
        public int PngCalls { get; private set; }
        public Action? Captured { get; init; }
        public Task<byte[]> CaptureAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); JpegCalls++; Captured?.Invoke(); return Task.FromResult<byte[]>([1, 2, 3]); }
        public Task<byte[]> CapturePngAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); PngCalls++; Captured?.Invoke(); return Task.FromResult<byte[]>([4, 5, 6]); }
    }
    private sealed class Store : IGameScreenshotStore
    {
        public int Calls { get; private set; }
        public Task<SavedScreenshot> SaveAsync(byte[] png, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Assert.Equal(new byte[] { 4, 5, 6 }, png); return Task.FromResult(new SavedScreenshot("test.png", "Pictures / Alexandra")); }
    }
}
