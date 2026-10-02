using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace WSGM.Overlay;

public abstract partial class OverlaySubView
{
    private protected static Control ToolTabs(string selected, params (string Id, string Label, Action Select)[] tabs)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var tab in tabs)
        {
            var button = (OverlayActionButton)Row(tab.Label, "", null, tab.Select);
            button.Tag = "tool.tab:" + tab.Id;
            button.MinWidth = 100;
            button.Margin = new Thickness(0, 0, 8, 8);
            button.Classes.Set("primary", tab.Id == selected);
            panel.Children.Add(button);
        }

        return panel;
    }

    private protected static Control PreviewCard(string id, string? image, string title, string description,
        Action open)
    {
        var card = new StackPanel
            { Width = 230, Spacing = 6, Margin = new Thickness(0, 0, 12, 12), Tag = "card:" + id };
        card.Children.Add(new OverlayPreviewImage(image) { Tag = "image:" + id });
        var button = Row(title, description, null, open);
        button.Tag = "item:" + id;
        card.Children.Add(button);
        return card;
    }

    private protected static Control ToggleRow(string label, bool value, Action<bool> changed, bool enabled = true)
    {
        return ChoiceRow(label, new[] { (false, "Off"), (true, "On") }, value, changed);
    }
}
