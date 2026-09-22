using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>The Global profile and every per-game profile.</summary>
/// <remarks>
///     One store for every setting that can differ per game. A game profile holds only what the user
///     changed for that game; everything else is read from <see cref="Global" /> when it is resolved and
///     is never copied in. The rules are in <c>docs\profiles.md</c>.
/// </remarks>
public sealed class ProfileConfig
{
    /// <summary>Values in force when no enabled game profile sets them.</summary>
    public ProfileValues Global { get; set; } = new();

    /// <summary>Per-game profiles, matched against the running application.</summary>
    public List<GameProfile> Games { get; set; } = [];
}

/// <summary>One per-game profile.</summary>
public sealed class GameProfile
{
    /// <summary>Longest accepted <see cref="Name" />.</summary>
    public const int MaxNameLength = 80;

    /// <summary>
    ///     Canonical WSGM application identity, or <c>profile:&lt;guid&gt;</c> for a named profile that
    ///     activates only through <see cref="ProcessNames" />.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-visible name; an empty name displays <see cref="Id" />.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Exact executable names that activate this profile, compared without case.</summary>
    public List<string> ProcessNames { get; set; } = [];

    /// <summary>
    ///     Whether this profile's values apply. This is Steam's "Use per-game profile" switch and the
    ///     overlay's Per-application scope. Off keeps the values, so switching back on restores them.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The values this game overrides. Everything left unset comes from Global.</summary>
    public ProfileValues Values { get; set; } = new();
}

/// <summary>One profile layer. Every member is optional: null means this layer does not set it.</summary>
public sealed class ProfileValues
{
    /// <summary>RTSS frame limit; zero disables limiting.</summary>
    public int? FrameLimit { get; set; }

    /// <summary>Performance-overlay level.</summary>
    public int? OverlayLevel { get; set; }

    /// <summary>Whether manual power uses the coordinated unified target rather than PL1/PL2.</summary>
    public bool? TdpUnified { get; set; }

    /// <summary>Unified power target in watts.</summary>
    public int? UnifiedWatts { get; set; }

    /// <summary>Sustained power limit in watts.</summary>
    public int? SustainedWatts { get; set; }

    /// <summary>Boost power limit in watts.</summary>
    public int? BoostWatts { get; set; }

    /// <summary>Variable refresh.</summary>
    public bool? VariableRefreshRate { get; set; }

    /// <summary>Device power preset on AC.</summary>
    public DevicePowerPresetReference? AcPowerPreset { get; set; }

    /// <summary>Device power preset on battery.</summary>
    public DevicePowerPresetReference? BatteryPowerPreset { get; set; }

    /// <summary>Authored fan-curve profile.</summary>
    public string? FanCurveProfileId { get; set; }

    /// <summary>Managed-controller target.</summary>
    public ManagedControllerTarget? ControllerTarget { get; set; }

    /// <summary>Device capability values, one per device, capability and instance.</summary>
    public List<ProfileDeviceValue> Device { get; set; } = [];
}

/// <summary>One stored device capability value.</summary>
public sealed class ProfileDeviceValue
{
    /// <summary>Stable local device identity.</summary>
    public string DeviceIdentityKey { get; set; } = string.Empty;

    /// <summary>Semantic capability identifier.</summary>
    public string CapabilityId { get; set; } = string.Empty;

    /// <summary>Descriptor instance, or null for a single-instance capability.</summary>
    public string? InstanceId { get; set; }

    /// <summary>The value.</summary>
    public CapabilityValue? Value { get; set; }
}
