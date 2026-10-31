using System;
using System.Collections.Generic;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>Core Audio operations used by the profile service.</summary>
/// <remarks>
///     Methods return HRESULTs: negative values indicate failure and make out values unusable unless documented
///     otherwise.
/// </remarks>
internal interface IAudioProfileOperations
{
    /// <summary>Lists active endpoints in one direction.</summary>
    /// <param name="direction">Playback or recording flow.</param>
    /// <param name="endpoints">Detached endpoint snapshot on success.</param>
    /// <returns>The native HRESULT.</returns>
    int ListEndpoints(CoreAudio.AudioDirection direction, out IReadOnlyList<CoreAudio.AudioEndpoint> endpoints);

    /// <summary>Sets the named endpoint as default for all audio roles.</summary>
    /// <param name="endpointId">Active endpoint identifier; a partial role change is possible on failure.</param>
    /// <returns>The native HRESULT.</returns>
    int SetDefaultEndpoint(string endpointId);

    /// <summary>Reads the console-role default endpoint’s volume and mute state.</summary>
    /// <param name="direction">Playback or recording flow.</param>
    /// <param name="volume">Rounded volume percentage on success.</param>
    /// <param name="muted">One when muted; zero otherwise, on success.</param>
    /// <returns>The native HRESULT.</returns>
    int GetVolume(CoreAudio.AudioDirection direction, out int volume, out int muted);

    /// <summary>Sets the console-role default endpoint’s volume.</summary>
    /// <param name="direction">Playback or recording flow.</param>
    /// <param name="volume">Requested percentage, clamped to 0–100.</param>
    /// <param name="muted">Mute state after the write, valid only on success. A positive volume also unmutes the endpoint.</param>
    /// <returns>The native HRESULT.</returns>
    int SetVolume(CoreAudio.AudioDirection direction, int volume, out int muted);

    /// <summary>Sets mute on the console-role default playback endpoint.</summary>
    /// <param name="muted">Requested mute state.</param>
    /// <returns>The native HRESULT.</returns>
    int SetMuted(bool muted);

    /// <summary>Reads an endpoint’s shared-mode device format.</summary>
    /// <param name="endpointId">Endpoint identifier.</param>
    /// <param name="format">Observed format on success.</param>
    /// <returns>The native HRESULT.</returns>
    int GetDeviceFormat(string endpointId, out CoreAudio.AudioDeviceFormat format);

    /// <summary>Probes the supported candidate formats for an endpoint.</summary>
    /// <param name="endpointId">Endpoint identifier.</param>
    /// <param name="formats">Supported candidates on success; not an exhaustive hardware format list.</param>
    /// <returns>The native HRESULT.</returns>
    int ListSupportedDeviceFormats(string endpointId, out IReadOnlyList<CoreAudio.AudioDeviceFormat> formats);

    /// <summary>Requests a device format change.</summary>
    /// <param name="endpointId">Endpoint identifier.</param>
    /// <param name="format">Requested format; callers must check support.</param>
    /// <returns>The native HRESULT.</returns>
    int SetDeviceFormat(string endpointId, CoreAudio.AudioDeviceFormat format);

    /// <summary>Reads supported spatial formats and the current selection.</summary>
    /// <param name="endpointId">Playback endpoint identifier.</param>
    /// <param name="state">Observed spatial state on success.</param>
    /// <returns>The native HRESULT.</returns>
    int GetSpatialAudio(string endpointId, out CoreAudio.SpatialAudioState state);

    /// <summary>Requests a spatial format change.</summary>
    /// <param name="endpointId">Playback endpoint identifier.</param>
    /// <param name="format">Requested spatial format or Off sentinel.</param>
    /// <param name="status">Windows operation status, valid only for a nonnegative HRESULT; inspect it separately.</param>
    /// <returns>The native HRESULT.</returns>
    int SetSpatialAudio(string endpointId, Guid format, out CoreAudio.SpatialAudioSetStatus status);

    /// <summary>Registers endpoint-change notifications.</summary>
    /// <param name="onChanged">Short nonblocking callback; may run on a native notification thread.</param>
    /// <param name="watch">Caller-owned registration on success; dispose to unregister.</param>
    /// <returns>The native HRESULT.</returns>
    int WatchEndpoints(Action onChanged, out IDisposable? watch);
}
