using System;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>What the display-off mute service should do after one policy input changes.</summary>
internal enum DisplayMuteAction
{
    /// <summary>The current state already satisfies the policy.</summary>
    NoChange,

    /// <summary>The screen went dark — mute if the user has not muted already.</summary>
    Mute,

    /// <summary>The screen is lit again — undo a mute this process applied.</summary>
    Restore,

    /// <summary>The last download stopped while the screen remains dark — restore
    /// after the completion grace period unless activity resumes.</summary>
    DelayRestore,
}

/// <summary>Pure decision logic for <c>Shell\DisplayOffMuteService</c>. Kept separate so
/// the state mapping and the wrap-safe input-tick comparison are unit-testable without a
/// message window or an audio endpoint.</summary>
internal static class DisplayMuteDecider
{
    /// <summary>Grace after the last active download before audio is restored while
    /// the screen remains dark.</summary>
    internal static readonly TimeSpan DownloadCompletionRestoreDelay = TimeSpan.FromSeconds(10);

    /// <summary>MONITOR_DISPLAY_STATE: the display is off.</summary>
    internal const int DisplayOff = 0;

    /// <summary>MONITOR_DISPLAY_STATE: the display is on.</summary>
    internal const int DisplayOn = 1;

    /// <summary>MONITOR_DISPLAY_STATE: the display is dimmed (still lit).</summary>
    internal const int DisplayDimmed = 2;

    /// <summary>Returns whether a MONITOR_DISPLAY_STATE value is the documented
    /// display-off value.
    ///
    /// <para>Every other value is treated as lit, including "dimmed" and any value
    /// Windows may add later. The asymmetry is deliberate and fail-safe: a dimmed
    /// screen is still in front of the user, and an unknown value must never be the
    /// reason a device stays silent.</para></summary>
    /// <param name="state">The reported MONITOR_DISPLAY_STATE.</param>
    /// <returns>True only for the documented off value.</returns>
    internal static bool IsDisplayOff(int state) => state == DisplayOff;

    /// <summary>Reconciles the feature setting, display state, Steam download state,
    /// and ownership of the current mute. Muting requires every positive condition;
    /// restoration is immediate when the display is lit or the setting is disabled,
    /// but an idle transition while the display remains dark receives a grace period
    /// so adjacent queue items do not flap the endpoint.</summary>
    /// <param name="enabled">Whether the user enabled download-aware muting.</param>
    /// <param name="displayOff">Whether this session's display is known to be dark.</param>
    /// <param name="downloadActive">Whether Steam reports an active download.</param>
    /// <param name="mutedByUs">Whether WSGM owns the current mute.</param>
    /// <returns>The side effect the service should perform next.</returns>
    internal static DisplayMuteAction Reconcile(
        bool enabled,
        bool displayOff,
        bool downloadActive,
        bool mutedByUs)
    {
        if (!mutedByUs)
        {
            return enabled && displayOff && downloadActive
                ? DisplayMuteAction.Mute
                : DisplayMuteAction.NoChange;
        }
        if (!enabled || !displayOff)
        {
            return DisplayMuteAction.Restore;
        }
        return downloadActive
            ? DisplayMuteAction.NoChange
            : DisplayMuteAction.DelayRestore;
    }

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
