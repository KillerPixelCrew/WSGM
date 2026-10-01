using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WSGM.Overlay;

/// <summary>A section heading with one explicit pin action for the whole block.</summary>
internal sealed class SectionPinHeader : Grid
{
    private readonly Button _pin = new() { MinHeight = 36 };

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
        SetColumn(_pin, 0);
        ColumnDefinitions = new ColumnDefinitions("Auto");
        RowDefinitions = new RowDefinitions("Auto");
        Children.Add(_pin);
    }

    internal void Refresh(bool pinned)
    {
        _pin.Content = pinned ? "Unpin section" : "Pin section";
        AutomationProperties.SetName(_pin, (pinned ? "Unpin " : "Pin ") + Title);
    }
}
