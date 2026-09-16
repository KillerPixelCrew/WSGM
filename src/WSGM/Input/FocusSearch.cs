using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace WSGM.Input;

/// <summary>Finds focus targets in a visual tree in document order.</summary>
internal static class FocusSearch
{
    /// <summary>The first control gamepad or keyboard focus may land on.</summary>
    /// <param name="root">The tree to search.</param>
    /// <returns>The first focusable, enabled and visible element that is not a text box, or null.</returns>
    /// <remarks>
    ///     Text boxes are skipped for the same reason D-pad traversal skips them: focusing one
    ///     pops the touch keyboard.
    /// </remarks>
    internal static InputElement? FirstNavigable(Visual root)
    {
        return First<InputElement>(
            root,
            element => element is { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true }
                and not TextBox);
    }

    /// <summary>The first descendant of a type that matches.</summary>
    /// <typeparam name="T">The kind of control to find.</typeparam>
    /// <param name="root">The tree to search.</param>
    /// <param name="match">Whether a candidate qualifies.</param>
    /// <returns>The first match, or null.</returns>
    internal static T? First<T>(Visual root, Func<T, bool> match)
        where T : Visual
    {
        foreach (var visual in root.GetVisualDescendants())
        {
            if (visual is T candidate && match(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
