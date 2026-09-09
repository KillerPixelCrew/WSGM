using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;

namespace WSGM.Overlay;

/// <summary>Explicit pin edits for a declared widget; constructing the view never changes preferences.</summary>
internal sealed class PluginWidgetPinControls : StackPanel
{
    internal PluginWidgetPinControls(string title, Func<bool, Task> save)
    {
        Spacing = 4;
        Children.Add(new TextBlock { Text = title, Classes = { "setting-title" } });
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button pin = new() { Content = "Pin widget" };
        Button unpin = new() { Content = "Unpin widget" };
        TextBlock status = new() { Classes = { "caption" } };
        buttons.Children.Add(pin);
        buttons.Children.Add(unpin);
        Children.Add(buttons);
        Children.Add(status);
        bool busy = false;
        async Task SetAsync(bool pinned)
        {
            if (busy) { return; }
            busy = true;
            pin.IsEnabled = unpin.IsEnabled = false;
            try { await save(pinned); status.Text = pinned ? "Pinned to Quick Access" : "Widget unpinned"; }
            catch (Exception ex) { status.Text = "Pin change failed: " + ex.Message; }
            finally { busy = false; pin.IsEnabled = unpin.IsEnabled = true; }
        }
        pin.Click += async (_, _) => await SetAsync(true);
        unpin.Click += async (_, _) => await SetAsync(false);
    }
}
