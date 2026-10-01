using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace WSGM.Overlay;

/// <summary>Re-reads a control's source on a timer while the control is attached and can be seen.</summary>
internal static class VisiblePoll
{
    /// <summary>Refreshes once on attach, then every interval until the control is detached.</summary>
    /// <param name="control">The control whose source has no change notification.</param>
    /// <param name="interval">Time between reads.</param>
    /// <param name="refresh">Reads the source and updates the control.</param>
    /// <remarks>
    ///     A hidden page keeps its controls in the tree for the sheet's life, so a tick while the parent
    ///     is not effectively visible is skipped.
    /// </remarks>
    internal static void Attach(Control control, TimeSpan interval, Action refresh)
    {
        DispatcherTimer timer = new() { Interval = interval };
        timer.Tick += (_, _) =>
        {
            if (control.GetVisualParent() is { IsEffectivelyVisible: false })
            {
                return;
            }

            refresh();
        };
        control.AttachedToVisualTree += (_, _) =>
        {
            refresh();
            timer.Start();
        };
        control.DetachedFromVisualTree += (_, _) => timer.Stop();
    }
}
