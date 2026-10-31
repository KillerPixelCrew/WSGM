using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>A bounded shared-performance snapshot for one overlay projection.</summary>
/// <param name="Visible">Whether the performance section is available for presentation.</param>
/// <param name="Status">Service availability or operation detail.</param>
/// <param name="Rows">Value controls shown with Device power and available to pinning.</param>
/// <param name="ProfileRows">Per-application profile controls; the enable toggle heads the Device overview.</param>
internal sealed record PerformanceOverlaySnapshot(
    bool Visible,
    string Status,
    IReadOnlyList<DescriptorRow> Rows,
    IReadOnlyList<DescriptorRow> ProfileRows)
{
    /// <summary>The id of the per-application enable toggle among <see cref="ProfileRows" />.</summary>
    /// <remarks>
    ///     The header's profile switch and this row are two placements of one state, the running
    ///     application's profile scope, and both write it through the same command.
    /// </remarks>
    internal const string ApplicationProfileRowId = "application-profile";

    /// <summary>The Device page these rows are drawn on, beside the Overview.</summary>
    /// <remarks>The enable toggle in <see cref="ProfileRows" /> heads the Overview alone.</remarks>
    public string SectionId => DeviceSections.PowerId;

    /// <summary>The per-application enable toggle, or null when the snapshot has none.</summary>
    public DescriptorRow? ApplicationProfile =>
        ProfileRows.FirstOrDefault(row => string.Equals(row.Id, ApplicationProfileRowId, StringComparison.Ordinal));

    /// <summary>The rows a Device page draws: the profile rows without the toggle that heads the Overview, then the values.</summary>
    public IEnumerable<DescriptorRow> PageRows =>
        ProfileRows.Where(row => !string.Equals(row.Id, ApplicationProfileRowId, StringComparison.Ordinal))
            .Concat(Rows);
}
