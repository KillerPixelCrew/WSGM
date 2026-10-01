using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>A section heading with one explicit pin action for the whole block.</summary>
internal sealed class SectionPinHeader : Grid
{
    private readonly Button _pin = new() { Classes = { "deck-action", "section-pin" } };

    internal SectionPinHeader(string id, string title, Action<string> toggle, bool pinnedSurface)
    {
        SectionId = id;
        Title = title;
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        ColumnSpacing = 12;
        RowDefinitions = new RowDefinitions("Auto,Auto");
        RowSpacing = 6;
        Margin = new Thickness(0, 0, 0, 2);
        Children.Add(new TextBlock
        {
            Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        });
        var icon = new Path
        {
            Data = Icons.Pin, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        icon.Bind(Shape.StrokeProperty, new Binding(nameof(Button.Foreground)) { Source = _pin });
        _pin.Content = icon;
        _pin.Tag = pinnedSurface ? "pin:" + id : id;
        _pin.Click += (_, _) => toggle(id);
        SetColumn(_pin, 1);
        Children.Add(_pin);
        var divider = new Border { Height = 1, Classes = { "section-divider" } };
        SetRow(divider, 1);
        SetColumnSpan(divider, 2);
        Children.Add(divider);
    }

    internal string SectionId { get; }

    internal string Title { get; }

    internal void ShowPinOnly()
    {
        Children.Clear();
        Margin = default;
        RowSpacing = 0;
        VerticalAlignment = VerticalAlignment.Center;
        SetColumn(_pin, 0);
        ColumnDefinitions = new ColumnDefinitions("Auto");
        RowDefinitions = new RowDefinitions("Auto");
        Children.Add(_pin);
    }

    internal void Refresh(bool pinned)
    {
        _pin.Classes.Set("pinned", pinned);
        var action = (pinned ? "Unpin " : "Pin ") + Title;
        ToolTip.SetTip(_pin, action);
        AutomationProperties.SetName(_pin, action);
    }
}
