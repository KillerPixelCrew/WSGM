using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Single policy gate for autonomous CEF work during Steam startup.</summary>
internal static class SteamUiReadiness
{
    /// <summary>Gets whether Steam has progressed beyond process creation to a real
    /// Big Picture window. A cold-start SharedJSContext can accept evaluations before
    /// this point; early mutation was the distinguishing state in a device-observed
    /// startup failure.</summary>
    internal static bool IsReady => CanDriveAutomaticCef(
        Steam.IsRunning, Steam.IsBigPictureVisible);

    /// <summary>Pure form of <see cref="IsReady"/> for regression coverage.</summary>
    internal static bool CanDriveAutomaticCef(bool steamRunning, bool bigPictureVisible)
        => steamRunning && bigPictureVisible;
}
