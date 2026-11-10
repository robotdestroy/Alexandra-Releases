using System.Windows;
using Alexandra.Core;

namespace Alexandra.App;

public partial class UpdatesWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly AppUpdateCoordinator _updates;
    private readonly Func<Task> _restart;
    private readonly CancellationToken _lifetime;
    private bool _closed;
    public UpdatesWindow(SettingsStore settings, AppUpdateCoordinator updates, Func<Task> restart, CancellationToken lifetime)
    {
        InitializeComponent();
        _settings = settings; _updates = updates; _restart = restart; _lifetime = lifetime;
        VersionText.Text = "Installed version: " + updates.CurrentVersion;
        AutomaticBox.IsChecked = settings.Value.AutoUpdateEnabled;
        updates.Changed += UpdateChanged;
        Closed += (_, _) => { _closed = true; updates.Changed -= UpdateChanged; };
        Render(updates.State);
    }
    private void UpdateChanged(AppUpdateState state)
    {
        if (!_closed) Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Render(state); }));
    }
    private void Render(AppUpdateState state)
    {
        StatusText.Text = state.Message;
        CheckButton.IsEnabled = state.Phase is not (UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Unavailable);
        Progress.Visibility = state.Phase is UpdatePhase.Checking or UpdatePhase.Downloading ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = state.Phase == UpdatePhase.Checking;
        Progress.Value = state.Percent ?? 0;
        RestartButton.Visibility = state.Phase == UpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AutomaticClick(object sender, RoutedEventArgs e)
    {
        var previous = _settings.Value.AutoUpdateEnabled;
        _settings.Value.AutoUpdateEnabled = AutomaticBox.IsChecked == true;
        try { _settings.Save(); }
        catch (Exception)
        {
            _settings.Value.AutoUpdateEnabled = previous; AutomaticBox.IsChecked = previous;
            StatusText.Text = "The update preference could not be saved.";
        }
    }
    private async void CheckClick(object sender, RoutedEventArgs e)
    {
        using var check = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        check.CancelAfter(TimeSpan.FromMinutes(5));
        try { await _updates.CheckAndDownloadAsync(check.Token); }
        catch (OperationCanceledException) { }
    }
    private async void RestartClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Restart Alexandra to install the downloaded update? This ends the current conversation.",
            "Alexandra update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        RestartButton.IsEnabled = false;
        try { await _restart(); }
        catch (Exception) { StatusText.Text = "Couldn't restart for the update. Close and reopen Alexandra when convenient."; RestartButton.IsEnabled = true; }
    }
}
