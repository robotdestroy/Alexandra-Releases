using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Alexandra.Infrastructure;

namespace Alexandra.App;

public sealed class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(LocalDiagnosticLog log)
    {
        Title = "Alexandra · Local debug logs";
        Width = 850; Height = 600; MinWidth = 500; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("WindowBrush");
        Foreground = (Brush)FindResource("TextPrimaryBrush");
        var panel = new DockPanel { Margin = new Thickness(18) };
        var note = new TextBlock { Text = "Local transcripts and research diagnostics. Generated replies may include words interrupted before playback.\n" + log.DirectoryPath,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls);
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = (FontFamily)FindResource("BodyFontFamily"), FontSize = 12, Background = Background, Foreground = Foreground };
        void Refresh()
        {
            try { text.Text = log.ReadRecent(); if (text.Text.Length == 0) text.Text = "No saved logs yet. Enable local logging in Setup, then talk to Alexandra."; }
            catch (Exception) { text.Text = "The local logs could not be read."; }
        }
        var refresh = new Button { Content = "Refresh" }; refresh.Click += (_, _) => Refresh(); controls.Children.Add(refresh);
        var export = new Button { Content = "Export logs…" };
        export.Click += (_, _) => {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export Alexandra debug logs", Filter = "Text file (*.txt)|*.txt",
                FileName = "Alexandra-debug.txt", OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            try { File.WriteAllText(dialog.FileName, log.ReadRecent()); }
            catch (Exception) { MessageBox.Show(this, "Could not export the logs.", "Alexandra"); }
        };
        controls.Children.Add(export);
        var clear = new Button { Content = "Clear saved logs" };
        clear.Click += (_, _) => {
            if (MessageBox.Show(this, "Delete all locally saved conversation debug logs? Exported copies are not removed.",
                "Clear logs", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { log.Clear(); Refresh(); } catch (Exception) { MessageBox.Show(this, "Could not clear the logs.", "Alexandra"); }
        };
        controls.Children.Add(clear);
        panel.Children.Add(text); Content = panel; Refresh();
    }
}

