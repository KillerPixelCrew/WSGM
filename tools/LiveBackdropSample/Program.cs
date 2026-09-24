using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LiveBackdrop;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace LiveBackdropSample;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args) => AppBuilder.Configure<SampleApplication>()
        .UsePlatformDetect()
        .LogToTrace()
        .StartWithClassicDesktopLifetime(args);
}

internal sealed class SampleApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CreateWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Window CreateWindow()
    {
        var window = new Window
        {
            Title = "Avalonia LiveBackdrop | Esc closes | F11 fullscreen",
            Width = 850,
            Height = 620,
            MinWidth = 580,
            MinHeight = 440,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            ShowInTaskbar = false,
            Topmost = true
        };
        var backdrop = LiveBackdrop.Attach(window);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var label = new TextBlock { Text = "Blur: 8 px", FontSize = 18 };
        var slider = new Slider { Minimum = 0, Maximum = 60, Value = 8, TickFrequency = 1, IsSnapToTickEnabled = true };
        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                backdrop.BlurRadius = slider.Value;
                label.Text = $"Blur: {slider.Value:0} px";
            }
        };
        var enabled = new CheckBox { Content = "Live backdrop enabled", IsChecked = true };
        enabled.IsCheckedChanged += (_, _) => backdrop.IsEnabled = enabled.IsChecked == true;
        var retry = new Button { Content = "Recreate backdrop" };
        retry.Click += (_, _) => backdrop.Retry();
        var hide = new Button { Content = "Hide for 2 seconds" };
        var returnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        returnTimer.Tick += (_, _) =>
        {
            returnTimer.Stop();
            window.Show();
        };
        hide.Click += (_, _) =>
        {
            window.Hide();
            returnTimer.Start();
        };
        window.Closed += (_, _) => returnTimer.Stop();
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => window.Close();
        var logPath = Path.Combine(AppContext.BaseDirectory, $"LiveBackdrop-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        void UpdateStatus()
        {
            status.Text = backdrop.IsActive ? "Live compositor backdrop active" : backdrop.FailureReason
                ?? (backdrop.IsEnabled ? $"Waiting for transparency: {window.ActualTransparencyLevel}" : "Disabled; opaque fallback");
            try
            {
                File.AppendAllText(logPath, $"{DateTime.Now:O} {status.Text}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        backdrop.StateChanged += (_, _) => UpdateStatus();
        window.Opened += (_, _) => UpdateStatus();
        window.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                window.Close();
            }
            else if (args.Key == Key.F11)
            {
                window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            }
        };
        window.Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(105, 20, 25, 35)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 220, 230, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = "Avalonia live desktop blur", FontSize = 28, Foreground = Brushes.White },
                    new TextBlock
                    {
                        Text = "Move other windows, open Steam or a game, then resize this window. Test the dropdown, hide/show and opaque fallback.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.White
                    },
                    label,
                    slider,
                    enabled,
                    new ComboBox { ItemsSource = new[] { "Popup above the glass", "Second option", "Third option" }, SelectedIndex = 0 },
                    new TextBox { PlaceholderText = "Type here to check focus and input" },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { retry, hide, close } },
                    status
                }
            }
        };
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        return window;
    }
}
