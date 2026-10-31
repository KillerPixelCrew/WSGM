using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One group of a Device page: a heading, its pin identity and the rows drawn under it.</summary>
/// <param name="Id">
///     The persisted pin and expansion id: <c>section.device.plugin.&lt;section&gt;.main</c>, its
///     <c>.category.&lt;category&gt;</c> or its <c>.configuration</c> group.
/// </param>
/// <param name="Title">The heading, on the page and as a pin.</param>
/// <param name="SectionId">The Device page the group is drawn on.</param>
/// <param name="Capabilities">The device rows, already in presentation order.</param>
/// <param name="HostRows">The host-owned rows, in presentation order.</param>
/// <param name="OwnFold">
///     Whether the group folds on its own. False joins the shared fold of its page, which only the
///     Power page has; its fan group still folds on its own.
/// </param>
internal sealed record DeviceOverlayGroup(
    string Id,
    string Title,
    string SectionId,
    IReadOnlyList<DeviceOverlayCapability> Capabilities,
    IReadOnlyList<DescriptorRow> HostRows,
    bool OwnFold);
