using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentAvalonia.UI.Controls;
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

    /// <summary>
    ///     A page's view switcher: a segmented track of its views, one selected. It holds up to five views, wraps when
    ///     the pane is narrow, and each segment is its own focus stop named after its view.
    /// </summary>
    /// <param name="tag">The track's stable key; each segment is keyed <c>{tag}:{id}</c>.</param>
    /// <param name="selected">The current view.</param>
    /// <param name="views">Each view's id, label and selection.</param>
    /// <returns>The track.</returns>
    private protected static Border ViewTabs(string tag, string selected,
        params (string Id, string Label, Action Select)[] views)
    {
        var segments = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, label, select) in views)
        {
            var segment = Row(label, "", null, select);
            segment.Tag = tag + ":" + id;
            segment.Classes.Add("segment");
            segment.Classes.Set("selected", id == selected);
            segment.HorizontalAlignment = HorizontalAlignment.Left;
            segments.Children.Add(segment);
        }

        return new Border { Child = segments, Classes = { "deck-segments" }, Tag = tag };
    }

    /// <summary>A status chip in one of the shared badge tones.</summary>
    /// <param name="text">The chip's text.</param>
    /// <param name="tone"><c>good</c>, <c>warn</c>, <c>bad</c>, <c>info</c>, <c>action</c> or empty for neutral.</param>
    /// <returns>The chip.</returns>
    private protected static Border Chip(string text, string tone)
    {
        var chip = new Border
        {
            Classes = { "badge" },
            Child = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (tone.Length > 0)
        {
            chip.Classes.Add(tone);
        }

        return chip;
    }

    /// <summary>An attention message in the shared InfoBar.</summary>
    /// <param name="tag">The banner's stable key.</param>
    /// <param name="severity">Success, warning, error or informational.</param>
    /// <param name="title">A short title, or null for none.</param>
    /// <param name="message">The service's text.</param>
    /// <returns>The banner.</returns>
    private protected static FAInfoBar Banner(string tag, FAInfoBarSeverity severity, string? title, string message)
    {
        return new FAInfoBar
        {
            Tag = tag, IsOpen = true, IsClosable = false, IsIconVisible = true, Severity = severity,
            Title = title ?? "", Message = message, HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    /// <summary>A bordered card around a panel, keyed so its editors survive publications.</summary>
    /// <param name="content">The card's body.</param>
    /// <param name="tag">The card's stable key; include anything that changes its layout.</param>
    /// <returns>The card.</returns>
    private protected static Border Card(Panel content, string tag)
    {
        return new Border { Child = content, Classes = { "device-group" }, Tag = tag };
    }

    /// <summary>
    ///     The numbered steps of running work: done steps are checked, the current one is the action chip and later
    ///     ones are captions.
    /// </summary>
    /// <param name="steps">The step labels, in order.</param>
    /// <param name="active">The current step's index; beyond the last marks every step done, -1 none.</param>
    /// <returns>The step strip.</returns>
    private protected static WrapPanel StepChips(IReadOnlyList<string> steps, int active)
    {
        var strip = new WrapPanel { Orientation = Orientation.Horizontal };
        for (var index = 0; index < steps.Count; index++)
        {
            var done = index < active;
            var dot = Chip(done ? "✓" : (index + 1).ToString(CultureInfo.InvariantCulture),
                done ? "good" : index == active ? "action" : "");
            dot.Width = 24;
            dot.Height = 24;
            dot.CornerRadius = new CornerRadius(12);
            dot.Padding = default;
            if (dot.Child is TextBlock number)
            {
                number.HorizontalAlignment = HorizontalAlignment.Center;
            }

            var step = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 22, 4)
            };
            step.Children.Add(dot);
            var label = new TextBlock { Text = steps[index], VerticalAlignment = VerticalAlignment.Center };
            if (index != active)
            {
                label.Classes.Add("caption");
            }

            step.Children.Add(label);
            strip.Children.Add(step);
        }

        return strip;
    }

    private protected static Control ToggleRow(string label, bool value, Action<bool> changed, bool enabled = true)
    {
        var row = ChoiceRow(label, new[] { (false, "Off"), (true, "On") }, value, changed);
        row.IsEnabled = enabled;
        return row;
    }
}
