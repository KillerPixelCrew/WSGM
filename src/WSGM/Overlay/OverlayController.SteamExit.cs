using System;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    /// <summary>How long Steam stays closed before an automatic relaunch.</summary>
    private static readonly TimeSpan SteamRelaunchDelay = TimeSpan.FromSeconds(10);

    private void OnSteamExited()
    {
        switch (DecideSteamExitReaction())
        {
            case SteamExitReaction.ShowOverlay:
                ShowOverlay();
                return;
            case SteamExitReaction.RelaunchBigPicture:
            case SteamExitReaction.RelaunchDesktop:
                Log.Info($"Steam exited — auto-relaunching in {SteamRelaunchDelay.TotalSeconds:0} s.");
                _pendingSteamRelaunch?.Dispose();
                _pendingSteamRelaunch = RunOnUiThreadAfter(SteamRelaunchDelay, RelaunchSteamAfterExit);
                return;
            case SteamExitReaction.Ignore:
            default:
                Log.Info("Steam exited — leaving it closed.");
                return;
        }
    }

    /// <summary>
    ///     Re-decides at fire time: a config reload replaces <c>_config</c> wholesale, the
    ///     session may have changed mode, and the user may have closed Steam while the delay ran.
    /// </summary>
    private void RelaunchSteamAfterExit()
    {
        _pendingSteamRelaunch = null;
        switch (DecideSteamExitReaction())
        {
            case SteamExitReaction.RelaunchBigPicture:
                _modes.StartOrFocusSteam();
                return;
            case SteamExitReaction.RelaunchDesktop:
                _modes.EnsureSteamDesktop();
                return;
            case SteamExitReaction.Ignore:
            case SteamExitReaction.ShowOverlay:
            default:
                Log.Info("Auto-relaunch skipped: the session no longer wants Steam started.");
                return;
        }
    }

    /// <summary>
    ///     Reads the live session state the policy needs. Explorer's presence is the same
    ///     signal the overlay's own mode button uses to tell desktop from game mode.
    /// </summary>
    private SteamExitReaction DecideSteamExitReaction()
    {
        return SteamExitPolicy.Decide(
            !ExplorerControl.IsDesktopShellRunning(),
            _config.SteamAutoRelaunch,
            _monitor?.Paused == true,
            _modes.SteamClosedByUser);
    }
}
