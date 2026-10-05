using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace WSGM.Overlay;

/// <summary>
///     What the overlay's "show more" rows share: after the next chunk is drawn, controller focus moves to
///     the first row it added instead of staying on the more row or falling back to the top.
/// </summary>
internal static class PagedRows
{
    /// <summary>Focuses the first focusable control tagged <paramref name="tag" /> once the new rows are laid out.</summary>
    /// <param name="root">The list's container.</param>
    /// <param name="tag">The tag of the first row the chunk added.</param>
    internal static void FocusAdded(Control root, object tag)
    {
        Dispatcher.UIThread.Post(() => root.GetLogicalDescendants().OfType<Control>()
            .FirstOrDefault(control => control.Focusable && Equals(control.Tag, tag))
            ?.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
    }
}
