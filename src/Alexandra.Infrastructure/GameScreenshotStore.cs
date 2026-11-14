using Alexandra.Core;

namespace Alexandra.Infrastructure;

// Destination is supplied by the application, never a model or transcript.
public sealed class GameScreenshotStore(string folder) : IGameScreenshotStore
{
    public async Task<SavedScreenshot> SaveAsync(byte[] png, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidOperationException("The game did not produce a valid PNG screenshot.");
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new InvalidOperationException("The screenshot folder is unavailable.");
        var fileName = $"Elite-{DateTime.Now:yyyy-MM-dd-HHmmss}-{Guid.NewGuid():N}.png";
        var path = Path.Combine(folder, fileName);
        var temporary = path + ".part";
        Directory.CreateDirectory(folder);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                await output.WriteAsync(png, token);
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, false);
            return new(fileName, "Pictures / Alexandra", path, DateTimeOffset.UtcNow);
        }
        catch
        {
            // Only remove this call's own partial file, never existing screenshots.
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
