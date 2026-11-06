using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;

namespace WSGM.Settings;

/// <summary>Marks the rows whose editors no longer show what was last loaded or saved.</summary>
/// <remarks>
///     A row is compared with itself, by what its editors show, so a row created later (a plugin, a startup
///     app) starts unmarked. The mark only shows while Save has something to write, so an editor that a
///     service moved without changing a saved value never reads as an edit.
/// </remarks>
internal sealed class SettingsRowMarks
{
    private readonly ConditionalWeakTable<Border, string> _saved = new();

    /// <summary>Takes what every row shows now as its saved state.</summary>
    /// <param name="pages">Every Settings page, shown or not.</param>
    internal void Record(IEnumerable<Control> pages)
    {
        foreach (var row in pages.SelectMany(Rows))
        {
            _saved.AddOrUpdate(row, StateOf(row));
        }
    }

    /// <summary>Marks every row that changed, or none when Save has nothing to write.</summary>
    /// <param name="pages">Every Settings page, shown or not.</param>
    /// <param name="unsaved">Whether Save has anything to write.</param>
    internal void Mark(IEnumerable<Control> pages, bool unsaved)
    {
        foreach (var row in pages.SelectMany(Rows))
        {
            var state = StateOf(row);
            if (!_saved.TryGetValue(row, out var saved))
            {
                _saved.Add(row, state);
                saved = state;
            }

            row.Classes.Set("pending", unsaved && saved != state);
        }
    }

    private static IEnumerable<Border> Rows(Control page)
    {
        return page.GetLogicalDescendants().OfType<Border>().Where(border => border.Classes.Contains("setting-row"));
    }

    private static string StateOf(Border row)
    {
        StringBuilder state = new();
        foreach (var editor in row.GetLogicalDescendants())
        {
            switch (editor)
            {
                case ToggleButton toggle:
                    state.Append(toggle.IsChecked).Append('|');
                    break;
                case SelectingItemsControl choice:
                    state.Append(choice.SelectedIndex).Append('|');
                    break;
                case TextBox text:
                    state.Append(text.Text).Append('|');
                    break;
                case NumericUpDown number:
                    state.Append(number.Value).Append('|');
                    break;
                case RangeBase range:
                    state.Append(range.Value).Append('|');
                    break;
                // A card that shows its settings as text (a startup app) marks what that text says.
                case TextBlock text when text.Classes.Contains("settings-row-state"):
                    state.Append(text.IsEffectivelyVisible).Append(text.Text).Append('|');
                    break;
            }
        }

        return state.ToString();
    }
}
