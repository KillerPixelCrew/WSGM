using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>Draws a key combination as keycaps joined by plus signs: the Overlay's one keycap element.</summary>
/// <remarks>Styled by <c>Border.keycap</c> in <c>CommandDeck.axaml</c>; the panel is presentation only.</remarks>
internal static class Keycaps
{
    /// <summary>Draws a chord.</summary>
    /// <param name="keys">The keys in press order.</param>
    /// <param name="large">Whether to use the editor's larger keycaps.</param>
    /// <returns>The keycaps, named for assistive technology as the chord, such as Ctrl+Alt+S.</returns>
    internal static WrapPanel Create(IReadOnlyList<int> keys, bool large = false)
    {
        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Focusable = false
        };
        AutomationProperties.SetName(panel, HotkeyKeys.Describe(keys));
        for (var index = 0; index < keys.Count; index++)
        {
            if (index > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "+",
                    Classes = { "keycap-joiner" },
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0)
                });
            }

            var cap = new Border
            {
                Classes = { "keycap" },
                Child = new TextBlock
                {
                    Text = HotkeyKeys.Label(keys[index]),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Margin = new Thickness(0, 2)
            };
            cap.Classes.Set("large", large);
            panel.Children.Add(cap);
        }

        return panel;
    }
}
