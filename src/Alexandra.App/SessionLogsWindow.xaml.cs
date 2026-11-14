using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Diagnostics;
using System.Windows.Input;
using Alexandra.Infrastructure;
using Microsoft.Win32;

namespace Alexandra.App;

public partial class SessionLogsWindow : Window
{
    private readonly string _directory;
    private string? _selectedPath;
    private IReadOnlyList<SessionPhoto> _photos = [];
    private CancellationTokenSource? _photoLoad;
    private CancellationTokenSource? _searchLoad;
    private Entry[] _entries = [];
    private bool _ready, _closed;
    private SessionLogHeaderView? _header;
    internal Task PhotosLoaded { get; private set; } = Task.CompletedTask;
    internal Task SearchFinished { get; private set; } = Task.CompletedTask;
    public SessionLogsWindow(string directory, string? selectedPath = null)
    {
        InitializeComponent();
        _directory = Path.GetFullPath(directory);
        // Show the same location Explorer will actually open, not an app-container alias.
        try { FolderPath.Text = SessionLogFiles.ResolveFolder(_directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { FolderPath.Text = _directory; }
        _ready = true;
        Reload(selectedPath);
        Closed += (_, _) => { _closed = true; _searchLoad?.Cancel(); _searchLoad?.Dispose(); _photoLoad?.Cancel(); _photoLoad?.Dispose(); };
    }
    private sealed record Entry(string Path, string Label);
    private void Reload(string? selected)
    {
        try
        {
            _entries = SessionLogCatalog.List(_directory).Select(p => new Entry(p,
                DateTime.ParseExact(Path.GetFileNameWithoutExtension(p)[8..25], "yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToLocalTime().ToString("MMM d, yyyy · h:mm tt"))).ToArray();
            SearchFinished = FilterAsync(selected, delay: false);
            Notice.Text = "Logs stay local. The current session updates while you play.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _searchLoad?.Cancel(); _entries = []; ShowEntries([], null);
            Notice.Text = "Could not read the logs: " + ex.Message;
        }
    }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        SearchFinished = FilterAsync(_selectedPath, delay: true);
    }
    private async Task FilterAsync(string? selected, bool delay)
    {
        _searchLoad?.Cancel(); _searchLoad?.Dispose();
        _searchLoad = new CancellationTokenSource();
        var token = _searchLoad.Token;
        var query = SearchBox.Text.Trim();
        ClearSearch.IsEnabled = SearchBox.Text.Length > 0;
        if (query.Length == 0) { ShowEntries(_entries, selected); return; }
        SearchCount.Text = "Searching…";
        var entries = _entries;
        try
        {
            if (delay) await Task.Delay(200, token);
            var result = await Task.Run(() => SessionLogSearch.FilterAsync(entries.Select(e => e.Path).ToArray(), query, token), token);
            if (_closed || token.IsCancellationRequested) return;
            var paths = result.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            ShowEntries(entries.Where(e => paths.Contains(e.Path)).ToArray(), _selectedPath ?? selected);
            if (result.UnreadableCount > 0) SearchCount.Text += $" · {result.UnreadableCount} could not be searched";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void ShowEntries(Entry[] entries, string? selected)
    {
        LogList.ItemsSource = entries;
        LogList.SelectedItem = entries.FirstOrDefault(e => e.Path == selected) ?? entries.FirstOrDefault();
        SearchCount.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? $"{_entries.Length} session{(_entries.Length == 1 ? "" : "s")}" : $"{entries.Length} of {_entries.Length} sessions";
        if (entries.Length == 0)
        {
            _selectedPath = null; _photoLoad?.Cancel();
            _photos = []; PhotoThumbnails.ItemsSource = null; PhotoSection.Visibility = Visibility.Collapsed;
            ShowMessage(_entries.Length == 0 ? "No gameplay session logs yet." : "No matching sessions. Try another search or choose Clear.");
            SaveCopy.IsEnabled = false;
        }
    }
    private void ClearSearchClick(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
        { SearchBox.Clear(); e.Handled = true; }
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPath = (LogList.SelectedItem as Entry)?.Path;
        _photoLoad?.Cancel(); _photoLoad?.Dispose(); _photoLoad = null;
        _photos = []; PhotoThumbnails.ItemsSource = null; PhotoSection.Visibility = Visibility.Collapsed;
        SaveCopy.IsEnabled = _selectedPath is not null;
        if (_selectedPath is null) return;
        try
        {
            if (new FileInfo(_selectedPath).Length > 2 * 1024 * 1024)
            { ShowMessage("This log is too large for the viewer. Use Save a copy to read it in your Markdown editor."); return; }
            var rendered = new SessionLogMarkdown(_selectedPath, ActivateLink).Render(SessionLogPresentation.Format(File.ReadAllText(_selectedPath)),
                LogContent.ActualWidth > 32 ? LogContent.ActualWidth - 32 : 720);
            _header = rendered.Header;
            LogContent.Document = rendered.Document;
            LogContent.ScrollToHome();
            _photos = rendered.Photos;
            PhotoThumbnails.ItemsSource = _photos;
            PhotoHeading.Text = $"PHOTOS · {_photos.Count} · Click to enlarge";
            PhotoSection.Visibility = _photos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _photoLoad = new CancellationTokenSource();
            PhotosLoaded = LoadThumbnailsAsync(_photos, _photoLoad.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowMessage("This log could not be read: " + ex.Message); SaveCopy.IsEnabled = false; }
    }
    private void LogContentSizeChanged(object sender, SizeChangedEventArgs e) => _header?.UpdateWidth(e.NewSize.Width - 32);
    private void ShowMessage(string message)
    {
        _header = null;
        LogContent.Document = new FlowDocument(new Paragraph(new Run(message)))
            { FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 17, Foreground = Foreground };
    }
    private static async Task LoadThumbnailsAsync(IReadOnlyList<SessionPhoto> photos, CancellationToken token)
    {
        foreach (var photo in photos)
        {
            if (token.IsCancellationRequested) return;
            var image = await Task.Run(() => SessionLogMedia.Load(photo.Path, 320));
            if (token.IsCancellationRequested) return;
            photo.SetThumbnail(image);
        }
    }
    private void PhotoClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SessionPhoto photo) OpenPhoto(photo.Path);
    }
    private void OpenPhoto(string path)
    {
        var index = _photos.ToList().FindIndex(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) new SessionPhotoWindow(_photos, index) { Owner = this }.Show();
    }
    private void ActivateLink(string target)
    {
        if (_selectedPath is not null && SessionLogMedia.TryPhotoPath(target, _selectedPath, out var path)) { OpenPhoto(path); return; }
        if (!SessionLogMedia.TryWebLink(target, out var uri)) return;
        try { Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Notice.Text = "Couldn't open that web link. Your log is unchanged."; }
    }
    private void RefreshClick(object sender, RoutedEventArgs e) => Reload(_selectedPath);
    private void FolderClick(object sender, RoutedEventArgs e)
    {
        try { SessionLogFiles.OpenFolder(_directory); Notice.Text = "Opened the log folder in Explorer."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Notice.Text = "Explorer couldn't open this folder. You can still read logs here or save a copy."; }
    }
    private void SaveCopyClick(object sender, RoutedEventArgs e)
    {
        if (_selectedPath is null) return;
        var dialog = new SaveFileDialog { Title = "Save a session log copy", FileName = Path.GetFileName(_selectedPath),
            Filter = "Markdown session log (*.md)|*.md", DefaultExt = ".md", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Export, never relocate or delete the original journal.
            if (string.Equals(Path.GetFullPath(dialog.FileName), _selectedPath, StringComparison.OrdinalIgnoreCase)) return;
            File.WriteAllText(dialog.FileName, SessionLogPresentation.Format(File.ReadAllText(_selectedPath)), new System.Text.UTF8Encoding(false));
            Notice.Text = "Copy saved. Your original log is unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Notice.Text = "Couldn't save the copy: " + ex.Message; }
    }
}
