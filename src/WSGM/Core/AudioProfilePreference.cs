using System;

namespace WSGM.Core;

/// <summary>Optional audio changes for one display-switch direction.</summary>
public sealed class AudioProfilePreference
{
    /// <summary>Default playback endpoint, or null to leave it unchanged.</summary>
    public AudioEndpointPreference? Output { get; set; }

    /// <summary>Default recording endpoint, or null to leave it unchanged.</summary>
    public AudioEndpointPreference? Input { get; set; }

    /// <summary>Default playback volume, or null to leave it unchanged.</summary>
    public int? VolumePercent { get; set; }

    /// <summary>Default playback mute state, or null to leave it unchanged.</summary>
    public bool? Muted { get; set; }

    /// <summary>Default playback format, or null to leave it unchanged.</summary>
    public AudioFormatPreference? PlaybackFormat { get; set; }

    /// <summary>Default playback spatial format, or null to leave it unchanged.</summary>
    public Guid? SpatialFormat { get; set; }
}
