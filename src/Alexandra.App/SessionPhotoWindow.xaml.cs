using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;

namespace Alexandra.App;

public partial class SessionPhotoWindow : Window
{
    private readonly IReadOnlyList<SessionPhoto> _photos;
    private readonly IPhotoClipboard _clipboard;
    private int _index, _generation;
    private double _zoom = 1;
    internal Task PhotoLoaded { get; private set; } = Task.CompletedTask;
    internal Task PhotoCopied { get; private set; } = Task.CompletedTask;
    internal SessionPhotoWindow(IReadOnlyList<SessionPhoto> photos, int index, IPhotoClipboard? clipboard = null)
    {
        if (photos.Count == 0 || index < 0 || index >= photos.Count) throw new ArgumentOutOfRangeException(nameof(index));
        InitializeComponent(); _photos = photos; _clipboard = clipboard ?? new WindowsPhotoClipboard();
        Closed += (_, _) => _generation++;
        Select(index);
    }
    private void Select(int index)
    {
        _index = index; _zoom = 1;
        Caption.Text = _photos[index].Caption; PhotoFile.Text = _photos[index].FileName;
        PhotoCount.Text = $"{index + 1} / {_photos.Count}";
        Previous.IsEnabled = index > 0; Next.IsEnabled = index < _photos.Count - 1;
        PhotoImage.Source = null; PhotoStatus.Text = "Loading photo…";
        CopyNotice.Text = ""; CopyNotice.Visibility = Visibility.Collapsed;
        CopyPhoto.IsEnabled = ZoomIn.IsEnabled = ZoomOut.IsEnabled = Fit.IsEnabled = false;
        PhotoLoaded = LoadPhotoAsync(_photos[index], ++_generation);
    }
    private async Task LoadPhotoAsync(SessionPhoto photo, int generation)
    {
        var image = await Task.Run(() => SessionLogMedia.Load(photo.Path, 4096));
        if (generation != _generation) return;
        PhotoImage.Source = image;
        PhotoStatus.Text = image is null ? "Photo unavailable. It may have been moved or deleted, or the file cannot be read." : "";
        CopyPhoto.IsEnabled = ZoomIn.IsEnabled = Fit.IsEnabled = image is not null;
        SizePhoto();
    }
    private void SizePhoto()
    {
        if (PhotoImage?.Source is not BitmapSource bitmap) return;
        var scale = Math.Min(Math.Max(1, PhotoScroll.ActualWidth - 20) / bitmap.PixelWidth,
            Math.Max(1, PhotoScroll.ActualHeight - 20) / bitmap.PixelHeight) * _zoom;
        PhotoImage.Width = bitmap.PixelWidth * scale; PhotoImage.Height = bitmap.PixelHeight * scale;
        ZoomOut.IsEnabled = _zoom > 1; ZoomIn.IsEnabled = _zoom < 8;
    }
    private void PhotoSizeChanged(object sender, SizeChangedEventArgs e) => SizePhoto();
    private void ZoomInClick(object sender, RoutedEventArgs e) { _zoom = Math.Min(8, _zoom * 1.5); SizePhoto(); }
    private void ZoomOutClick(object sender, RoutedEventArgs e) { _zoom = Math.Max(1, _zoom / 1.5); SizePhoto(); }
    private void FitClick(object sender, RoutedEventArgs e) { _zoom = 1; SizePhoto(); PhotoScroll.ScrollToHome(); PhotoScroll.ScrollToLeftEnd(); }
    private void PreviousClick(object sender, RoutedEventArgs e) { if (_index > 0) Select(_index - 1); }
    private void NextClick(object sender, RoutedEventArgs e) { if (_index < _photos.Count - 1) Select(_index + 1); }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void CopyPhotoClick(object sender, RoutedEventArgs e)
    {
        if (!CopyPhoto.IsEnabled) return;
        PhotoCopied = CopyPhotoAsync(_photos[_index], _generation);
    }
    private async Task CopyPhotoAsync(SessionPhoto photo, int generation)
    {
        CopyPhoto.IsEnabled = false;
        CopyNotice.Visibility = Visibility.Visible; CopyNotice.Text = "Copying photo…";
        try
        {
            // Copy original-resolution pixels, not the 4096px preview, zoomed view or file path.
            // The existing raster-size/file-size safeguards still apply.
            var original = await Task.Run(() => SessionLogMedia.Load(photo.Path, int.MaxValue));
            if (generation != _generation) return;
            if (original is null)
            {
                CopyNotice.Text = "Couldn't copy this photo. The original file may have moved or become unreadable.";
                return;
            }
            _clipboard.Copy(original);
            CopyNotice.Text = "Photo copied.";
        }
        catch (ExternalException)
        {
            if (generation == _generation) CopyNotice.Text = "Clipboard is busy. Try Copy again.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            if (generation == _generation) CopyNotice.Text = "Couldn't copy this photo. Please try again.";
        }
        finally
        {
            if (generation == _generation) CopyPhoto.IsEnabled = PhotoImage.Source is not null;
        }
    }
    private void PhotoKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        { CopyPhotoClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Left) { PreviousClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.Right) { NextClick(sender, e); e.Handled = true; }
    }
}
