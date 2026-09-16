using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace WSGM.Overlay;

/// <summary>A section heading with one explicit pin action for the whole block.</summary>
internal sealed class SectionPinHeader : Grid
{
    private readonly Button _pin = new() { MinHeight = 32 };
    private readonly string _title;

    internal SectionPinHeader(string id, string title, Action<string> toggle, bool pinnedSurface)
    {
        SectionId = id;
        _title = title;
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        ColumnSpacing = 12;
        Margin = new Thickness(2, 0, 2, 4);
        Children.Add(new TextBlock
            { Text = title, Classes = { "eyebrow" }, VerticalAlignment = VerticalAlignment.Center });
        _pin.Tag = pinnedSurface ? "pin:" + id : id;
        _pin.Click += (_, _) => toggle(id);
        SetColumn(_pin, 1);
        Children.Add(_pin);
    }

    internal string SectionId { get; }

    internal void Refresh(bool pinned)
    {
        _pin.Content = pinned ? "Unpin section" : "Pin section";
        AutomationProperties.SetName(_pin, (pinned ? "Unpin " : "Pin ") + _title);
    }
}
