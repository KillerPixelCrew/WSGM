using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Overlay;

/// <summary>One page of the Device destination as a description: its rail entry and its groups.</summary>
/// <param name="RailKey">
///     The stable rail key: <c>device.overview</c>, <c>device.gpu</c> or
///     <c>device.section.plugin.&lt;id&gt;</c>.
/// </param>
/// <param name="Title">The rail label.</param>
/// <param name="Icon">The declared section icon.</param>
/// <param name="SectionId">The published section the page draws, or null for the Overview and GPU pages.</param>
/// <param name="Groups">The groups, in presentation order.</param>
internal sealed record DevicePage(
    string RailKey,
    string Title,
    SectionIcon Icon,
    string? SectionId,
    IReadOnlyList<DeviceGroup> Groups)
{
    /// <summary>The device's lifecycle status and detail, drawn above the Overview's groups; null elsewhere.</summary>
    public (string Status, string Detail)? Heading { get; init; }

    /// <summary>The graphics publishers' notes, drawn under the GPU page's heading; null elsewhere.</summary>
    public string? Note { get; init; }

    /// <summary>Whether the page draws the button glyphs, whose input test holds the high-rate sample lease.</summary>
    public bool ObservesInput { get; init; }

    /// <summary>How many rows the rail badge counts.</summary>
    internal int Count => Groups.Sum(group => group.Count);

    /// <summary>Every group on the page, nested ones included, depth first.</summary>
    internal IEnumerable<DeviceGroup> AllGroups => Groups.SelectMany(group => group.Flatten());
}
