using System.Collections.Generic;
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
