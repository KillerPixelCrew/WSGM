using System.Collections.Generic;
using System.Linq;

namespace WSGM.Overlay;

/// <summary>One group of a Device, GPU or pinned page: a heading, how it folds, and its rows.</summary>
/// <param name="Id">
///     The stable pin and expansion id, such as <c>section.device.plugin.power.main</c>; a pinned copy scopes it
///     with <c>pin:</c>.
/// </param>
/// <param name="Title">The heading on the page.</param>
/// <param name="PinTitle">The name in the pin list and on Quick Access, or null when the group cannot be pinned.</param>
/// <param name="Fold">How the group is framed and folded.</param>
/// <param name="Rows">The rows, in presentation order.</param>
internal sealed record DeviceGroup(
    string Id,
    string Title,
    string? PinTitle,
    DeviceFold Fold,
    IReadOnlyList<DeviceRow> Rows)
{
    /// <summary>The nested groups of a <see cref="DeviceFold.Shared" /> fold or a GPU section, after its rows.</summary>
    public IReadOnlyList<DeviceGroup> Groups { get; init; } = [];

    /// <summary>The gap between the group's rows and nested groups.</summary>
    public double Spacing { get; init; } = 8;

    /// <summary>A current-value line under the fold's title, refreshed in place, or null.</summary>
    public string? Summary { get; init; }

    /// <summary>The number of rows the section rail counts for the group and its nested groups.</summary>
    internal int Count => Rows.Count(row => row.Counts) + Groups.Sum(group => group.Count);

    /// <summary>The group and its nested groups, depth first.</summary>
    internal IEnumerable<DeviceGroup> Flatten()
    {
        return Groups.SelectMany(group => group.Flatten()).Prepend(this);
    }

    /// <summary>Whether a control drawn for <paramref name="drawn" /> keeps its frame and rows for this group.</summary>
    /// <param name="drawn">The group the control was built from.</param>
    /// <returns>
    ///     True when heading, fold, spacing and every row keep their layout. Nested groups are matched on their
    ///     own, so a change in one of them leaves the fold and its other groups in place.
    /// </returns>
    internal bool SameLayoutAs(DeviceGroup drawn)
    {
        if (Id != drawn.Id || Title != drawn.Title || PinTitle != drawn.PinTitle || Fold != drawn.Fold
            || !Spacing.Equals(drawn.Spacing) || Rows.Count != drawn.Rows.Count
            || Summary is null != drawn.Summary is null)
        {
            return false;
        }

        for (var index = 0; index < Rows.Count; index++)
        {
            if (!Rows[index].SameLayoutAs(drawn.Rows[index]))
            {
                return false;
            }
        }

        return true;
    }
}
