using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>
///     The overlay's "Load more" rows. A long list shows a chunk and one row that adds the next; after a chunk
///     the view itself draws, controller focus moves to the first row it added instead of staying on the more
///     row or falling back to the top.
/// </summary>
internal static class PagedRows
{
    /// <summary>The row that loads the next chunk.</summary>
    /// <param name="tag">The row's stable focus key.</param>
    /// <param name="detail">What is shown so far, such as "48 of 120", or empty.</param>
    /// <param name="load">Loads more, or null while a load runs.</param>
    /// <param name="title">The row's label.</param>
    /// <returns>The row.</returns>
    internal static Control More(string tag, string detail, Action? load, string title = "Load more")
    {
        return new OverlayActionButton
        {
            Title = title,
            Description = detail,
            IconGeometry = Icons.ArrowDown,
            Activate = load,
            IsEnabled = load is not null,
            Tag = tag
        };
    }

    /// <summary>The row that shows the next chunk of a list the view already holds.</summary>
    /// <param name="root">The list's container, searched for the first added row.</param>
    /// <param name="tag">The row's stable focus key.</param>
    /// <param name="detail">What is shown so far, such as "48 of 120".</param>
    /// <param name="added">The tag of the first row the next chunk adds.</param>
    /// <param name="grow">Grows the shown count and draws the list again.</param>
    /// <param name="title">The row's label.</param>
    /// <returns>The row.</returns>
    internal static Control More(Control root, string tag, string detail, object added, Action grow,
        string title = "Load more")
    {
        return More(tag, detail, () =>
        {
            grow();
            FocusAdded(root, added);
        }, title);
    }

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
