using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace WSGM.OverlayMockup;

internal sealed partial class MockupWindow
{
    // One stroke vocabulary for navigation, status and section landmarks.
    private static readonly Dictionary<string, Geometry> IconGeometry = new()
    {
        ["home"] = Geometry.Parse("M3 10L12 3L21 10M5 9V21H10V14H14V21H19V9"),
        ["device"] =
            Geometry.Parse("M5 5H19L22 9V17L19 20H5L2 17V9Z M5 11H9M7 9V13 M16 10H17M18 13H19 M10 7H14V17H10Z"),
        ["steam"] = Geometry.Parse(
            "M3 16L9 19L17 10 M13 7A5 5 0 1 0 23 7A5 5 0 1 0 13 7 M6 17A3 3 0 1 0 12 17A3 3 0 1 0 6 17"),
        ["tools"] = Geometry.Parse("M14 3A6 6 0 0 0 8 11L2 17A3 3 0 0 0 7 22L13 16A6 6 0 0 0 21 10L17 12L13 8Z"),
        ["power"] = Geometry.Parse("M12 2V12 M6 5A9 9 0 1 0 18 5"),
        ["wifi"] = Geometry.Parse("M2 8Q12 0 22 8 M5 12Q12 6 19 12 M8 16Q12 12 16 16 M11 20H13"),
        ["bluetooth"] = Geometry.Parse("M7 6L17 16L12 21V3L17 8L7 18"),
        ["audio"] = Geometry.Parse("M3 9H7L12 4V20L7 15H3Z M16 8Q20 12 16 16 M19 4Q27 12 19 20"),
        ["brightness"] =
            Geometry.Parse(
                "M8 12A4 4 0 1 0 16 12A4 4 0 1 0 8 12 M12 1V4 M12 20V23 M1 12H4 M20 12H23 M4 4L6 6 M18 18L20 20 M4 20L6 18 M18 6L20 4"),
        ["eject"] = Geometry.Parse("M4 15L12 4L20 15Z M4 20H20"),
        ["keyboard"] =
            Geometry.Parse("M2 5H22V19H2Z M5 9H7M9 9H11M13 9H15M17 9H19 M5 12H7M9 12H11M13 12H15M17 12H19 M6 16H18"),
        ["close"] = Geometry.Parse("M5 5L19 19M19 5L5 19"),
        ["battery"] = Geometry.Parse("M2 6H19V18H2Z M22 9V15 M6 9V15M10 9V15M14 9V15"),
        ["fan"] = Geometry.Parse(
            "M10 12A2 2 0 1 0 14 12A2 2 0 1 0 10 12 M11 10Q3 0 4 9Q4 14 10 12 M14 11Q24 3 15 4Q10 4 12 10 M13 14Q21 24 20 15Q20 10 14 12 M10 13Q0 21 9 20Q14 20 12 14"),
        ["display"] = Geometry.Parse("M2 3H22V17H2Z M12 17V22M7 22H17"),
        ["performance"] = Geometry.Parse("M3 19V5M3 19H22M5 15L9 10L13 13L20 5"),
        ["lighting"] = Geometry.Parse("M13 2L5 14H11L10 22L20 9H13Z"),
        ["info"] = Geometry.Parse("M2 12A10 10 0 1 0 22 12A10 10 0 1 0 2 12 M12 10V17M12 6V7"),
        ["sleep"] = Geometry.Parse("M19 15A9 9 0 0 1 9 3A9 9 0 1 0 21 15Z"),
        ["restart"] = Geometry.Parse("M20 8A9 9 0 1 0 21 15M20 2V8H14"),
        ["exit"] = Geometry.Parse("M10 3H3V21H10M8 12H22M17 7L22 12L17 17"),
        ["storage"] = Geometry.Parse("M4 3H17L20 7V21H4Z M7 3V8M11 3V8M15 3V8M8 13H16M8 17H16")
    };

    private static string IconKey(string label)
    {
        var text = label.ToLowerInvariant();
        if (text.Contains("wi-fi") || text.Contains("connection"))
        {
            return "wifi";
        }

        if (text.Contains("bluetooth"))
        {
            return "bluetooth";
        }

        if (text.Contains("audio"))
        {
            return "audio";
        }

        if (text.Contains("brightness") || text.Contains("comfort"))
        {
            return "brightness";
        }

        if (text.Contains("eject"))
        {
            return "eject";
        }

        if (text.Contains("keyboard"))
        {
            return "keyboard";
        }

        if (text.Contains("close"))
        {
            return "close";
        }

        if (text.Contains("quick"))
        {
            return "home";
        }

        if (text.Contains("fan") || text.Contains("cool"))
        {
            return "fan";
        }

        if (text.Contains("battery") || text.Contains("longer"))
        {
            return "battery";
        }

        if (text.Contains("performance") || text.Contains("balance"))
        {
            return "performance";
        }

        if (text.Contains("display") || text.Contains("view") || text.Contains("desktop"))
        {
            return "display";
        }

        if (text.Contains("lighting"))
        {
            return "lighting";
        }

        if (text.Contains("sleep") || text.Contains("hibernate") || text.Contains("while"))
        {
            return "sleep";
        }

        if (text.Contains("restart"))
        {
            return "restart";
        }

        if (text.Contains("sign out"))
        {
            return "exit";
        }

        if (text.Contains("power") || text.Contains("shut") || text.Contains("away"))
        {
            return "power";
        }

        if (text.Contains("steam") || text.Contains("library") || text.Contains("game mode"))
        {
            return "steam";
        }

        if (text.Contains("storage") || text.Contains("card manager"))
        {
            return "storage";
        }

        if (text.Contains("tools") || text.Contains("system") || text.Contains("plugin") || text.Contains("launch"))
        {
            return "tools";
        }

        if (text.Contains("info"))
        {
            return "info";
        }

        return "device";
    }

    private static Control CreateIcon(string label, double size = 22)
    {
        return new Path
        {
            Data = IconGeometry[IconKey(label)],
            Stroke = Brush("#FF9D3D"),
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Control IconLabel(string label, double size = 14)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(CreateIcon(label));
        row.Children.Add(Text(label, size));
        return row;
    }

    private static Button IconButton(string label, Action action)
    {
        var button = ActionButton(label, action);
        button.Content = CreateIcon(label, 20);
        button.Width = 46;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        NameControl(button, label);
        ToolTip.SetTip(button, label);
        return button;
    }
}
