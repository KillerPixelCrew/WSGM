using System;
using System.Threading.Tasks;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>A single pin action for a declared widget, with confirmed preference readback.</summary>
internal sealed class PluginWidgetPinControls : CardButton
{
    internal PluginWidgetPinControls(string title, Func<bool, Task> save, Func<Task<bool>>? read = null)
    {
        Title = title;
        IconGeometry = Icons.Pin;
        var pinned = false;
        var busy = false;
        ShowState();
        AttachedToVisualTree += async (_, _) =>
        {
            if (read is null) { return; }
            IsEnabled = false;
            try { pinned = await read(); ShowState(); }
            catch (Exception ex) { Description = "Could not read pin: " + ex.Message; }
            finally { IsEnabled = true; }
        };
        Click += async (_, _) =>
        {
            if (busy) { return; }
            busy = true;
            IsEnabled = false;
            try
            {
                if (read is not null) { pinned = await read(); }
                var next = !pinned;
                await save(next);
                pinned = next;
                ShowState();
            }
            catch (Exception ex) { Description = "Pin change failed: " + ex.Message; }
            finally { busy = false; IsEnabled = true; }
        };
        return;

        void ShowState()
        {
            IsPinned = pinned;
            Description = pinned ? "Pinned to Quick access - Select to unpin" : "Select to pin to Quick access";
        }
    }
}
