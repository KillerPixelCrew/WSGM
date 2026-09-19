using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace WSGM.OverlayMockup;

internal sealed partial class MockupWindow
{
    private bool _integration = true;

    private Control QuickPage()
    {
        Control SessionCard()
        {
            var resume = ActionButton("Back to play  ↗", () => Notice("Resume requested · preview only"), true);
            var hero = Card(Stack(
                Text("YOUR SESSION", 11, true, FontWeight.SemiBold),
                Text("Steam Big Picture", 22, weight: FontWeight.SemiBold),
                Text("Your session, at a glance.", 13, true),
                Flow(120, Stat("82%", "Battery"), Stat("17 W", "Balanced"), Stat("60 fps", "Frame limit")),
                resume));
            hero.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.Parse("#E6403429"), 0),
                    new GradientStop(Color.Parse("#E62C2C2C"), 1)
                }
            };
            return hero;
        }

        Control ComfortCard()
        {
            return Card(Stack(
                SectionTitle("Comfort first", "Your everyday adjustments, one touch away."),
                Range("brightness", "Brightness", 68, 0, 100, "%"),
                Range("volume", "Volume", 42, 0, 100, "%"),
                Row("Night light", Toggle("night-light", "Night light"))));
        }

        return SplitPage([
            ("Your session", () => ShowDetail("Your session", SessionCard())),
            ("Comfort first", () => ShowDetail("Comfort first", ComfortCard())),
            ("Power & cooling", () => ShowDetail("Power & cooling", Stack(
                Text("Power, cooling and battery controls."),
                ActionButton("Open Device", () => Navigate("Device"))))),
            ("Your library", () => ShowDetail("Your library", Stack(
                Text("Library and per-game launch options."),
                ActionButton("Open Steam", () => Navigate("Steam"))))),
            ("Step away", () => ShowDetail("Step away", Stack(
                Text("Sleep, switch modes or end your session."),
                ActionButton("Open power menu", ShowPowerMenu))))
        ]);
    }

    private Control DevicePage()
    {
        var sections = new List<(string Title, Action Open)>();
        if (_integration)
        {
            sections.Add(("Power limits", () => ShowDetail("Power limits", PowerLimitsCard())));
            sections.Add(("Fans & thermals", () => ShowDetail("Fans & thermals", FansCard())));
            sections.Add(("Battery & charging", () => ShowDetail("Battery & charging", ChargingCard())));
        }

        sections.Add(("Windows power", () => ShowDetail("Windows power", WindowsPowerCard())));
        sections.Add(("Display", () => ShowDetail("Display", DisplayCard())));
        sections.Add(("Performance", ShowPerformance));
        sections.Add(("Controller", ShowController));
        if (_integration)
        {
            sections.Add(("Lighting", ShowLighting));
            sections.Add(("Device info", ShowDeviceInfo));
        }

        return SplitPage(sections);
    }

    private Control SplitPage(List<(string Title, Action Open)> sections)
    {
        _sectionButtons.Clear();
        _sectionDetail = new ContentControl();
        _selectedSection = _sectionSelections.GetValueOrDefault(_destination, sections[0].Title);
        var menu = new StackPanel { Spacing = 4 };
        foreach (var (title, open) in sections)
        {
            var button = ActionButton(title, () => SelectSection(title, open));
            var label = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            label.Children.Add(IconLabel(title));
            if (title == "Lighting")
            {
                var badge = new FAInfoBadge { Value = 3 };
                Grid.SetColumn(badge, 1);
                label.Children.Add(badge);
            }

            button.Content = label;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Classes.Add("nav");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Height = 48;
            _sectionButtons.Add(title, button);
            menu.Children.Add(button);
        }

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,2*") };
        split.Bind(HeightProperty, new Binding("Viewport.Height") { Source = _pageViewport });
        var sidebar = Card(new ScrollViewer
        {
            Content = menu,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        sidebar.Background = Brush("#CC202020");
        sidebar.BorderBrush = Brush("#665F5F5F");
        sidebar.Padding = new Thickness(10);
        split.Children.Add(sidebar);
        var divider = new Border { Width = 2, Background = Brush("#88FF9D3D"), Margin = new Thickness(0, 8) };
        Grid.SetColumn(divider, 1);
        split.Children.Add(divider);
        var detail = new ScrollViewer
        {
            Content = _sectionDetail,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var controls = Card(detail);
        controls.Background = Brush("#962C2C2C");
        controls.BorderBrush = Brush("#665F5F5F");
        Grid.SetColumn(controls, 2);
        split.Children.Add(controls);
        var first = sections.FirstOrDefault(x => x.Title == _selectedSection);
        if (first.Open is null)
        {
            first = sections[0];
        }

        SelectSection(first.Title, first.Open);
        return split;
    }

    private Control WindowsPowerCard()
    {
        return Card(Stack(
            SectionTitle("Windows power", "Available with or without a device plugin."),
            Row("Energy plan",
                Picker("windows-plan", "Windows energy plan", "Balanced", "Balanced", "Power saver",
                    "High performance")),
            Row("Keep awake", Toggle("keep-awake", "Keep awake"))));
    }

    private Control PowerLimitsCard()
    {
        var performance = new FASettingsExpander
        {
            Header = "Automatic power",
            Description = "Let AutoTDP follow your target.",
            Footer = Toggle("auto-tdp", "Automatic power"),
            IsExpanded = false
        };
        performance.Items.Add(Row("Target",
            Picker("target-fps", "Target frame rate", "60 fps", "30 fps", "40 fps", "60 fps", "90 fps")));
        performance.Items.Add(Row("Strategy",
            Picker("auto-strategy", "AutoTDP strategy", "Balanced", "Quiet", "Balanced", "Responsive")));
        return Card(Stack(
            SectionTitle("Power & performance", "Limits and automatic control"),
            Row("Hardware profile",
                Picker("hardware-profile", "Hardware profile", "Balanced", "Super Battery", "Balanced", "Performance",
                    "Custom")),
            performance,
            Range("tdp", "Sustained power", 17, 8, 37, "W"),
            Range("boost", "Boost power", 25, 8, 37, "W"),
            Row("Frame limit",
                Picker("frame-limit", "Frame limit", "60 fps", "Off", "30 fps", "40 fps", "60 fps", "90 fps",
                    "120 fps"))));
    }

    private Control FansCard()
    {
        return Card(Stack(
            SectionTitle("Fans & thermals", "Cooling and current readings"),
            Flow(100, Stat("62 °C", "CPU temperature"), Stat("2,140", "Fan 1 · rpm"), Stat("2,080", "Fan 2 · rpm")),
            Row("Fan mode", Picker("fan-mode", "Fan mode", "Automatic", "Automatic", "Custom", "Full speed")),
            ActionButton("Edit fan curve  ↗", ShowFanCurve)));
    }

    private Control ChargingCard()
    {
        return Card(Stack(
            SectionTitle("Battery & charging", "Charge limits and source profiles"),
            Range("charge-limit", "Charge limit", 80, 60, 100, "%"),
            Row("On battery",
                Picker("battery-profile", "Battery profile", "Balanced", "Super Battery", "Balanced", "Performance",
                    "Custom")),
            Row("Plugged in",
                Picker("ac-profile", "Plugged-in profile", "Performance", "Super Battery", "Balanced", "Performance",
                    "Custom"))));
    }

    private Control DisplayCard()
    {
        return Card(Stack(
            SectionTitle("Display", "Panel and refresh controls"),
            Range("brightness", "Brightness", 68, 0, 100, "%"),
            Row("Refresh rate", Picker("refresh", "Refresh rate", "120 Hz", "60 Hz", "120 Hz")),
            Row("Variable refresh", Toggle("vrr", "Variable refresh", true))));
    }

    private Control SteamPage()
    {
        return SplitPage([
            ("Library", () => ShowDetail("Library", Stack(
                Row("Library tabs", Toggle("library-tabs", "Library tabs", true)),
                Row("Custom artwork", Toggle("artwork", "Custom artwork", true)),
                ActionButton("Manage library folders",
                    () => Notice("Library folder manager selected · preview only"))))),
            ("Per-game launch fixes", () => ShowDetail("Per-game launch fixes", Stack(
                Row("Selected game",
                    Picker("launch-game", "Selected game", "Sample game", "Sample game", "Another game")),
                Row("De-elevated launch", Toggle("de-elevate", "De-elevated launch")),
                Row("Input lease", Toggle("lease", "Steam Input lease", true))))),
            ("Card manager", () => ShowStatus("Storage", true)),
            ("Open Steam", () => ShowDetail("Open Steam", Stack(
                Text("Return to your Steam library."),
                ActionButton("Open Steam", () => Notice("Open Steam requested · preview only")))))
        ]);
    }

    private Control ToolsPage()
    {
        return SplitPage([
            ("Display", () => ShowDetail("Display", DisplayCard())),
            ("Performance", ShowPerformance),
            ("Storage", () => ShowStatus("Storage", true)),
            ("System", () => ShowDetail("System", Stack(
                ActionButton("Task Manager", () => Notice("Task Manager requested · preview only")),
                ActionButton("Open keyboard", ShowKeyboard)))),
            ("Plugins", () => ShowDetail("Plugins", Stack(
                Banner("Independent by design", "These controls remain available when Device Integration is off."),
                Row("IR integration", Toggle("ir-plugin", "IR integration")),
                Row("Sample widget", Toggle("widget", "Sample widget", true))))),
            ("Controller ownership", ShowController)
        ]);
    }

    private Control PowerPage()
    {
        return SplitPage([
            ("Power actions", () => ShowDetail("Power actions", Stack(
                Text("Ready for a pause?", 24, weight: FontWeight.SemiBold),
                Text("Sleep, switch sessions or shut down. Actions here are simulated.", muted: true),
                ActionButton("Open power menu", ShowPowerMenu, true)))),
            ("Wake & idle", () => ShowDetail("Wake & idle", Stack(
                Row("Keep awake", Toggle("keep-awake", "Keep awake")),
                Row("Dim after",
                    Picker("dim", "Dim after", "5 minutes", "Never", "2 minutes", "5 minutes", "10 minutes")),
                Row("Sleep after",
                    Picker("sleep", "Sleep after", "30 minutes", "Never", "15 minutes", "30 minutes", "1 hour"))))),
            ("Session", () => ShowDetail("Session", Stack(
                Row("Mute when screen is off", Toggle("screen-mute", "Mute when screen is off", true)),
                Row("Restore on wake", Toggle("restore-wake", "Restore on wake", true)))))
        ]);
    }

    private void ShowPerformance()
    {
        ShowDetail("Performance", Stack(
            Row("Frame limit",
                Picker("frame-limit", "Frame limit", "60 fps", "Off", "30 fps", "40 fps", "60 fps", "90 fps",
                    "120 fps")),
            Row("On-screen display", Toggle("osd", "On-screen display", true)),
            Flow(120, Stat("60 fps", "Frame rate"), Stat("16.7 ms", "Frame time"), Stat("17 W", "Package power"))));
    }

    private void ShowController()
    {
        ShowDetail("Controller", Stack(
            Row("Button labels", Picker("glyphs", "Button labels", "Xbox", "Xbox", "Nintendo", "PlayStation")),
            Banner("Controller output",
                _integration
                    ? "Sample: Steam Deck target active."
                    : "Device Integration is off. No controller target is created."),
            Text("Physical gamepad capture and Steam Input handoff are outside this visual prototype.", muted: true)));
    }

    private void ShowLighting()
    {
        ShowDetail("Lighting", Stack(
            Range("rgb-brightness", "Brightness", 60, 0, 100, "%"),
            Row("Left ring", Picker("rgb-left", "Left ring", "Teal", "Teal", "Amber", "Violet", "White", "Off")),
            Row("Right ring", Picker("rgb-right", "Right ring", "Teal", "Teal", "Amber", "Violet", "White", "Off")),
            Row("Buttons", Picker("rgb-buttons", "Buttons", "White", "Teal", "Amber", "Violet", "White", "Off"))));
    }

    private void ShowFanCurve()
    {
        ShowDetail("Fan curve", Stack(
            Text("Custom fan response", 24, weight: FontWeight.SemiBold),
            Range("fan40", "At 40 °C", 20, 0, 100, "%"),
            Range("fan60", "At 60 °C", 45, 0, 100, "%"),
            Range("fan80", "At 80 °C", 80, 0, 100, "%"),
            ActionButton("Use custom curve", () =>
            {
                State("fan-mode", "Automatic").Value = "Custom";
                DismissDetail();
                Notice("Custom fan curve selected · preview only");
            }, true)));
    }

    private void ShowDeviceInfo()
    {
        ShowDetail("Device info", Stack(
            Text("MSI Claw 8 AI+ A2VM", 26, weight: FontWeight.SemiBold),
            Text("Content follows the existing Claw capability publication. All readings shown here are sample values.",
                muted: true),
            Flow(120, Stat("82%", "Battery"), Stat("62 °C", "CPU"), Stat("120 Hz", "Display"))));
    }
}
