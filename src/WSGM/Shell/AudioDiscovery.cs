using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Reads Windows' endpoints and the selected or current default playback endpoint's capabilities.</summary>
    /// <param name="endpointId">The selected endpoint, or null to inspect the current default without selecting it.</param>
    /// <returns>The observation. Every call here is a blocking Core Audio read.</returns>
    internal static AudioDiscovery Read(string? endpointId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Empty;
        }

        var outputRead = CoreAudio.ListEndpoints(CoreAudio.AudioDirection.Render, out var playback);
        var outputs = outputRead >= 0 ? Options(playback) : [];
        var inputs = Endpoints(CoreAudio.AudioDirection.Capture);
        endpointId ??= playback.FirstOrDefault(endpoint => endpoint.IsDefault).Id;
        if (endpointId is null)
        {
            return new AudioDiscovery(outputs, inputs, null, null, null);
        }

        var formats = CoreAudio.ListSupportedDeviceFormats(endpointId, out var supported) >= 0
            ? supported
            : null;
        var spatial = CoreAudio.GetSpatialAudio(endpointId, out var state) >= 0
            ? state.SupportedFormats
            : null;
        return new AudioDiscovery(outputs, inputs, endpointId, formats, spatial);
    }

    private static IReadOnlyList<AudioEndpointOption> Endpoints(CoreAudio.AudioDirection direction)
    {
        return CoreAudio.ListEndpoints(direction, out var endpoints) >= 0
            ? Options(endpoints)
            : [];
    }

    private static IReadOnlyList<AudioEndpointOption> Options(IReadOnlyList<CoreAudio.AudioEndpoint> endpoints)
    {
        return
        [
            .. endpoints.Select(static endpoint =>
                new AudioEndpointOption(endpoint.Id, AudioEndpointText.Name(endpoint)))
        ];
    }
}

/// <summary>Display names for the spatial-audio formats Windows reports.</summary>
internal static class SpatialAudioNames
{
    /// <summary>Names one spatial-audio format, falling back to its identifier when it is not a known one.</summary>
    /// <param name="format">The spatial-audio format identifier.</param>
    /// <returns>The name shown to the user.</returns>
    internal static string For(Guid format)
    {
        return format == CoreAudio.SpatialAudioFormats.Off ? "Off"
            : format == CoreAudio.SpatialAudioFormats.WindowsSonic ? "Windows Sonic"
            : format == CoreAudio.SpatialAudioFormats.DolbyAtmosForHeadphones ? "Dolby Atmos for Headphones"
            : format == CoreAudio.SpatialAudioFormats.DolbyAtmosForSpeakers ? "Dolby Atmos for Speakers"
            : format == CoreAudio.SpatialAudioFormats.DolbyAtmosForHomeTheater ? "Dolby Atmos for Home Theater"
            : format == CoreAudio.SpatialAudioFormats.DtsHeadphoneX ? "DTS Headphone:X"
            : format == CoreAudio.SpatialAudioFormats.DtsXUltra ? "DTS:X Ultra"
            : format.ToString();
    }
}

/// <summary>One named saved-endpoint choice.</summary>
/// <param name="Id">Windows endpoint identifier retained in the audio preference.</param>
/// <param name="Name">Display label; identity comparisons must use Id.</param>
public sealed record AudioEndpointOption(string Id, string Name)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}

/// <summary>One named spatial-audio-format choice.</summary>
/// <param name="Format">Spatial format identifier, including the Off sentinel.</param>
/// <param name="Name">Display label for the format.</param>
public sealed record SpatialAudioOption(Guid Format, string Name)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}
