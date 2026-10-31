using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Uses Windows' notification state to keep WSGM's small non-activating popups, such as the Notes
///     countdown, out of a confirmed exclusive Direct3D game, while still showing them over Steam Big
///     Picture and borderless games that Windows reports as generically busy.
/// </summary>
internal static class OverlayPopupVisibility
{
    /// <summary>
    ///     Gets whether it is safe to display a non-activating popup.
    ///     Returns false for confirmed exclusive fullscreen, a locked/not-present session,
    ///     or a failed system query.
    /// </summary>
    /// <returns>Whether the current successful notification-state query permits a popup; query failures suppress it.</returns>
    internal static bool CanShow()
    {
        var result = NativeMethods.SHQueryUserNotificationState(out var state);
        if (AllowsPopup(result, state))
        {
            return true;
        }

        if (result < 0)
        {
            Log.Warn($"Overlay popup suppressed: notification-state query failed (HRESULT 0x{result:X8}).");
        }
        else
        {
            Log.Info($"Overlay popup suppressed: Windows notification state {state} is exclusive or not present.");
        }

        return false;
    }

    /// <summary>
    ///     Returns whether a native notification-state result permits a popup.
    ///     QUNS_BUSY is deliberately allowed: Steam Big Picture and borderless-fullscreen
    ///     games commonly report it without holding exclusive fullscreen.
    /// </summary>
    /// <param name="hresult">SHQueryUserNotificationState result; any negative HRESULT suppresses the popup.</param>
    /// <param name="state">Raw notification state, meaningful only on success.</param>
    /// <returns>
    ///     False for a failed query, not-present session or exclusive Direct3D fullscreen; true for other states,
    ///     including busy and unknown values.
    /// </returns>
    internal static bool AllowsPopup(int hresult, int state)
    {
        return hresult >= 0 && state is not NativeMethods.QunsNotPresent
            and not NativeMethods.QunsRunningD3dFullScreen;
    }
}
