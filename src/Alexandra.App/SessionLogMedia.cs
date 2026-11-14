using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Alexandra.App;

internal sealed class SessionPhoto(string path, string caption) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Caption { get; } = caption;
    public string FileName => System.IO.Path.GetFileName(Path);
    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail { get => _thumbnail; private set { _thumbnail = value; Changed(); } }
    private string _status = "Loading photo…";
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public void SetThumbnail(BitmapSource? image) { Thumbnail = image; Status = image is null ? "Photo unavailable" : ""; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

internal static class SessionLogMedia
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    public static bool TryPhotoPath(string? target, string logPath, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(target)) return false;
        try
        {
            string candidate;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile || uri.IsUnc || !string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Query)) return false;
                candidate = uri.LocalPath;
            }
            else candidate = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(logPath)!, Uri.UnescapeDataString(target));
            candidate = System.IO.Path.GetFullPath(candidate);
            // No remote images, device paths, alternate streams or executable links.
            if (candidate.Length < 3 || !char.IsAsciiLetter(candidate[0]) || candidate[1] != ':' || candidate[2] != '\\' ||
                candidate[2..].Contains(':') || !Extensions.Contains(System.IO.Path.GetExtension(candidate)) ||
                new DriveInfo(System.IO.Path.GetPathRoot(candidate)!).DriveType == DriveType.Network) return false;
            path = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool TryWebLink(string target, out Uri? uri)
    {
        return Uri.TryCreate(target, UriKind.Absolute, out uri) && (uri.Scheme == "https" || uri.Scheme == "http");
    }

    public static BitmapSource? Load(string path, int longestEdge)
    {
        try
        {
            // Do not follow a local symlink/junction to a network share. Read only bounded raster files.
            for (var item = path; item is not null; item = System.IO.Path.GetDirectoryName(item))
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 64 * 1024 * 1024) return null;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > 100_000_000) return null;
            var landscape = frame.PixelWidth >= frame.PixelHeight;
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            if (landscape) image.DecodePixelWidth = Math.Min(longestEdge, frame.PixelWidth);
            else image.DecodePixelHeight = Math.Min(longestEdge, frame.PixelHeight);
            image.StreamSource = stream; image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or
            System.Runtime.InteropServices.COMException or FileFormatException) { return null; }
    }
}
