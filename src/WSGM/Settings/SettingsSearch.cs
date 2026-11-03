using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace WSGM.Settings;

/// <summary>One setting a search can find: a visible row of a section.</summary>
/// <param name="SectionIndex">The section that shows the row.</param>
/// <param name="Path">Where the row sits, such as "General › Start".</param>
/// <param name="Title">The row's title.</param>
/// <param name="Caption">The row's help line, which a search also reads.</param>
/// <param name="Value">The row's current value, such as "On", or empty.</param>
/// <param name="Row">The row, to scroll to and focus.</param>
internal sealed record SettingsSearchEntry(
    int SectionIndex,
    string Path,
    string Title,
    string Caption,
    string Value,
    Control? Row);

/// <summary>Finds settings by what the pages actually show.</summary>
/// <remarks>
///     Read from the pages rather than kept in a second list, so a row is searchable as soon as it exists
///     and a row hidden by its setting is not offered. A row inside a list that has never been shown has
///     not been created yet, so it is found only once its page has been opened.
/// </remarks>
internal static class SettingsSearch
{
    /// <summary>Reads the rows a section shows now.</summary>
    /// <param name="sectionIndex">The section's index.</param>
    /// <param name="sectionTitle">The section's title, for the path.</param>
    /// <param name="page">The section's page, whether or not it is on screen.</param>
    /// <returns>The rows, in page order.</returns>
    internal static IReadOnlyList<SettingsSearchEntry> Collect(int sectionIndex, string sectionTitle, Control page)
    {
        List<SettingsSearchEntry> entries = [];
        var group = string.Empty;
        Walk(page);
        return entries;

        void Walk(ILogical parent)
        {
            foreach (var child in parent.LogicalChildren)
            {
                if (child is not Control { IsVisible: true } control)
                {
                    continue;
                }

                if (control is Border header && header.Classes.Contains("section-header"))
                {
                    group = header.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty;
                    continue;
                }

                if (control is Border row && row.Classes.Contains("setting-row"))
                {
                    var text = row.GetLogicalDescendants().OfType<TextBlock>().ToArray();
                    var title = text.FirstOrDefault(block => block.Classes.Contains("setting-title"))?.Text;
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        var caption = text.FirstOrDefault(block => block.Classes.Contains("caption"))?.Text;
                        var path = group.Length == 0 ? sectionTitle : $"{sectionTitle} › {group}";
                        entries.Add(new SettingsSearchEntry(sectionIndex, path, title, caption ?? string.Empty,
                            ValueOf(row), row));
                    }

                    continue;
                }

                Walk(control);
            }
        }
    }

    /// <summary>The entries a query finds, the best first.</summary>
    /// <param name="entries">Everything searchable.</param>
    /// <param name="query">What was typed. Every word must appear; empty finds nothing.</param>
    /// <returns>Titles that start with the query, then titles that hold it, then the rest, in page order.</returns>
    internal static IReadOnlyList<SettingsSearchEntry> Find(IEnumerable<SettingsSearchEntry> entries, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [];
        }

        var whole = string.Join(' ', words);
        return
        [
            .. entries
                .Where(entry => words.All(word => Holds(entry.Title, word) || Holds(entry.Caption, word)
                                                                           || Holds(entry.Path, word)))
                .OrderBy(entry => entry.Title.StartsWith(whole, StringComparison.CurrentCultureIgnoreCase)
                    ? 0
                    : words.All(word => Holds(entry.Title, word))
                        ? 1
                        : 2)
        ];
    }

    private static bool Holds(string text, string word)
    {
        return text.Contains(word, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string ValueOf(Control row)
    {
        foreach (var editor in row.GetLogicalDescendants())
        {
            switch (editor)
            {
                case ToggleSwitch toggle:
                    return toggle.IsChecked == true ? "On" : "Off";
                case ComboBox { SelectedItem: ComboBoxItem { Content: { } content } }:
                    return content.ToString() ?? string.Empty;
                case ComboBox { SelectedItem: string choice }:
                    return choice;
            }
        }

        return string.Empty;
    }
}
