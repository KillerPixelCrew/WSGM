using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Uses Windows' notification state to keep the volume OSD out of a
/// confirmed exclusive Direct3D game, while still showing it over Steam Big Picture
/// and borderless games that Windows reports as generically busy.</summary>
internal static class VolumeOsdVisibility
{
    /// <summary>Gets whether it is safe to display the non-activating volume indicator.
    /// Returns false for confirmed exclusive fullscreen, a locked/not-present session,
    /// or a failed system query.</summary>
    internal static bool CanShow()
    {
        var result = NativeMethods.SHQueryUserNotificationState(out var state);
        if (AllowsVolumeOsd(result, state))
        {
            return true;
        }

        if (result < 0)
        {
            Log.Warn($"Volume OSD suppressed: notification-state query failed (HRESULT 0x{result:X8}).");
        }
        else
        {
            Log.Info($"Volume OSD suppressed: Windows notification state {state} is exclusive or not present.");
        }
        return false;
    }

    /// <summary>Returns whether a native notification-state result permits the OSD.
    /// QUNS_BUSY is deliberately allowed: Steam Big Picture and borderless-fullscreen
    /// games commonly report it without holding exclusive fullscreen.</summary>
    internal static bool AllowsVolumeOsd(int hresult, int state)
        => hresult >= 0 && state is not NativeMethods.QunsNotPresent
            and not NativeMethods.QunsRunningD3dFullScreen;
}
