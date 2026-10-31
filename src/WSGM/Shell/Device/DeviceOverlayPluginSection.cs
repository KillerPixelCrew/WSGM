using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Shell;

/// <summary>One plugin-declared overlay section, projected for presentation.</summary>
/// <remarks>
///     Presentation-only: titles are already resolved from the WSGM-owned key vocabulary or bounded
///     plugin text, so no consumer of this record touches SDK display metadata again.
/// </remarks>
/// <param name="SectionId">Stable plugin section identity.</param>
/// <param name="Title">Resolved plain section title.</param>
/// <param name="Description">Resolved supporting text, empty when absent.</param>
/// <param name="Icon">Host-supported section icon.</param>
/// <param name="Categories">Category headings in declaration order; an empty list leaves a single lead group.</param>
internal sealed record DeviceOverlayPluginSection(
    string SectionId,
    string Title,
    string Description,
    SectionIcon Icon,
    IReadOnlyList<DeviceOverlayCategory> Categories)
{
    /// <summary>The subject this section claims, which is how a WSGM-owned section finds its home.</summary>
    /// <remarks>
    ///     Carried through from the declaration rather than inferred from the title: a plugin may call
    ///     its power page anything, and the key is the only part of the declaration that means the same
    ///     thing to WSGM. <see cref="SettingSectionKey.Custom" /> claims no subject and absorbs nothing.
    /// </remarks>
    public SettingSectionKey Key { get; init; } = SettingSectionKey.Custom;
}
