using WSGM.Interop;

namespace WSGM.Core;

/// <summary>What the display-off mute service should do with one display-state sample.</summary>
internal enum DisplayMuteAction
{
    /// <summary>The screen went dark — mute if the user has not muted already.</summary>
    Mute,

    /// <summary>The screen is lit again — undo a mute this process applied.</summary>
    Restore,
}

/// <summary>Pure decision logic for <c>Shell\DisplayOffMuteService</c>. Kept separate so
/// the state mapping and the wrap-safe input-tick comparison are unit-testable without a
/// message window or an audio endpoint.</summary>
internal static class DisplayMuteDecider
{
    /// <summary>MONITOR_DISPLAY_STATE: the display is off.</summary>
    internal const int DisplayOff = 0;

    /// <summary>MONITOR_DISPLAY_STATE: the display is on.</summary>
    internal const int DisplayOn = 1;

    /// <summary>MONITOR_DISPLAY_STATE: the display is dimmed (still lit).</summary>
    internal const int DisplayDimmed = 2;

    /// <summary>Maps a MONITOR_DISPLAY_STATE value to the action to take.
    ///
    /// <para>Only the documented "off" value mutes; <b>every other value restores</b>,
    /// including "dimmed" and any value Windows may add later. The asymmetry is
    /// deliberate and is the fail-safe direction: a dimmed screen is still lit in front
    /// of the user, and a state this build does not recognise must never be the reason a
    /// device stays silent.</para></summary>
    /// <param name="state">The reported MONITOR_DISPLAY_STATE.</param>
    /// <returns>The action for that state.</returns>
    internal static DisplayMuteAction ActionFor(int state) =>
        state == DisplayOff ? DisplayMuteAction.Mute : DisplayMuteAction.Restore;

    /// <summary>Whether a notification source may be believed when it says the screen went
    /// dark. Only <see cref="DisplayStateSource.Session"/> describes this session's own
    /// display; the console and legacy settings are registered purely as redundant WAKE
    /// sources, so a stale or cross-session "off" from them must never start a mute. Every
    /// source may report the screen coming back — that direction is the fail-safe one.
    /// </summary>
    /// <param name="source">The setting that delivered the notification.</param>
    /// <returns>True when the source is authoritative for a dark screen.</returns>
    internal static bool MayReportDark(DisplayStateSource source) =>
        source == DisplayStateSource.Session;

    /// <summary>Whether new keyboard/mouse/touch input arrived since the baseline taken
    /// when the screen went dark. GetLastInputInfo reports a 32-bit tick count that wraps
    /// roughly every 49 days, so the comparison is an unchecked signed difference rather
    /// than <c>&gt;</c> on the raw values.</summary>
    /// <param name="baselineTick">The tick count captured at mute time.</param>
    /// <param name="currentTick">The tick count read now.</param>
    /// <returns>True when input happened after the baseline.</returns>
    internal static bool HasInputSince(uint baselineTick, uint currentTick) =>
        unchecked((int)(currentTick - baselineTick)) > 0;
}
