using System;
using Avalonia.Controls;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>Retained command row whose current callback follows service publications.</summary>
internal sealed class OverlayActionButton : ActionButton, IOverlayRefreshable
{
    /// <summary>Routes each click to the latest activation callback.</summary>
    internal OverlayActionButton()
    {
        Click += (_, _) => Activate?.Invoke();
    }

    /// <summary>Current command callback; null makes activation a no-op without changing enabled presentation.</summary>
    internal Action? Activate { get; set; }

    /// <inheritdoc />
    public void RefreshFrom(Control replacement)
    {
        var next = (OverlayActionButton)replacement;
        CopyContentFrom(next);
        IsEnabled = next.IsEnabled;
        Activate = next.Activate;
        Classes.Set("primary", next.Classes.Contains("primary"));
        Classes.Set("danger", next.Classes.Contains("danger"));
    }
}
