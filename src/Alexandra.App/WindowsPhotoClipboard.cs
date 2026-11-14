using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Alexandra.App;

internal interface IPhotoClipboard
{
    void Copy(BitmapSource image);
}

/// <summary>Explicit photo-viewer copy only; never a navigation destination or game input.</summary>
internal sealed class WindowsPhotoClipboard : IPhotoClipboard
{
    public void Copy(BitmapSource image)
    {
        using var png = new MemoryStream();
        var data = CreateData(image, png);
        // Persist image bytes so the photo can still be pasted after the viewer/app closes.
        Clipboard.SetDataObject(data, true);
    }

    internal static DataObject CreateData(BitmapSource image, Stream png)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png); png.Position = 0;
        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", png);
        return data;
    }
}
