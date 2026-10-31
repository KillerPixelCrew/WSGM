using System;
using WindowsDeviceControl;

namespace WSGM.Shell;

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
