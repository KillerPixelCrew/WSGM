using System;
using System.Collections.Generic;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>Observed playback format and spatial capabilities for one live output endpoint.</summary>
/// <param name="EndpointId">Default playback endpoint observed during this read.</param>
/// <param name="SupportedFormats">Finite set of formats successfully probed for this endpoint.</param>
/// <param name="CurrentFormat">Device format observed before the probes.</param>
/// <param name="SupportedSpatialFormats">
///     Spatial formats reported by Windows; support does not guarantee licence
///     availability.
/// </param>
/// <param name="CurrentSpatialFormat">Observed spatial format, including the Off sentinel.</param>
internal sealed record AudioPlaybackCapabilities(
    string EndpointId,
    IReadOnlyList<CoreAudio.AudioDeviceFormat> SupportedFormats,
    CoreAudio.AudioDeviceFormat CurrentFormat,
    IReadOnlyList<Guid> SupportedSpatialFormats,
    Guid CurrentSpatialFormat);
