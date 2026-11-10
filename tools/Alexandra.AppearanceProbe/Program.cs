using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Alexandra.App;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static void Main(string[] args)
    {
        // No real settings, credentials, microphone, game capture, network or visible windows.
        var application = new App();
        application.InitializeComponent();
        var iconUri = new Uri("pack://application:,,,/Alexandra.App;component/Assets/alexandra-icon.ico");
        var iconDecoder = BitmapDecoder.Create(iconUri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Check(iconDecoder.Frames.Select(frame => frame.PixelWidth).Order().SequenceEqual(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }) &&
            iconDecoder.Frames.All(frame => frame.PixelWidth == frame.PixelHeight), "bundled app icon contains all nine Windows display sizes");
        var appIcon = BitmapFrame.Create(iconUri);
        var window = new Window { AllowsTransparency = true, WindowStyle = WindowStyle.None, Background = Brushes.Transparent };
        window.Icon = appIcon;
        var surface = new Grid();
        var header = new Border();
        var status = new StackPanel();
        var waveformArea = new Grid();
        window.Content = surface;
        void Apply(bool minimal, bool topmost) => WindowAppearance.Apply(window, surface, header, status, waveformArea, minimal, topmost);
        Apply(false, false);
        Check(((SolidColorBrush)surface.Background).Color.A == 255 && !window.Topmost, "opaque normal mode");
        Apply(true, true);
        var gradient = (RadialGradientBrush)surface.Background;
        Check(gradient.GradientStops[0].Color == Color.FromArgb(153, 0, 0, 0), "60% black center");
        Check(gradient.GradientStops[^1].Color.A == 0 && gradient.RadiusX < .5 && gradient.RadiusY < .5, "fully clear before all window edges");
        Check(header.Visibility == Visibility.Collapsed && status.Visibility == Visibility.Collapsed, "title, controls and bottom text hidden");
        Check(window.ResizeMode == ResizeMode.NoResize && !ToolTipService.GetIsEnabled(waveformArea), "no resize grip or waveform tooltip in minimal mode");
        Check(surface.Cursor == Cursors.SizeAll, "minimal surface advertises click-and-drag");
        Check(window.Opacity == 1 && surface.Opacity == 1 && window.Topmost, "opaque controls, topmost enabled");
        Check(ReferenceEquals(window.Icon, appIcon), "minimal mode retains the app icon for taskbar and app switching");
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle(gradient, null, new Rect(0, 0, 460, 250));
        var rendered = new RenderTargetBitmap(460, 250, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(drawing);
        var pixels = new byte[460 * 250 * 4];
        rendered.CopyPixels(pixels, 460 * 4, 0);
        byte Alpha(int x, int y) => pixels[(y * 460 + x) * 4 + 3];
        Check(Alpha(230, 125) == 153 && Alpha(105, 125) < 153 && Alpha(105, 125) > 0, "rendered center and gradual horizontal fade");
        Check(Alpha(10, 125) == 0 && Alpha(449, 125) == 0 && Alpha(230, 10) == 0 && Alpha(230, 239) == 0, "rendered clear margin on all four sides");
        Apply(true, false);
        Check(!window.Topmost && surface.Background is RadialGradientBrush, "independent keep-on-top toggle");
        Apply(false, false);
        Check(((SolidColorBrush)surface.Background).Color.A == 255, "restores opaque background");
        Check(header.Visibility == Visibility.Visible && status.Visibility == Visibility.Visible && window.ResizeMode == ResizeMode.CanResizeWithGrip && ToolTipService.GetIsEnabled(waveformArea), "restores title, controls, status and resizing");
        Check(surface.Cursor is null, "normal mode restores standard mouse cursor");
        Check(ReferenceEquals(window.Icon, appIcon), "normal mode retains the same app icon");
        CheckWindowSizing();

        var family = (FontFamily)application.FindResource("AlexandraFontFamily");
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Check(family.Source == "Segoe UI" && typeface.TryGetGlyphTypeface(out _), "public-source UI font loads from Windows without a bundled font");
        var button = new Button { Content = "Save", Style = (Style)application.FindResource(typeof(Button)) };
        Check(button.FontFamily.Source == family.Source && button.FontSize == 18 && button.BorderThickness == new Thickness(0), "flat UI buttons");
        surface.Children.Add(button);
        button.Measure(new Size(200, 60)); button.Arrange(new Rect(0, 0, 200, 60)); button.UpdateLayout();
        button.Foreground = (Brush)application.FindResource("ActiveTextBrush");
        button.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        var buttonLabel = Descendants(button).OfType<TextBlock>().First();
        Check(buttonLabel.Foreground == button.Foreground, "button label tracks inverted foreground");
        var menu = new MenuItem { Header = "Appearance", Style = (Style)application.FindResource(typeof(MenuItem)) };
        menu.ApplyTemplate(); menu.Foreground = button.Foreground;
        Check(((TextBlock)menu.Template.FindName("MenuHeader", menu)).Foreground == menu.Foreground, "menu label tracks inverted foreground");
        var text = new TextBlock { Style = (Style)application.FindResource(typeof(TextBlock)) };
        Check(text.FontFamily.Source == "Segoe UI" && text.FontSize == 17, "readable Segoe UI content");

        var directory = Path.Combine(Path.GetTempPath(), "Alexandra-AppearanceProbe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsStore(directory);
            Check(settings.Value.MinimalMode && settings.Value.KeepOnTop, "new profiles default to minimal mode and keep-on-top");
            Check(settings.Value.ThinkingSoundEnabled, "thinking sound defaults on for new profiles");
            Check(settings.Value.AutoUpdateEnabled && !settings.Value.AllowCloud, "automatic updates default on without enabling cloud voice consent");
            Check(!Directory.Exists(directory), "loading defaults does not write settings");
            Check(settings.Value.WindowScale == 1, "new and older profiles default to 100% size");
            settings.Value.MinimalMode = true;
            settings.Value.KeepOnTop = true;
            settings.Value.WindowScale = .75;
            settings.Value.Voice = "marin";
            settings.Value.KeyboardKey = 120;
            settings.Value.ControllerId = "synthetic-hotas";
            settings.Value.ControllerButton = 4;
            settings.Save();
            var restored = new SettingsStore(directory).Value;
            Check(restored.MinimalMode && restored.KeepOnTop, "appearance persists across launches");
            Check(restored.WindowScale == .75, "chosen window scale persists across launches");
            Check(restored.Voice == "marin" && restored.KeyboardKey == 120 && restored.ControllerId == "synthetic-hotas" && restored.ControllerButton == 4, "preserves voice and input settings");
            settings.Value.MinimalMode = false;
            settings.Value.ThinkingSoundEnabled = false;
            settings.Value.AutoUpdateEnabled = false;
            settings.SaveKey("synthetic-probe-not-a-real-credential");
            settings.Save();
            Check(!new SettingsStore(directory).Value.AutoUpdateEnabled, "automatic-update opt-out persists");
            var keyCheck = new SettingsStore(directory);
            keyCheck.SaveKey("");
            Check(keyCheck.ReadKey() == "synthetic-probe-not-a-real-credential", "encrypted credential survives settings changes and blank Setup saves");
            Check(!File.ReadAllText(Path.Combine(directory, "settings.json")).Contains("synthetic-probe", StringComparison.Ordinal), "credential is separate from general preferences");
            Check(!new SettingsStore(directory).Value.MinimalMode, "explicit normal-mode choice persists across launches");
            Check(!new SettingsStore(directory).Value.ThinkingSoundEnabled, "thinking sound can be disabled and stays disabled across launches");
            var fixture = Path.Combine(directory, "settings.json");
            File.WriteAllText(fixture, """{"Voice":"marin","KeyboardKey":120}""");
            var noAppearancePreference = new SettingsStore(directory).Value;
            Check(noAppearancePreference.ThinkingSoundEnabled, "existing profiles without a thinking-sound preference default on");
            Check(noAppearancePreference.MinimalMode && noAppearancePreference.Voice == "marin" && noAppearancePreference.KeyboardKey == 120,
                "profiles without an appearance preference use minimal mode without disturbing other settings");
            File.WriteAllText(fixture, """{"TransparentBackground":true,"KeepOnTop":true,"Voice":"marin","KeyboardKey":120}""");
            var migrated = new SettingsStore(directory);
            Check(migrated.Value.MinimalMode && migrated.Value.KeepOnTop && migrated.Value.Voice == "marin" && migrated.Value.KeyboardKey == 120, "migrates old transparent preference without disturbing other settings");
            migrated.Save();
            Check(!File.ReadAllText(fixture).Contains("TransparentBackground", StringComparison.Ordinal), "next save removes obsolete preference name");
            File.WriteAllText(fixture, """{"TransparentBackground":false}""");
            Check(!new SettingsStore(directory).Value.MinimalMode, "old opaque preference remains normal mode");
            File.WriteAllText(fixture, """{"TransparentBackground":true,"MinimalMode":false}""");
            Check(!new SettingsStore(directory).Value.MinimalMode, "explicit new preference takes precedence over legacy overlay");
            File.WriteAllText(fixture, """{"TransparentBackground":false,"MinimalMode":true}""");
            Check(new SettingsStore(directory).Value.MinimalMode, "explicit minimal mode takes precedence over legacy opaque preference");
            File.WriteAllText(fixture, """{"WindowScale":999}""");
            Check(WindowSizing.SizeForScale(new SettingsStore(directory).Value.WindowScale).Width == 920, "oversized saved preference is clamped safely");
        }
        finally
        {
            // Remove only the uniquely named fixture file created by this probe.
            var fixture = Path.Combine(directory, "settings.json");
            if (File.Exists(fixture)) File.Delete(fixture);
            var syntheticKey = Path.Combine(directory, "openai.key");
            if (File.Exists(syntheticKey)) File.Delete(syntheticKey);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
        SetupPreviewProbe.Run(Check);
        OnboardingProbe.Run(Check);
        AutoStartProbe.Run(Check);
        WindowPositionProbe.Run(Check);
        var notice = new Border(); var noticeText = new TextBlock();
        WindowAppearance.ApplyNotice(notice, noticeText, true, "Voice busy · retrying in 12s");
        Check(notice.Visibility == Visibility.Visible && noticeText.Text.Contains("12s"), "minimal mode exposes cooldown notices");
        WindowAppearance.ApplyNotice(notice, noticeText, true, "Voice is temporarily busy.");
        Check(notice.Visibility == Visibility.Visible, "minimal mode exposes final voice failures");
        WindowAppearance.ApplyNotice(notice, noticeText, false, "Voice is temporarily busy.");
        Check(notice.Visibility == Visibility.Collapsed, "normal mode uses its existing status instead of duplicating notices");
        WindowAppearance.ApplyNotice(notice, noticeText, true, null);
        Check(notice.Visibility == Visibility.Collapsed && noticeText.Text == "", "healthy minimal mode remains waveform-only");
        window.Close();
        application.Shutdown();
        Console.WriteLine($"All {_checks} appearance checks passed.");
        if (args.Contains("--play-thinking-sound", StringComparer.Ordinal))
        {
            // Explicit opt-in only. No voice connection, game data, recording or saved preferences.
            using var sound = new ThinkingSoundPlayer();
            sound.PlayPulse();
            Thread.Sleep(2200);
            sound.PlayPulse(); // Also surfaces any asynchronous device error from the first pulse.
            Thread.Sleep(150);
            sound.Stop(); // Exercise interruption while a pulse is in progress.
            Console.WriteLine("Thinking sound output and interruption smoke check completed.");
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        _checks++;
        Console.WriteLine("PASS: " + name);
    }

    private static void CheckWindowSizing()
    {
        Check(WindowSizing.SizeForScale(.5) == new Size(230, 125) && WindowSizing.SizeForScale(2) == new Size(920, 500), "50–200% sizing limits");
        Check(WindowSizing.ClampScale(-1) == .5 && WindowSizing.ClampScale(10) == 2 &&
            WindowSizing.ClampScale(double.NaN) == 1 && WindowSizing.ClampScale(double.PositiveInfinity) == 1, "invalid scales safely normalized");
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 2 })
        {
            var correct = true;
            for (var edge = 1; edge <= 8; edge++)
            {
                var input = new WindowSizing.NativeRect { Left = -600, Top = -100, Right = 100, Bottom = 300 };
                var output = WindowSizing.Constrain(input, edge, dpi, dpi, 1);
                var width = output.Right - output.Left;
                var height = output.Bottom - output.Top;
                correct &= Math.Abs(width / (460 * dpi) - height / (250 * dpi)) < .005;
                correct &= edge is 1 or 4 or 7 ? output.Right == input.Right : output.Left == input.Left;
                correct &= edge is 3 or 4 or 5 ? output.Bottom == input.Bottom : output.Top == input.Top;
            }
            Check(correct, $"all eight drag edges preserve proportions and anchors at {dpi * 100}% display DPI");
        }
        var tiny = WindowSizing.Constrain(new WindowSizing.NativeRect { Right = 10, Bottom = 10 }, 8, 1, 1, 1);
        var huge = WindowSizing.Constrain(new WindowSizing.NativeRect { Right = 10000, Bottom = 10000 }, 8, 1, 1, 1);
        Check(tiny.Right == 230 && tiny.Bottom == 125 && huge.Right == 920 && huge.Bottom == 500, "corner drags clamp both dimensions together");
        foreach (var scale in new[] { .5, 1d, 2 })
        {
            var design = new Grid { Width = 460, Height = 250 };
            var bar = new System.Windows.Shapes.Rectangle { Width = 301, Height = 64 };
            design.Children.Add(bar);
            var viewbox = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = design };
            var size = WindowSizing.SizeForScale(scale);
            viewbox.Measure(size); viewbox.Arrange(new Rect(size)); viewbox.UpdateLayout();
            var scaled = bar.TransformToAncestor(viewbox).TransformBounds(new Rect(bar.RenderSize));
            Check(Math.Abs(scaled.Width - 301 * scale) < .01 && Math.Abs(scaled.Height - 64 * scale) < .01,
                $"waveform geometry scales in both directions at {scale * 100}%");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
