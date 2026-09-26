using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

namespace WSGM.Core;

/// <summary>Shared shape check for plugin-supplied identifiers.</summary>
public static class DeviceIdentifier
{
    /// <summary>
    ///     Whether a plugin-supplied identifier is non-empty, bounded, and uses only ASCII
    ///     letters, digits, '.', '-' and '_'.
    /// </summary>
    /// <param name="value">The identifier to check.</param>
    /// <param name="maximumLength">Longest accepted identifier.</param>
    /// <returns><see langword="true" /> when the identifier is safe to store, log, and use as a key.</returns>
    public static bool IsValid(string value, int maximumLength)
    {
        return PlainText.IsIdentifier(value, maximumLength);
    }
}

/// <summary>Capability ids the authored-profile chain targets.</summary>
public static class DeviceAuthoredProfileCapabilities
{
    /// <summary>The fan-curve capability authored curve profiles apply to.</summary>
    public const string FanCurve = "fan.curve";

    /// <summary>The lighting zone-colour capability authored colour profiles apply to.</summary>
    public const string Lighting = "lighting.zone-color";
}

/// <summary>When WSGM asks the device plugin to stream motion.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MotionStreamMode>))]
public enum MotionStreamMode
{
    /// <summary>Stream whenever a managed target with a motion report is active.</summary>
    Always,

    /// <summary>
    ///     Stream only while a consumer has asked the virtual controller for motion, the way a Steam
    ///     Deck's own controller powers its IMU on request. Only the Steam Deck target carries that
    ///     request; a DualShock 4 target always streams.
    /// </summary>
    OnDemand
}

/// <summary>Persisted settings for the optional production device platform.</summary>
public sealed class DeviceIntegrationConfig
{
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

    /// <summary>When the plugin's gyroscope and accelerometer stream runs.</summary>
    /// <remarks>
    ///     Motion is the highest-rate data WSGM moves, and on the Claw reading it costs a driver host
    ///     and a WSGM thread several percent of a core (docs/perf). Whatever the mode, the stream is
    ///     off while controller management is not active or the managed target carries no motion
    ///     report; the mode decides whether it also waits for a consumer's request. Older
    ///     configurations that named the retired "InGame" mode load as <see cref="MotionStreamMode.OnDemand" />.
    /// </remarks>
    public MotionStreamMode MotionStream { get; set; } = MotionStreamMode.OnDemand;

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

    /// <summary>Stored values for the settings a plugin declares for itself.</summary>
    /// <remarks>
    ///     Keyed by device definition and plugin, so a value authored for one plugin never reaches
    ///     another that happens to reuse the setting identifier. Values are revalidated against the
    ///     current manifest on load, because a plugin update can narrow a range or drop an option.
    /// </remarks>
    public List<PluginSettingsScope> PluginSettings { get; set; } = [];
}

/// <summary>The stored settings of one plugin on one device definition.</summary>
public sealed class PluginSettingsScope
{
    /// <summary>Device definition the values were authored against.</summary>
    public string DeviceDefinitionId { get; set; } = string.Empty;

    /// <summary>Plugin that declared the settings.</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>The values, one per declared setting the user has changed.</summary>
    public List<PluginSettingValue> Values { get; set; } = [];

    /// <summary>
    ///     The manifest the plugin published when it last ran, or null when none has been seen.
    /// </summary>
    /// <remarks>
    ///     Cached because Settings has to draw the page without activating device hardware. The
    ///     declaration is published by plugin code rather than stored in <c>plugin.wsgm.json</c>, so
    ///     there is nothing equivalent to read from the installed package at rest.
    ///     <para>
    ///         It is a cache and never the authority. The shell replaces it whenever a running plugin
    ///         publishes, and stored values are still reconciled against the live declaration when one
    ///         exists — this only decides what can be <em>drawn</em> when no plugin is running, never what
    ///         is legal to send one.
    ///     </para>
    ///     <para>
    ///         Stale by construction: a plugin uninstalled or downgraded between sessions leaves a manifest
    ///         describing settings that no longer exist. That is why it is dropped when it fails its own
    ///         validation on load, and why the page it produces is editable but the values still go through
    ///         reconciliation before they reach a plugin.
    ///     </para>
    /// </remarks>
    public PluginSettingsManifest? Declaration { get; set; }

    /// <summary>Named fan curves and lighting profiles the user authored for this device.</summary>
    /// <remarks>
    ///     Device-keyed and stored beside the plugin's settings because they are authored the same way
    ///     and become meaningless against a different device. Authoring lives in Settings; choosing
    ///     which one is in force is the overlay's job (D22b), so nothing here records a selection.
    /// </remarks>
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
    /// <summary>Longest accepted <see cref="Name" />.</summary>
    public const int MaxNameLength = 48;

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

/// <summary>One stored plugin setting value.</summary>
/// <remarks>
///     Mirrors the value shapes the SDK allows a setting to take. There is no curve field: a curve is
///     authored as a named profile with its own storage, so a curve-shaped setting is refused at
///     declaration rather than given a second home here.
/// </remarks>
public sealed class PluginSettingValue
{
    /// <summary>Which declared setting this is the value of.</summary>
    public string SettingId { get; set; } = string.Empty;

    /// <summary>Value of a boolean setting.</summary>
    public bool? Boolean { get; set; }

    /// <summary>Value of an integer setting.</summary>
    public int? Integer { get; set; }

    /// <summary>Selected option of a choice setting.</summary>
    public string? Choice { get; set; }

    /// <summary>Packed 24-bit RGB of a colour setting.</summary>
    public int? Color { get; set; }

    /// <summary>Value of a text setting.</summary>
    public string? Text { get; set; }
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
    ToggleSteamOverlay
}

/// <summary>One allowlisted OEM-control assignment.</summary>
public sealed class DeviceOemAssignment
{
    /// <summary>Stable logical control identifier from the plugin descriptor.</summary>
    public string ControlId { get; set; } = string.Empty;

    /// <summary>Closed WSGM-owned action.</summary>
    public OemAction Action { get; set; } = OemAction.Disabled;
}
