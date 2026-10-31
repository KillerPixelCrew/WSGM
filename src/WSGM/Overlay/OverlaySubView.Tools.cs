using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using WSGM.Controls;

namespace WSGM.Overlay;

public abstract partial class OverlaySubView
{
    private protected static Control ToolTabs(string selected, params (string Id, string Label, Action Select)[] tabs)
    {
        return CommandStrip([
            .. tabs.Select(tab => Command("tool.tab:" + tab.Id, tab.Label, tab.Select, tab.Id == selected))
        ]);
    }

    /// <summary>One compact command for a <see cref="CommandStrip" />.</summary>
    /// <param name="tag">The stable focus key.</param>
    /// <param name="label">What the command reads.</param>
    /// <param name="onClick">What it does, or null while it is unavailable.</param>
    /// <param name="selected">Whether it is the current tab, tool or the primary command.</param>
    /// <returns>The command.</returns>
    private protected static ActionButton Command(string tag, string label, Action? onClick, bool selected = false)
    {
        var button = Row(label, "", null, onClick);
        button.Tag = tag;
        button.Classes.Set("primary", selected);
        return button;
    }

    /// <summary>
    ///     Lays commands out as one compact wrapping strip: tabs, tools and short actions. A command there is
    ///     its label, so a row's description and icon are dropped; other controls keep their own look.
    /// </summary>
    /// <param name="commands">The strip's commands and controls, in order.</param>
    /// <returns>The strip.</returns>
    private protected static WrapPanel CommandStrip(params Control[] commands)
    {
        var strip = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var command in commands)
        {
            if (command is ActionButton button)
            {
                button.Description = "";
                button.IconGeometry = null;
                button.MinWidth = 52;
                button.Padding = new Thickness(6, 6);
                button.HorizontalAlignment = HorizontalAlignment.Left;
            }

            command.Margin = new Thickness(0, 0, 8, 8);
            strip.Children.Add(command);
        }

        return strip;
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
        var row = ChoiceRow(label, new[] { (false, "Off"), (true, "On") }, value, changed);
        row.IsEnabled = enabled;
        return row;
    }
}
