using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>One Graphics group as a Quick Access pin target: a section's lead rows or one of its categories.</summary>
/// <param name="Id">The stable pin id.</param>
/// <param name="Title">The heading on the Graphics page, where the section is already named.</param>
/// <param name="PinTitle">The heading on Quick Access and in the pin list, which also names the section.</param>
/// <param name="Section">The section the group belongs to.</param>
/// <param name="Rows">The group's rows, in placement order.</param>
internal sealed record GraphicsPinSection(
    string Id,
    string Title,
    string PinTitle,
    GraphicsOverlaySection Section,
    IReadOnlyList<DeviceOverlayCapability> Rows);

/// <summary>The pin identity of the Graphics groups.</summary>
/// <remarks>
///     A group's id is <c>section.graphics.&lt;pluginId&gt;/&lt;sectionId&gt;</c> for the rows in no declared
///     category and that id plus <c>.category.&lt;categoryId&gt;</c> for a category, so it survives restarts,
///     driver updates and a second graphics package. An id is only ever matched against the current
///     snapshot, never parsed, so dots in a plugin or section id cannot make it ambiguous.
/// </remarks>
internal static class GraphicsSectionPins
{
    /// <summary>The prefix every Graphics pin id starts with.</summary>
    internal const string Prefix = "section.graphics.";

    /// <summary>The pin id of a section's lead rows, or of one of its categories.</summary>
    /// <param name="section">The section.</param>
    /// <param name="categoryId">The category, or null for the rows in none.</param>
    /// <returns>The id.</returns>
    internal static string Id(GraphicsOverlaySection section, string? categoryId = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        return categoryId is null ? Prefix + section.Key : Prefix + section.Key + ".category." + categoryId;
    }

    /// <summary>Every group of every section that has rows, in page order.</summary>
    /// <param name="snapshot">The Graphics snapshot, or null while no source is attached.</param>
    /// <returns>The groups.</returns>
    internal static IEnumerable<GraphicsPinSection> Build(GraphicsOverlaySnapshot? snapshot)
    {
        return snapshot is null ? [] : snapshot.Sections.SelectMany(Groups);
    }

    /// <summary>A section's groups: its uncategorized rows under the section's title, then each category.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The groups that have rows.</returns>
    internal static IEnumerable<GraphicsPinSection> Groups(GraphicsOverlaySection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var lead = section.Capabilities.Where(capability => capability.CategoryId is null
                                                            || section.Categories.All(category =>
                                                                category.Id != capability.CategoryId)).ToArray();
        if (lead.Length > 0)
        {
            yield return new GraphicsPinSection(Id(section), section.Title, section.Title, section, lead);
        }

        foreach (var category in section.Categories)
        {
            var rows = section.Capabilities.Where(capability => capability.CategoryId == category.Id).ToArray();
            if (rows.Length > 0)
            {
                yield return new GraphicsPinSection(Id(section, category.Id), category.Title,
                    $"{section.Title}: {category.Title}", section, rows);
            }
        }
    }

    /// <summary>Finds the group a pin id names in the current snapshot.</summary>
    /// <param name="snapshot">The Graphics snapshot, or null while no source is attached.</param>
    /// <param name="id">The pin id.</param>
    /// <returns>The group, or null when its publisher, section or category is absent now.</returns>
    internal static GraphicsPinSection? Resolve(GraphicsOverlaySnapshot? snapshot, string id)
    {
        return id.StartsWith(Prefix, StringComparison.Ordinal)
            ? Build(snapshot).FirstOrDefault(group => group.Id == id)
            : null;
    }
}
