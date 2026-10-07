using System;
using Avalonia.Controls;

namespace WSGM.Controls;

/// <summary>
///     Commits a dropdown's choice once, when the user settles on it. Browsing an open dropdown with a
///     controller moves its selection through every item it passes, so a selection only commits after the
///     dropdown closes, or immediately when it changes while closed (keyboard or touch on the closed box).
/// </summary>
internal static class ComboCommit
{
    /// <summary>Attaches the commit rule to an existing dropdown.</summary>
    /// <typeparam name="T">The item type a committed choice must have.</typeparam>
    /// <param name="editor">The UI-thread dropdown; attach once for its lifetime because handlers are not detached.</param>
    /// <param name="refreshing">
    ///     True while the owner sets the selection itself; that selection becomes the baseline instead of
    ///     being committed.
    /// </param>
    /// <param name="commit">Runs on the UI thread for a changed, correctly typed choice while the editor is enabled.</param>
    internal static void Attach<T>(ComboBox editor, Func<bool> refreshing, Action<T> commit) where T : class
    {
        var committed = editor.SelectedItem;
        var open = false;
        editor.DropDownOpened += (_, _) => open = true;
        editor.DropDownClosed += (_, _) =>
        {
            open = false;
            Commit();
        };
        editor.SelectionChanged += (_, _) =>
        {
            if (refreshing())
            {
                committed = editor.SelectedItem;
            }
            else if (!open && !editor.IsDropDownOpen)
            {
                Commit();
            }
        };
        return;

        void Commit()
        {
            if (refreshing() || !editor.IsEnabled || editor.SelectedItem is not T choice || Equals(committed, choice))
            {
                return;
            }

            committed = choice;
            commit(choice);
        }
    }
}
