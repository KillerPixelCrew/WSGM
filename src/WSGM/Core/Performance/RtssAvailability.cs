namespace WSGM.Core;

/// <summary>Availability of the optional external RTSS integration.</summary>
internal enum RtssAvailability
{
    /// <summary>Discovery has not run.</summary>
    Unknown,

    /// <summary>The registry names no RTSS folder, or the folder has no RTSS executable.</summary>
    NotInstalled,

    /// <summary>The installation lacks the 64-bit profile API or is older than 7.3.</summary>
    Incompatible,

    /// <summary>The installation has no running process.</summary>
    NotRunning,

    /// <summary>Discovery accepted a running process; the profile API is not yet ready.</summary>
    AdapterUnavailable,

    /// <summary>The adapter can access the verified RTSS profile API.</summary>
    Ready,

    /// <summary>Discovery or adapter access failed, or runtime identity was ambiguous.</summary>
    Degraded
}
