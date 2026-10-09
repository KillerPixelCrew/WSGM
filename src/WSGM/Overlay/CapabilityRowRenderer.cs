using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Labs.Panels;
using Avalonia.LogicalTree;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The capability-row rules the Device, Graphics and pinned sections share: what makes a row's layout
///     change, how readings group, how values refresh in place and which published row a commit may use.
/// </summary>
internal static class CapabilityRowRenderer
{
    // Marks the wrapping strip consecutive readings share. Not a string, so no focus or pin lookup sees it.
    private static readonly object ReadingsTag = new();

    /// <summary>Whether a republished row can keep its control, so focus and drafts survive.</summary>
    /// <remarks>Values never take part; a title, writability or category change still rebuilds the row.</remarks>
    /// <param name="before">Descriptor currently represented by the mounted control.</param>
    /// <param name="after">New published descriptor.</param>
    /// <returns>True when identity and layout fields match; values are ignored.</returns>
    internal static bool SameRowLayout(DeviceOverlayCapability before, DeviceOverlayCapability after)
    {
        return before.SameLayoutAs(after);
    }

    /// <summary>Whether two row lists keep their controls, row by row.</summary>
    /// <param name="before">Mounted rows in display order.</param>
    /// <param name="after">New published rows in display order.</param>
    /// <returns>True when counts and every corresponding row layout match.</returns>
    internal static bool SameRowLayouts(IReadOnlyList<DeviceOverlayCapability> before,
        IReadOnlyList<DeviceOverlayCapability> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }

        for (var index = 0; index < after.Count; index++)
        {
            if (!SameRowLayout(before[index], after[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     The published row a commit or action may use: the same row the user saw, still invokable and
    ///     from the same descriptor and device cycle; otherwise null.
    /// </summary>
    /// <remarks>
    ///     An editor can report after its row was republished, and the sources check only the row they are
    ///     given, so eligibility is rechecked here at invocation.
    /// </remarks>
    /// <param name="published">Current authoritative rows.</param>
    /// <param name="seen">Descriptor captured when the user began the action.</param>
    /// <returns>The current invokable row with matching identity and generations, otherwise null.</returns>
    internal static DeviceOverlayCapability? CurrentInvokable(IEnumerable<DeviceOverlayCapability> published,
        DeviceOverlayCapability seen)
    {
        var current = published.FirstOrDefault(candidate => candidate.GpuPluginId == seen.GpuPluginId
                                                            && candidate.CapabilityId == seen.CapabilityId
                                                            && candidate.InstanceId == seen.InstanceId);
        return current is { CanInvoke: true }
               && SameRowLayout(current, seen)
            ? current
            : null;
    }

    /// <summary>The row as the overlay presents it.</summary>
    /// <param name="capability">Published capability to label for the Overlay.</param>
    /// <returns>A copy labelled Firmware power mode for scenario controls, otherwise the original value.</returns>
    internal static DeviceOverlayCapability Present(DeviceOverlayCapability capability)
    {
        return capability.Role == CapabilityRole.ScenarioMode
            ? capability with { Title = "Firmware power mode" }
            : capability;
    }

    /// <summary>
    ///     Adds a row; a reading joins the wrapping strip the previous reading started, and any other row
    ///     ends that strip.
    /// </summary>
    /// <param name="target">The section body.</param>
    /// <param name="row">The row control.</param>
    /// <param name="readingWidth">The minimum width of a reading, or null for a full-width row.</param>
    internal static void AddRow(Panel target, Control row, double? readingWidth)
    {
        if (readingWidth is not { } width)
        {
            target.Children.Add(row);
            return;
        }

        row.MinWidth = width;
        Flex.SetGrow(row, 1);
        var count = target.Children.Count;
        if (count == 0 || target.Children[count - 1] is not FlexPanel strip || strip.Tag != ReadingsTag)
        {
            strip = new FlexPanel { Wrap = FlexWrap.Wrap, ColumnSpacing = 12, RowSpacing = 4, Tag = ReadingsTag };
            target.Children.Add(strip);
        }

        strip.Children.Add(row);
    }

    /// <summary>Refreshes every capability row under <paramref name="root" /> in place from the published rows.</summary>
    /// <param name="root">The section or pane holding the rows.</param>
    /// <param name="published">The rows as currently published.</param>
    /// <param name="marker">The temperature marker for curve rows, if any.</param>
    internal static void RefreshValues(Control root, IEnumerable<DeviceOverlayCapability> published, int? marker)
    {
        Dictionary<(string?, string, string?), DeviceOverlayCapability>? byRow = null;
        foreach (var view in root.GetLogicalDescendants().OfType<DeviceCapabilityControl>())
        {
            if (byRow is null)
            {
                byRow = [];
                foreach (var capability in published)
                {
                    byRow.TryAdd((capability.GpuPluginId, capability.CapabilityId, capability.InstanceId), capability);
                }
            }

            if (byRow.TryGetValue((view.GpuPluginId, view.CapabilityId, view.InstanceId), out var current))
            {
                view.Refresh(Present(current), marker);
            }
        }
    }
}
