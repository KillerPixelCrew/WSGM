using System.Collections.Generic;

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
