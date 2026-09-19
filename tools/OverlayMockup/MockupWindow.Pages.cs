using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace WSGM.OverlayMockup;

internal sealed partial class MockupWindow
{
    private bool _integration = true;

    private Control QuickPage()
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
        var essentials = Card(Stack(
            SectionTitle("Comfort first", "Your everyday adjustments, one touch away."),
            Range("brightness", "Brightness", 68, 0, 100, "%"),
            Range("volume", "Volume", 42, 0, 100, "%"),
            Row("Night light", Toggle("night-light", "Night light"))));
        return Stack(
            Flow(340, hero, essentials),
            Flow(250,
                Tile("Power & cooling", "Find the balance that feels right.", () => Navigate("Device")),
                Tile("Your library", "Steam shortcuts and launch options.", () => Navigate("Steam")),
                Tile("Step away", "Sleep, switch modes or end your session.", ShowPowerMenu, "⏻")));
    }

    private Control DevicePage()
    {
        Control[] common =
        [
            Tile("Windows power", "Energy plan and keep-awake controls.",
                () => ShowDetail("Windows power", WindowsPowerCard())),
            Tile("Display", "Brightness, refresh rate and variable refresh.",
                () => ShowDetail("Display", DisplayCard())),
            Tile("Performance", "Frame limits and the on-screen display.", ShowPerformance),
            Tile("Controller", "Output and button labels.", ShowController)
        ];
        if (!_integration)
        {
            var banner = Banner("Device integration is off",
                "Windows controls and independent tools are still available.");
            return Stack(
                banner,
                new MenuTilePanel(_pageViewport, common, banner));
        }

        return new MenuTilePanel(_pageViewport, [
            Tile("Power limits", "Hardware profiles, AutoTDP and power limits.",
                () => ShowDetail("Power limits", PowerLimitsCard())),
            Tile("Fans & thermals", "Fan mode, curve and temperature readings.",
                () => ShowDetail("Fans & thermals", FansCard())),
            Tile("Battery & charging", "Charge limit and AC / battery profiles.",
                () => ShowDetail("Battery & charging", ChargingCard())),
            .. common,
            Tile("Lighting", "Colour, brightness and zones.", ShowLighting, count: 3),
            Tile("Device info", "MSI Claw 8 AI+ A2VM", ShowDeviceInfo)
        ]);
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
        return new MenuTilePanel(_pageViewport, [
            Tile("Library", "Tabs, artwork and library folders.", () => ShowDetail("Steam library", Stack(
                Row("Library tabs", Toggle("library-tabs", "Library tabs", true)),
                Row("Custom artwork", Toggle("artwork", "Custom artwork", true)),
                ActionButton("Manage library folders",
                    () => Notice("Library folder manager selected · preview only"))))),
            Tile("Per-game launch fixes", "Launch actions and input handoff.", () =>
                ShowDetail("Per-game launch fixes", Stack(
                    Row("Selected game",
                        Picker("launch-game", "Selected game", "Sample game", "Sample game", "Another game")),
                    Row("De-elevated launch", Toggle("de-elevate", "De-elevated launch")),
                    Row("Input lease", Toggle("lease", "Steam Input lease", true))))),
            Tile("Card manager", "A place for removable game libraries.", () => ShowStatus("Storage")),
            Tile("Open Steam", "Return to the library.", () => Notice("Open Steam requested · preview only"))
        ]);
    }

    private Control ToolsPage()
    {
        return new MenuTilePanel(_pageViewport, [
            Tile("Display", "Brightness, refresh rate and variable refresh.",
                () => ShowDetail("Display", DisplayCard())),
            Tile("Performance", "Frame limit, on-screen display and monitoring.", ShowPerformance),
            Tile("Storage", "Removable drives and library space.", () => ShowStatus("Storage")),
            Tile("System", "Task Manager, keyboard and desktop tools.", () => ShowDetail("System", Stack(
                ActionButton("Task Manager", () => Notice("Task Manager requested · preview only")),
                ActionButton("Open keyboard", () =>
                {
                    DismissSurface();
                    ShowKeyboard();
                })))),
            Tile("Plugins", "Independent integrations and widgets.", () => ShowDetail("Plugins", Stack(
                Banner("Independent by design", "These controls remain available when Device Integration is off."),
                Row("IR integration", Toggle("ir-plugin", "IR integration")),
                Row("Sample widget", Toggle("widget", "Sample widget", true))))),
            Tile("Controller ownership", "See which surface has your input.", ShowController)
        ]);
    }

    private Control PowerPage()
    {
        var hero = Card(Stack(
            Text("ON YOUR TERMS", 11, true),
            Text("Ready for a pause?", 24, weight: FontWeight.SemiBold),
            Text("One place for sleep, session changes and a proper goodbye.", 13, true),
            ActionButton("Open power menu  ↗", ShowPowerMenu, true)));
        return Stack(hero, Flow(340,
            Card(Stack(SectionTitle("Wake & idle", "Automatic screen and sleep timing"),
                Row("Keep awake", Toggle("keep-awake", "Keep awake")),
                Row("Dim after",
                    Picker("dim", "Dim after", "5 minutes", "Never", "2 minutes", "5 minutes", "10 minutes")),
                Row("Sleep after",
                    Picker("sleep", "Sleep after", "30 minutes", "Never", "15 minutes", "30 minutes", "1 hour")))),
            Card(Stack(SectionTitle("Session", "Screen-off and wake behavior"),
                Row("Mute when screen is off", Toggle("screen-mute", "Mute when screen is off", true)),
                Row("Restore on wake", Toggle("restore-wake", "Restore on wake", true)),
                Text("Power actions in this mockup only show a confirmation. Nothing happens to your session.", 13,
                    true)))));
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
