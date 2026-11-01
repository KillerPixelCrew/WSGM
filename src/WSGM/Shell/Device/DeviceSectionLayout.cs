using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Completes a published section list with the shared Device pages.</summary>
internal static class DeviceSectionLayout
{
    /// <summary>Adds predefined sections omitted by a plugin, preserving its category declarations.</summary>
    /// <param name="declared">A validated plugin section list.</param>
    /// <returns>The complete layout, including empty shared pages WSGM may populate.</returns>
    /// <remarks>
    ///     Descriptors may reference the predefined <see cref="DeviceSections" /> IDs without declaring their
    ///     sections. Empty pages need not be rendered. Custom sections still require explicit declarations.
    /// </remarks>
    internal static IReadOnlyList<CapabilitySection> IncludePredefined(IReadOnlyList<CapabilitySection> declared)
    {
        return
        [
            .. DeviceSections.All.Select(section => section with
                {
                    Categories = declared.FirstOrDefault(item => item.SectionId == section.SectionId)?.Categories ??
                                 section.Categories
                })
                .Concat(declared.Where(item =>
                    DeviceSections.All.All(section => section.SectionId != item.SectionId)))
        ];
    }
}
