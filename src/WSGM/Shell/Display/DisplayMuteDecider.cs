using System;

namespace WSGM.Shell;

/// <summary>
///     Pure decision logic for <see cref="DisplayOffMuteService" />. Kept separate so
///     the state mapping and the wrap-safe input-tick comparison are unit-testable without a
///     message window or an audio endpoint.
/// </summary>
internal static class DisplayMuteDecider
{
    /// <summary>
    ///     Grace after the last active download before audio is restored while
    ///     the screen remains dark.
    /// </summary>
    internal static readonly TimeSpan DownloadCompletionRestoreDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Reconciles the feature setting, display state, Steam download state,
    ///     and ownership of the current mute. Muting requires every positive condition;
    ///     restoration is immediate when the display is lit or the setting is disabled,
    ///     but an idle transition while the display remains dark receives a grace period
    ///     so adjacent queue items do not flap the endpoint.
    /// </summary>
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

    /// <summary>
    ///     Whether new keyboard/mouse/touch input arrived since the baseline taken
    ///     when the screen went dark. GetLastInputInfo reports a 32-bit tick count that wraps
    ///     roughly every 49 days, so the comparison is an unchecked signed difference rather
    ///     than <c>&gt;</c> on the raw values.
    /// </summary>
    /// <param name="baselineTick">The tick count captured at mute time.</param>
    /// <param name="currentTick">The tick count read now.</param>
    /// <returns>True when input happened after the baseline.</returns>
    internal static bool HasInputSince(uint baselineTick, uint currentTick)
    {
        return unchecked((int)(currentTick - baselineTick)) > 0;
    }
}
