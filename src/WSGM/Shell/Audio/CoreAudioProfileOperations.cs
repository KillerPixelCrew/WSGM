using System;
using System.Collections.Generic;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>The production <see cref="IAudioProfileOperations" /> over Windows Core Audio.</summary>
internal sealed class CoreAudioProfileOperations : IAudioProfileOperations
{
    /// <inheritdoc />
    public int ListEndpoints(CoreAudio.AudioDirection direction,
        out IReadOnlyList<CoreAudio.AudioEndpoint> endpoints)
    {
        return CoreAudio.ListEndpoints(direction, out endpoints);
    }

    /// <inheritdoc />
    public int SetDefaultEndpoint(string endpointId)
    {
        return CoreAudio.SetDefaultEndpoint(endpointId);
    }

    /// <inheritdoc />
    public int GetVolume(CoreAudio.AudioDirection direction, out int volume, out int muted)
    {
        return CoreAudio.GetVolume(direction, out volume, out muted);
    }

    /// <inheritdoc />
    public int SetVolume(CoreAudio.AudioDirection direction, int volume, out int muted)
    {
        return CoreAudio.SetVolume(direction, volume, out muted);
    }

    /// <inheritdoc />
    public int SetMuted(bool muted)
    {
        return CoreAudio.SetMuted(muted);
    }

    /// <inheritdoc />
    public int GetDeviceFormat(string endpointId, out CoreAudio.AudioDeviceFormat format)
    {
        return CoreAudio.GetDeviceFormat(endpointId, out format);
    }

    /// <inheritdoc />
    public int ListSupportedDeviceFormats(string endpointId, out IReadOnlyList<CoreAudio.AudioDeviceFormat> formats)
    {
        return CoreAudio.ListSupportedDeviceFormats(endpointId, out formats);
    }

    /// <inheritdoc />
    public int SetDeviceFormat(string endpointId, CoreAudio.AudioDeviceFormat format)
    {
        return CoreAudio.SetDeviceFormat(endpointId, format);
    }

    /// <inheritdoc />
    public int GetSpatialAudio(string endpointId, out CoreAudio.SpatialAudioState state)
    {
        return CoreAudio.GetSpatialAudio(endpointId, out state);
    }

    /// <inheritdoc />
    public int SetSpatialAudio(string endpointId, Guid format, out CoreAudio.SpatialAudioSetStatus status)
    {
        return CoreAudio.SetSpatialAudio(endpointId, format, out status);
    }

    /// <inheritdoc />
    public int WatchEndpoints(Action onChanged, out IDisposable? watch)
    {
        return CoreAudio.StartEndpointWatch(_ => onChanged(), out watch);
    }
}
