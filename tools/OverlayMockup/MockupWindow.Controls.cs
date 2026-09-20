using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Labs.Panels;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace WSGM.OverlayMockup;

internal sealed partial class MockupWindow
{
    private static IBrush Brush(string color)
    {
        return new SolidColorBrush(Color.Parse(color));
    }

    private static TextBlock Text(string text, double size = 14, bool muted = false, FontWeight? weight = null)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (weight is { } fontWeight)
        {
            label.FontWeight = fontWeight;
        }

        if (muted)
        {
            label.Classes.Add("muted");
        }

        return label;
    }

    private static StackPanel Stack(params Control[] children)
    {
        return Stack(children, 12);
    }

    private static StackPanel Stack(Control first, Control second, double spacing)
    {
        return Stack([first, second], spacing);
    }

    private static StackPanel Stack(Control first, Control second, Control third, Control fourth, double spacing)
    {
        return Stack([first, second, third, fourth], spacing);
    }

    private static StackPanel Stack(IEnumerable<Control> children, double spacing)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return stack;
    }

    private static Button ActionButton(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label };
        NameControl(button, label);
        if (primary)
        {
            button.Classes.Add("primary");
        }

        button.Click += (_, _) => action();
        return button;
    }

    private static void NameControl(Control control, string name)
    {
        AutomationProperties.SetName(control, name);
    }

    private static Border Card(Control content, string role = "group")
    {
        var card = new Border { Child = content };
        card.Classes.Add(role);
        return card;
    }

    private static FlexPanel Flow(double basis, params Control[] children)
    {
        var panel = new FlexPanel
        {
            Wrap = FlexWrap.Wrap,
            ColumnSpacing = 12,
            RowSpacing = 12,
            AlignItems = AlignItems.FlexStart
        };
        foreach (var child in children)
        {
            child.MinWidth = basis;
            Flex.SetBasis(child, new FlexBasis(basis));
            Flex.SetGrow(child, 1);
            panel.Children.Add(child);
        }

        return panel;
    }

    private static Control Stat(string value, string label)
    {
        return Card(Stack(Text(value, 22, weight: FontWeight.SemiBold), Text(label, 11, true), 4), "stat");
    }

    private static Control SectionTitle(string title, string note)
    {
        // The navigation partial supplies this legacy note. Keep one sample notice in the shell instead.
        return note == "Preview controls · Changes stay in memory"
            ? Stack(Text(title, 20, weight: FontWeight.SemiBold))
            : Stack(Text(title, 16, weight: FontWeight.SemiBold), Text(note, 11, true), 4);
    }

    private Control Tile(string title, string note, Action action, string mark = "↗", int? count = null)
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(IconLabel(title, 15));
        Control trailing = count is { } value ? new FAInfoBadge { Value = value } : Text(mark, 24, true);
        Grid.SetColumn(trailing, 1);
        heading.Children.Add(trailing);
        var button = ActionButton(title, action);
        button.Content = Stack(heading, Text(note, 12, true), 8);
        button.Classes.Add("action-tile");
        return button;
    }

    private PreviewValue<T> State<T>(string key, T initial)
    {
        // Only performance/device overrides follow the preview profile. Windows controls stay global.
        if (key is "tdp" or "boost" or "hardware-profile" or "frame-limit" or "auto-tdp"
            or "target-fps" or "auto-strategy" or "fan-mode" or "fan40" or "fan60" or "fan80" or "vrr")
        {
            key = _profileScope + ":" + key;
        }

        if (_values.TryGetValue(key, out var value))
        {
            return (PreviewValue<T>)value;
        }

        var state = new PreviewValue<T>(initial);
        _values.Add(key, state);
        return state;
    }

    private Control Range(string key, string title, double initial, double min, double max, string unit)
    {
        var state = State(key, initial);
        var slider = new Slider
        {
            Minimum = min, Maximum = max, TickFrequency = 1, IsSnapToTickEnabled = true, MinHeight = 44, Height = 44
        };
        slider.Bind(RangeBase.ValueProperty,
            new Binding(nameof(state.Value)) { Source = state, Mode = BindingMode.TwoWay });
        NameControl(slider, title);
        var number = new FANumberBox { Minimum = min, Maximum = max, SmallChange = 1, Width = 112, MinHeight = 44 };
        number.Bind(FANumberBox.ValueProperty,
            new Binding(nameof(state.Value)) { Source = state, Mode = BindingMode.TwoWay });
        NameControl(number, title + " value");
        var readout = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        readout.Children.Add(number);
        readout.Children.Add(Text(unit, 14, true));
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(Text(title, weight: FontWeight.Medium));
        Grid.SetColumn(readout, 1);
        top.Children.Add(readout);
        return Card(Stack(top, slider, 4), "setting-row");
    }

    private ComboBox Picker(string key, string title, string initial, params string[] choices)
    {
        var state = State(key, initial);
        var picker = new ComboBox { ItemsSource = choices, MinWidth = 145, MaxWidth = 190 };
        picker.Bind(SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(state.Value)) { Source = state, Mode = BindingMode.TwoWay });
        NameControl(picker, title);
        return picker;
    }

    private ToggleSwitch Toggle(string key, string title, bool initial = false)
    {
        var state = State<bool?>(key, initial);
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 88 };
        toggle.Bind(ToggleSwitch.IsCheckedProperty,
            new Binding(nameof(state.Value)) { Source = state, Mode = BindingMode.TwoWay });
        NameControl(toggle, title);
        return toggle;
    }

    private static FASettingsExpanderItem Row(string title, Control editor, string? description = null)
    {
        return new FASettingsExpanderItem
        {
            Content = title,
            Description = description,
            Footer = editor,
            IsClickEnabled = false,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private static FAInfoBar Banner(string title, string message)
    {
        var banner = new FAInfoBar
        {
            Title = title, Message = message, IsOpen = true, IsClosable = false,
            Severity = FAInfoBarSeverity.Informational
        };
        banner.Classes.Add("banner");
        return banner;
    }

    private void ShowDetail(string title, Control content, bool allowSectionDetail = true)
    {
        if (allowSectionDetail && _sectionDetail is not null && (_selectingSection || !_surface.IsVisible))
        {
            ShowSectionDetail(title, content);
            return;
        }

        var back = ActionButton("← Back", DismissSurface);
        var pane = Card(new ScrollViewer
        {
            Content = Stack(back, Text(title, 20, weight: FontWeight.SemiBold), content)
        }, "utility-surface");
        var invoker = FocusManager?.GetFocusedElement() as Control;
        pane.HorizontalAlignment = HorizontalAlignment.Left;
        PositionUtility(pane, invoker);
        // The pane owns this handler, so dismissal leaves no window-level subscription behind.
        pane.LayoutUpdated += (_, _) => PositionUtility(pane, invoker);
        OpenSurface(pane, back);
    }

    private void PositionUtility(Border pane, Control? invoker)
    {
        var width = _root.Bounds.Width;
        var height = _root.Bounds.Height;
        var above = invoker?.Classes.Contains("tray-item") == true;
        var anchor = invoker?.TranslatePoint(new Point(0, above ? 0 : invoker.Bounds.Height), _root);
        pane.Width = Math.Min(520, Math.Max(0, width - 32));
        var left = Math.Clamp(anchor?.X ?? width - pane.Width - 16, 16,
            Math.Max(16, width - pane.Width - 16));
        var edge = Math.Clamp(above ? height - (anchor?.Y ?? height - 16) + 8 : (anchor?.Y ?? 64) + 8,
            16, Math.Max(16, height - 16));
        pane.MaxHeight = Math.Max(0, height - edge - 16);
        pane.VerticalAlignment = above ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        pane.Margin = above ? new Thickness(left, 16, 0, edge) : new Thickness(left, edge, 0, 16);
    }
}
