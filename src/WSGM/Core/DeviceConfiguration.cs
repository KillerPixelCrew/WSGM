using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>Compile-time release gates for optional device-platform features.</summary>
public static class DeviceFeatureAvailability
{
    /// <summary>Whether the reviewed controller backend passed every mandatory release gate.</summary>
    public const bool ControllerManagement = false;

    /// <summary>User-safe reason controller management is excluded from this release.</summary>
    public const string ControllerManagementDetail =
        "Controller management is unavailable: the pinned HIDMaestro release "
        + "does not pass the four-rear-control/stick-touch profile gate, and exact signed "
        + "driver reproduction is not established.";
}

/// <summary>Persisted settings for the optional production device platform.</summary>
public sealed class DeviceIntegrationConfig
{
    /// <summary>Master ownership switch. Older configurations default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Remembered child preference for physical-controller management.</summary>
    /// <remarks>
    /// Turning the master switch off makes this ineffective but does not erase it. A later master
    /// re-enable restores the user's previous choice after all safety gates are checked again.
    /// </remarks>
    public bool ControllerManagementEnabled { get; set; }

    /// <summary>Global managed-controller target.</summary>
    public ManagedControllerTarget ControllerTarget { get; set; } =
        ManagedControllerTarget.SteamDeckComposite;

    /// <summary>How the active handheld glyph profile is selected.</summary>
    public DeviceGlyphSelection GlyphSelection { get; set; } = DeviceGlyphSelection.Automatic;

    /// <summary>Manual reviewed glyph profile when <see cref="GlyphSelection"/> is manual.</summary>
    public string? ManualGlyphProfileId { get; set; }

    /// <summary>Sanitized diagnostics detail retained and displayed by default.</summary>
    public DeviceDiagnosticLevel DiagnosticLevel { get; set; } = DeviceDiagnosticLevel.Standard;

    /// <summary>Desired semantic profiles keyed by stable local device identity.</summary>
    public List<DeviceDesiredProfile> Profiles { get; set; } = [];
}

/// <summary>Controller identity exposed to applications while management is active.</summary>
public enum ManagedControllerTarget
{
    /// <summary>Rich Steam Deck-compatible composite target.</summary>
    SteamDeckComposite,

    /// <summary>Widely compatible Xbox 360 target.</summary>
    Xbox360,

    /// <summary>DualShock 4 target with native motion where supported.</summary>
    DualShock4,
}

/// <summary>How WSGM chooses a device glyph profile.</summary>
public enum DeviceGlyphSelection
{
    /// <summary>Use only the exact verified profile advertised by the device definition.</summary>
    Automatic,

    /// <summary>Leave Steam's own glyphs untouched.</summary>
    NativeSteam,

    /// <summary>Use an explicitly selected reviewed catalog profile.</summary>
    ManualReviewedProfile,
}

/// <summary>Sanitized production diagnostic verbosity.</summary>
public enum DeviceDiagnosticLevel
{
    /// <summary>Identity and current health only.</summary>
    Minimal,

    /// <summary>Normal state transitions, commands, and recovery outcomes.</summary>
    Standard,

    /// <summary>Bounded transaction metadata without raw unique identifiers or samples.</summary>
    Detailed,
}

/// <summary>All persistent desired state for one local device identity.</summary>
public sealed class DeviceDesiredProfile
{
    /// <summary>Stable local device identity key.</summary>
    public string DeviceIdentityKey { get; set; } = string.Empty;

    /// <summary>Currently selected named hardware profile, when any.</summary>
    public string? SelectedHardwareProfileId { get; set; }

    /// <summary>Desired values by semantic capability and optional instance.</summary>
    public List<DeviceCapabilityPreference> Capabilities { get; set; } = [];

    /// <summary>Allowlisted assignments for logical OEM controls.</summary>
    public List<DeviceOemAssignment> OemAssignments { get; set; } = [];

    /// <summary>Per-application managed target overrides.</summary>
    public List<DeviceApplicationTargetOverride> ControllerTargets { get; set; } = [];
}

/// <summary>Persistent desired-state layers for one semantic capability instance.</summary>
public sealed class DeviceCapabilityPreference
{
    /// <summary>Semantic capability identifier.</summary>
    public string CapabilityId { get; set; } = string.Empty;

    /// <summary>Optional descriptor instance identifier.</summary>
    public string? InstanceId { get; set; }

    /// <summary>Lowest-precedence global default.</summary>
    public CapabilityValue? GlobalDefault { get; set; }

    /// <summary>Desired value while on AC power.</summary>
    public CapabilityValue? AcPolicy { get; set; }

    /// <summary>Desired value while on battery.</summary>
    public CapabilityValue? DcPolicy { get; set; }

    /// <summary>Values supplied by named hardware profiles.</summary>
    public List<DeviceNamedDesiredValue> HardwareProfiles { get; set; } = [];

    /// <summary>Per-application values at the highest persistent precedence.</summary>
    public List<DeviceApplicationDesiredValue> ApplicationOverrides { get; set; } = [];
}

/// <summary>One named-hardware-profile value.</summary>
public sealed class DeviceNamedDesiredValue
{
    /// <summary>Stable profile identifier.</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>Desired semantic value.</summary>
    public CapabilityValue? Value { get; set; }
}

/// <summary>One per-application desired semantic value.</summary>
public sealed class DeviceApplicationDesiredValue
{
    /// <summary>Stable application identity owned by WSGM.</summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>Desired semantic value.</summary>
    public CapabilityValue? Value { get; set; }
}

/// <summary>Closed WSGM-owned actions that an OEM control may invoke.</summary>
/// <remarks>
/// There is deliberately no executable, script, shell-command, text-macro, or arbitrary-key action.
/// Keeping the vocabulary here, rather than in the plugin SDK, prevents hardware packages from
/// defining WSGM application policy or turning OEM assignment into a general remapper.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OemAction>))]
public enum OemAction
{
    /// <summary>Do nothing.</summary>
    Disabled,

    /// <summary>Open or close the WSGM overlay.</summary>
    ToggleWsgmOverlay,

    /// <summary>Open or close Steam's native Quick Access Menu.</summary>
    ToggleSteamQuickAccess,

    /// <summary>Open the overlay directly on the Device page.</summary>
    ShowWsgmDevicePage,

    /// <summary>Open or close the WSGM taskbar.</summary>
    ToggleWsgmTaskbar,

    /// <summary>Switch between Desktop and Game Mode.</summary>
    ToggleDesktopGameMode,

    /// <summary>Show or hide the on-screen keyboard.</summary>
    ToggleOnScreenKeyboard,

    /// <summary>Move to the next performance profile.</summary>
    CyclePerformanceProfile,

    /// <summary>Move to the next performance-overlay level.</summary>
    CyclePerformanceOverlayLevel,

    /// <summary>Forward as the current target's first rear control. Rear placement only.</summary>
    VirtualTargetRearButton1,

    /// <summary>Forward as the current target's second rear control. Rear placement only.</summary>
    VirtualTargetRearButton2,
}

/// <summary>One allowlisted OEM-control assignment.</summary>
public sealed class DeviceOemAssignment
{
    /// <summary>Stable logical control identifier from the plugin descriptor.</summary>
    public string ControlId { get; set; } = string.Empty;

    /// <summary>Closed WSGM-owned action.</summary>
    public OemAction Action { get; set; } = OemAction.Disabled;
}

/// <summary>One per-application controller target override.</summary>
public sealed class DeviceApplicationTargetOverride
{
    /// <summary>Stable application identity owned by WSGM.</summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>Managed target selected for that application.</summary>
    public ManagedControllerTarget Target { get; set; }
}

/// <summary>The desired-state layer that supplied an effective value.</summary>
public enum DeviceDesiredValueSource
{
    /// <summary>No desired value exists.</summary>
    None,

    /// <summary>Global per-device default.</summary>
    GlobalDefault,

    /// <summary>AC/DC policy.</summary>
    PowerPolicy,

    /// <summary>Selected named hardware profile.</summary>
    HardwareProfile,

    /// <summary>Matched application override.</summary>
    ApplicationOverride,

    /// <summary>Volatile session request.</summary>
    TemporaryRequest,
}

/// <summary>Result of resolving the frozen desired-state precedence.</summary>
public sealed record ResolvedDeviceDesiredValue(
    CapabilityValue? Value,
    DeviceDesiredValueSource Source);

/// <summary>Pure desired-state precedence and edit-target policy.</summary>
public static class DeviceDesiredStateResolver
{
    /// <summary>Resolves temporary, application, profile, power, and global layers.</summary>
    /// <param name="preference">Persistent capability layers.</param>
    /// <param name="onAcPower">Current power state.</param>
    /// <param name="hardwareProfileId">Selected named profile.</param>
    /// <param name="applicationId">Matched application identity.</param>
    /// <param name="temporary">Volatile session request.</param>
    /// <returns>The highest-precedence available value and its source.</returns>
    public static ResolvedDeviceDesiredValue Resolve(
        DeviceCapabilityPreference preference,
        bool onAcPower,
        string? hardwareProfileId,
        string? applicationId,
        CapabilityValue? temporary)
    {
        ArgumentNullException.ThrowIfNull(preference);
        if (temporary is not null)
        {
            return new(temporary, DeviceDesiredValueSource.TemporaryRequest);
        }

        CapabilityValue? application = preference.ApplicationOverrides.FirstOrDefault(
            value => string.Equals(value.ApplicationId, applicationId, StringComparison.Ordinal))?.Value;
        if (application is not null)
        {
            return new(application, DeviceDesiredValueSource.ApplicationOverride);
        }

        CapabilityValue? profile = preference.HardwareProfiles.FirstOrDefault(
            value => string.Equals(value.ProfileId, hardwareProfileId, StringComparison.Ordinal))?.Value;
        if (profile is not null)
        {
            return new(profile, DeviceDesiredValueSource.HardwareProfile);
        }

        CapabilityValue? power = onAcPower ? preference.AcPolicy : preference.DcPolicy;
        if (power is not null)
        {
            return new(power, DeviceDesiredValueSource.PowerPolicy);
        }

        return preference.GlobalDefault is null
            ? new(null, DeviceDesiredValueSource.None)
            : new(preference.GlobalDefault, DeviceDesiredValueSource.GlobalDefault);
    }

    /// <summary>Returns the highest active persistent layer an ordinary edit writes.</summary>
    public static DeviceDesiredValueSource PersistentEditTarget(
        DeviceCapabilityPreference preference,
        bool onAcPower,
        string? hardwareProfileId,
        string? applicationId)
    {
        ArgumentNullException.ThrowIfNull(preference);
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            return DeviceDesiredValueSource.ApplicationOverride;
        }

        if (!string.IsNullOrWhiteSpace(hardwareProfileId))
        {
            return DeviceDesiredValueSource.HardwareProfile;
        }

        if ((onAcPower && preference.AcPolicy is not null)
            || (!onAcPower && preference.DcPolicy is not null))
        {
            return DeviceDesiredValueSource.PowerPolicy;
        }

        return DeviceDesiredValueSource.GlobalDefault;
    }
}
