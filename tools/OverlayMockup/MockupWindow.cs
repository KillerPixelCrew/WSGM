using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace WSGM.OverlayMockup;

internal sealed partial class MockupWindow : Window
{
    private readonly TextBlock _clock = Text("", 15, weight: FontWeight.SemiBold);
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _date = Text("", 11, true);
    private readonly string[] _destinations = ["Quick access", "Device", "Steam", "Tools", "Power"];
    private readonly TextBlock _feedback = Text("Interactive concept · Sample data", 12, true);
    private readonly TextBlock _heading = Text("Quick access", 23, weight: FontWeight.SemiBold);
    private readonly Border _keyboard = new() { IsVisible = false };
    private readonly Dictionary<string, Button> _navigation = [];
    private readonly ContentControl _page = new();

    private readonly ScrollViewer _pageViewport = new()
        { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    private readonly Dictionary<string, Control> _pages = [];
    private readonly Grid _root = new();
    private readonly Grid _shell = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
    private readonly TextBlock _subtitle = Text("A few essentials. Then back to your game.", muted: true);
    private readonly Border _surface = new() { IsVisible = false };
    private readonly Border _tint = new();
    private readonly TextBox _typing = new() { PlaceholderText = "Try typing here…", MinHeight = 44 };
    private readonly Dictionary<string, object> _values = [];
    private bool _closing;
    private string _destination = "Quick access";
    private bool _opaque;
    private string _profileScope = "Global";
    private InputElement? _returnFocus;

    public MockupWindow(string[] args)
    {
        Title = "WSGM · Overlay concept 114";
        Width = 1280;
        Height = 800;
        MinWidth = 980;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
        TransparencyBackgroundFallback = Brush("#232323");
        _opaque = args.Contains("--opaque");
        ApplyGlass();
        BuildShell();
        Content = _root;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        var pageIndex = Array.IndexOf(args, "--page");
        var initial = pageIndex >= 0 && pageIndex + 1 < args.Length ? args[pageIndex + 1] : "Quick access";
        Navigate(_destinations.FirstOrDefault(x => x.Equals(initial, StringComparison.OrdinalIgnoreCase)) ??
                 "Quick access");
        var captureIndex = Array.IndexOf(args, "--capture");
        _clockTimer.Tick += RefreshClock;
        RefreshClock(this, EventArgs.Empty);
        _clockTimer.Start();
        Closed += (_, _) =>
        {
            _gamepad?.Dispose();
            _clockTimer.Stop();
            _clockTimer.Tick -= RefreshClock;
        };
        Opened += async (_, _) =>
        {
            if (!args.Contains("--windowed") && captureIndex < 0)
            {
                WindowState = WindowState.FullScreen;
            }

            _navigation[_destination].Focus();
            if (captureIndex < 0)
            {
                _gamepad = new PreviewGamepad(() => IsActive && !_closing, CycleDestination, ControllerKey);
                _gamepad.Start();
            }

            if (args.Contains("--power-menu"))
            {
                ShowPowerMenu();
            }
            else if (args.Contains("--keyboard"))
            {
                ShowKeyboard();
            }

            if (captureIndex >= 0 && captureIndex + 1 < args.Length)
            {
                // A visual export of the prototype, not a production test or desktop capture.
                await Task.Delay(700);
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
                bitmap.Render(_root);
                bitmap.Save(args[captureIndex + 1], PngBitmapEncoderOptions.Default);
                File.WriteAllText(args[captureIndex + 1] + ".txt",
                    $"Actual native transparency: {ActualTransparencyLevel}\n");
                Close();
            }
        };
    }

    private void BuildShell()
    {
        _root.Children.Add(_tint);
        _root.Children.Add(_shell);
        _shell.Children.Add(BuildHeader());
        var navigation = BuildNavigation();
        Grid.SetRow(navigation, 1);
        _shell.Children.Add(navigation);
        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(24, 10, 24, 12)
        };
        Grid.SetRow(body, 2);
        _shell.Children.Add(body);
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(Stack(_heading, _subtitle, 4));
        body.Children.Add(header);
        _pageViewport.Content = _page;
        Grid.SetRow(_pageViewport, 1);
        body.Children.Add(_pageViewport);
        var dock = BuildDock();
        Grid.SetRow(dock, 2);
        body.Children.Add(dock);
        var footer = new Grid
            { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(_feedback);
        var hints = Text("LT / RT  Pages     D-pad  Move     A  Select     B  Back", 11, true);
        Grid.SetColumn(hints, 1);
        footer.Children.Add(hints);
        Grid.SetRow(footer, 3);
        body.Children.Add(footer);

        _root.Children.Add(_surface);
        _root.Children.Add(_keyboard);
        _surface.Background = Brush("#800E0E0E");
        _surface.Padding = new Thickness(36);
        _keyboard.VerticalAlignment = VerticalAlignment.Bottom;
        _keyboard.Background = Brush("#FA232323");
        _keyboard.Padding = new Thickness(32, 20);
        _keyboard.CornerRadius = new CornerRadius(24, 24, 0, 0);
    }

    private Control BuildNavigation()
    {
        var bar = new Grid { Margin = new Thickness(32, 4, 32, 0) };
        var navigation = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        navigation.Children.Add(ActionButton("LT", () => CycleDestination(-1)));
        for (var i = 0; i < _destinations.Length; i++)
        {
            var destination = _destinations[i];
            var button = ActionButton(destination, () => Navigate(destination));
            button.Content = IconLabel(destination);
            button.Classes.Add("nav");
            _navigation.Add(destination, button);
            navigation.Children.Add(button);
        }

        navigation.Children.Add(ActionButton("RT", () => CycleDestination(1)));
        bar.Children.Add(navigation);
        return bar;
    }

    private Control BuildHeader()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24, 8) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        brand.Children.Add(Text("wsgm", 22, weight: FontWeight.Bold));
        var profile = new ComboBox
        {
            ItemsSource = new[] { "Global profile", "Game · Sample game" },
            SelectedIndex = 0,
            MinWidth = 188,
            VerticalAlignment = VerticalAlignment.Center
        };
        NameControl(profile, "Current profile context");
        ToolTip.SetTip(profile, "Preview global defaults or a sample game's separate performance values");
        profile.SelectionChanged += (_, _) =>
        {
            _profileScope = profile.SelectedIndex == 0 ? "Global" : "Sample game";
            _pages.Clear();
            Navigate(_destination);
        };
        brand.Children.Add(profile);
        var options = ActionButton("◐", ShowPreviewOptions);
        NameControl(options, "Preview options");
        brand.Children.Add(options);
        header.Children.Add(brand);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var clock = Stack(_clock, _date, 1);
        clock.Margin = new Thickness(0, 0, 16, 0);
        status.Children.Add(clock);
        status.Children.Add(IconButton("Wi-Fi", () => ShowStatus("Connections")));
        status.Children.Add(IconButton("Bluetooth", () => ShowStatus("Bluetooth")));
        status.Children.Add(IconButton("Audio", () => ShowStatus("Audio")));
        status.Children.Add(IconButton("Brightness", () => ShowStatus("Brightness")));
        status.Children.Add(IconButton("Eject", () => ShowStatus("Storage")));
        status.Children.Add(IconButton("Keyboard", ShowKeyboard));
        status.Children.Add(IconButton("Close overlay", CloseDeferred));
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        return new Border
        {
            Background = Brush("#B01B1B1B"),
            BorderBrush = Brush("#34555555"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header
        };
    }

    private void RefreshClock(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        _clock.Text = now.ToString("HH:mm");
        _date.Text = now.ToString("ddd, dd MMM");
    }

    private void ShowPreviewOptions()
    {
        var integration = new ToggleSwitch { Content = "Device integration", IsChecked = true, FontSize = 12 };
        integration.IsCheckedChanged += (_, _) =>
        {
            _integration = integration.IsChecked == true;
            _pages.Remove("Device");
            if (_destination == "Device")
            {
                Navigate("Device");
            }
        };
        var glass = ActionButton("◐    Glass / solid", () =>
        {
            _opaque = !_opaque;
            ApplyGlass();
        });
        glass.HorizontalContentAlignment = HorizontalAlignment.Left;
        glass.Background = Brushes.Transparent;
        integration.IsChecked = _integration;
        ShowDetail("Preview options", Stack(integration, glass,
            Text(
                "MSI Claw 8 AI+ · Sample device\nF11: fullscreen / windowed\nPage Up / Page Down: previous / next destination",
                13, true)), false);
    }

    private Control BuildDock()
    {
        var dock = new Grid
            { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        var apps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        apps.Children.Add(ActionButton("▷  Steam", () => Notice("Steam selected · preview only")));
        apps.Children.Add(ActionButton("▣  Desktop", () => Notice("Desktop selected · preview only")));
        dock.Children.Add(apps);
        var status = Text("MSI Claw 8 AI+   ·   82%", 12, true);
        Grid.SetColumn(status, 1);
        dock.Children.Add(status);
        return dock;
    }

    private void Navigate(string destination)
    {
        _deviceHistory.Clear();
        _destination = destination;
        foreach (var (name, button) in _navigation)
        {
            button.Classes.Set("selected", name == destination);
        }

        (_heading.Text, _subtitle.Text) = destination switch
        {
            "Device" => ("Device", "Power, cooling, display and hardware controls."),
            "Steam" => ("Steam", "Library and per-game launch options."),
            "Tools" => ("Tools", "System controls and independent integrations."),
            "Power" => ("Power & session", "Sleep, wake and session changes."),
            _ => ("Quick access", "Pinned controls and current session.")
        };
        if (!_pages.TryGetValue(destination, out var page))
        {
            page = destination switch
            {
                "Device" => DevicePage(),
                "Steam" => SteamPage(),
                "Tools" => ToolsPage(),
                "Power" => PowerPage(),
                _ => QuickPage()
            };
            _pages.Add(destination, page);
        }

        _page.Content = page;
    }

    private void ApplyGlass()
    {
        _tint.Background = Brush(_opaque ? "#232323" : "#AD232323");
        TransparencyLevelHint = _opaque
            ? [WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
    }

    private void Notice(string message)
    {
        _feedback.Text = message;
    }

    private void OpenSurface(Control content, Control focus)
    {
        HideKeyboard();
        _returnFocus = FocusManager?.GetFocusedElement() as InputElement;
        _shell.IsEnabled = false;
        _surface.Child = content;
        _surface.IsVisible = true;
        KeyboardNavigation.SetTabNavigation(_surface, KeyboardNavigationMode.Cycle);
        Dispatcher.UIThread.Post(() => focus.Focus());
    }

    private void DismissSurface()
    {
        _surface.IsVisible = false;
        _surface.Child = null;
        _shell.IsEnabled = true;
        _shell.IsVisible = true;
        _returnFocus?.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.PageUp or Key.PageDown && !_surface.IsVisible && !_keyboard.IsVisible)
        {
            CycleDestination(e.Key == Key.PageUp ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_keyboard.IsVisible)
            {
                HideKeyboard();
            }
            else if (_surface.IsVisible)
            {
                DismissSurface();
            }
            else if (_deviceHistory.Count > 0)
            {
                BackFromDeviceSubpage();
            }
            else if (_destination != "Quick access")
            {
                Navigate("Quick access");
                _navigation["Quick access"].Focus();
            }
            else
            {
                CloseDeferred();
            }

            e.Handled = true;
        }
    }

    private async void CloseDeferred()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        IsEnabled = false;
        await Task.Delay(150);
        Close();
    }
}
