using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>Persisted settings for the optional production device platform.</summary>
public sealed class DeviceIntegrationConfig
{
    /// <summary>The current device preference schema; retired package preferences are not migrated.</summary>
    public const int CurrentPreferencesSchemaVersion = 1;

    /// <summary>Master ownership switch. Older configurations default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Remembered child preference for physical-controller management.</summary>
    /// <remarks>
    ///     Turning the master switch off makes this ineffective but does not erase it. A later master
    ///     re-enable restores the user's previous choice after all safety gates are checked again.
    /// </remarks>
    public bool ControllerManagementEnabled { get; set; }

    /// <summary>Whether AutoTDP controls the primary power limit from frame delivery.</summary>
    /// <remarks>
    ///     Requires Device Integration, because the limit it moves is a plugin capability. Off leaves
    ///     the power limit entirely to manual control and profiles.
    /// </remarks>
    public bool AutoTdpEnabled { get; set; }

    /// <summary>Whether edits to Steam's guide button chord layout are kept for a Steam Deck target.</summary>
    /// <remarks>
    ///     Steam reloads its own chord template after every autosave for that controller type and
    ///     discards the edit; see <see cref="SteamGuideChordMirror" /> for the workaround this
    ///     switches on. Off restores Valve's template.
    /// </remarks>
    public bool KeepGuideChordEdits { get; set; } = true;

    /// <summary>How the active handheld glyph profile is selected.</summary>
    public DeviceGlyphSelection GlyphSelection { get; set; } = DeviceGlyphSelection.Automatic;

    /// <summary>Manual reviewed glyph profile when <see cref="GlyphSelection" /> is manual.</summary>
    public string? ManualGlyphProfileId { get; set; }

    /// <summary>Allowlisted assignments for logical OEM controls.</summary>
    public List<DeviceOemAssignment> OemAssignments { get; set; } = [];

    /// <summary>Schema last applied to device preferences; absent legacy values begin at zero.</summary>
    public int PreferencesSchemaVersion { get; set; }

    /// <summary>Authored fan and lighting profiles keyed by exact device definition and family.</summary>
    public List<DeviceProfileScope> DeviceProfiles { get; set; } = [];
}

/// <summary>The authored profiles of one exact built-in handheld definition.</summary>
public sealed class DeviceProfileScope
{
    /// <summary>Exact model definition the profiles were authored for.</summary>
    public string DeviceDefinitionId { get; set; } = string.Empty;

    /// <summary>Plain built-in family identity.</summary>
    public string FamilyId { get; set; } = string.Empty;

    /// <summary>Named fan curves and lighting profiles; selection belongs to the global/per-game profile.</summary>
    public List<DeviceAuthoredProfile> Profiles { get; set; } = [];
}

/// <summary>One named profile the user authored for a device capability.</summary>
/// <remarks>
///     A profile is not a setting. A setting is one value WSGM keeps and hands the plugin; a profile is
///     a named shape the user builds and then applies, globally or per application, from the overlay.
///     That is why curves are refused as settings and live here instead — one home each.
/// </remarks>
public sealed class DeviceAuthoredProfile
{
    /// <summary>Stable identifier the overlay selects by.</summary>
    /// <remarks>
    ///     Separate from <see cref="Name" /> so renaming a profile does not detach every application
    ///     override that pointed at it.
    /// </remarks>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>What the user called it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The capability this profile authors, for example a fan curve or a lighting colour.</summary>
    public string CapabilityId { get; set; } = string.Empty;

    /// <summary>Curve points, ascending by input. Empty for a profile that is not a curve.</summary>
    public List<AuthoredCurvePoint> Curve { get; set; } = [];

    /// <summary>Packed colour for a lighting profile, or null when this is not one.</summary>
    public int? Color { get; set; }
}

/// <summary>One authored curve point.</summary>
/// <remarks>
///     A mutable configuration class rather than the SDK's <c>CurvePoint</c> struct, matching every
///     other stored shape in this file: configuration is deserialized, normalized in place, and
///     re-serialized, while the SDK value is an immutable runtime contract.
/// </remarks>
public sealed class AuthoredCurvePoint
{
    /// <summary>The input, for a fan curve a temperature.</summary>
    public int Input { get; set; }

    /// <summary>The output, for a fan curve a duty percentage.</summary>
    public int Output { get; set; }
}

/// <summary>Controller identity exposed to applications while management is active.</summary>
public enum ManagedControllerTarget
{
    /// <summary>Rich Steam Deck-compatible composite target.</summary>
    SteamDeckComposite,

    /// <summary>Widely compatible Xbox 360 target.</summary>
    Xbox360,

    /// <summary>DualShock 4 target with native motion where supported.</summary>
    DualShock4
}

/// <summary>How WSGM chooses a device glyph profile.</summary>
public enum DeviceGlyphSelection
{
    /// <summary>Use only the exact verified profile advertised by the device definition.</summary>
    Automatic,

    /// <summary>Leave Steam's own glyphs untouched.</summary>
    NativeSteam,

    /// <summary>Use an explicitly selected reviewed catalog profile.</summary>
    ManualReviewedProfile
}

/// <summary>Closed WSGM-owned actions that an OEM control may invoke.</summary>
/// <remarks>
///     There is deliberately no executable, script, shell-command, text-macro, or arbitrary-key action.
///     Keeping the vocabulary here, rather than in the plugin SDK, prevents hardware packages from
///     defining WSGM application policy or turning OEM assignment into a general remapper.
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

    /// <summary>
    ///     Open the quick access sheet on its Open apps strip, or close it when it is up —
    ///     what the taskbar button did before the strip moved into the sheet.
    /// </summary>
    ToggleWsgmTaskbar,

    /// <summary>Switch between Desktop and Game Mode.</summary>
    ToggleDesktopGameMode,

    /// <summary>Show or hide the on-screen keyboard.</summary>
    ToggleOnScreenKeyboard,

    /// <summary>Assign the next device power preset for the current power source.</summary>
    CyclePerformanceProfile,

    /// <summary>Move to the next performance-overlay level.</summary>
    CyclePerformanceOverlayLevel,

    /// <summary>Forward as the current target's first rear control. Rear placement only.</summary>
    VirtualTargetRearButton1,

    /// <summary>Forward as the current target's second rear control. Rear placement only.</summary>
    VirtualTargetRearButton2,

    /// <summary>Invoke Steam's native Home/Overlay button for the active Steam window.</summary>
    ToggleSteamOverlay,

    /// <summary>Hold the right mouse button until the physical OEM gesture is released.</summary>
    MouseSecondaryButton
}

/// <summary>One allowlisted OEM-control assignment.</summary>
public sealed class DeviceOemAssignment
{
    /// <summary>Stable logical control identifier from the plugin descriptor.</summary>
    public string ControlId { get; set; } = string.Empty;

    /// <summary>Closed WSGM-owned action.</summary>
    public OemAction Action { get; set; } = OemAction.Disabled;
}
