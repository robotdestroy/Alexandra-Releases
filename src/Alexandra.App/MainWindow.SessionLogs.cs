using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Alexandra.Infrastructure;

namespace Alexandra.App;

public partial class MainWindow
{
    private void MostRecentSessionLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = SessionLogCatalog.FindMostRecent(SettingsStore.SessionLogDirectory);
            if (path is null)
            {
                const string message = "No session logs yet · play with session journals enabled in Setup";
                StatusText.Text = message;
                SetVoiceNotice(message); // Also visible in minimal mode; no cloud request.
                return;
            }
            new SessionLogsWindow(SettingsStore.SessionLogDirectory, path) { Owner = this }.Show();
        }
        catch (Exception ex) { ShowError(ex); }
    }
}
