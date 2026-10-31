using System;
using System.Collections.Generic;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>
///     One reading of what the machine currently offers: the endpoints, and the capabilities of the
///     one endpoint that was asked about.
/// </summary>
/// <param name="Outputs">Render endpoints, in enumeration order.</param>
/// <param name="Inputs">Capture endpoints, in enumeration order.</param>
/// <param name="EndpointId">The endpoint the capabilities below describe, or null when none was read.</param>
/// <param name="Formats">The device formats that endpoint reports, or null when the read failed.</param>
/// <param name="SpatialFormats">The spatial formats that endpoint reports, or null when the read failed.</param>
internal sealed record AudioDiscovery(
    IReadOnlyList<AudioEndpointOption> Outputs,
    IReadOnlyList<AudioEndpointOption> Inputs,
    string? EndpointId,
    IReadOnlyList<CoreAudio.AudioDeviceFormat>? Formats,
    IReadOnlyList<Guid>? SpatialFormats)
{
    /// <summary>Nothing observed, which is what a machine without Core Audio offers.</summary>
    internal static AudioDiscovery Empty { get; } = new([], [], null, null, null);
}
