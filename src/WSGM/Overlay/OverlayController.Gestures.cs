using WSGM.Core;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    /// <summary>What an edge swipe opens (routing result).</summary>
    public enum SwipeAction
    {
        /// <summary>The swipe is ignored.</summary>
        None,

        /// <summary>The quick access sheet opens.</summary>
        QuickAccess,

        /// <summary>The quick access sheet opens with focus on its Open apps strip.</summary>
        QuickAccessApps,

        /// <summary>Steam Big Picture's left-side Steam menu opens.</summary>
        SteamMenu,

        /// <summary>Steam Big Picture's right-side Quick Access Menu opens.</summary>
        SteamQuickAccess
    }

    /// <summary>Applies changed gesture settings without replacing the monitor.</summary>
    /// <param name="gestures">The new edge-swipe configuration.</param>
    private void ApplyGestures(GestureConfig gestures)
    {
        if (!_activationEnabled)
        {
            return;
        }

        // Keep one recognizer owner across configuration changes.
        if (_touchSwipes is null)
        {
            _touchSwipes = new TouchSwipeMonitor();
            _touchSwipes.Triggered += OnSwipeTriggered;
        }

        _touchSwipes.Configure(gestures);

        // The open sheet disarms the edges: docked on the top edge, a re-arm would
        // read touches inside its header as top-edge swipes.
        if (_overlay is not null)
        {
            HideTouchEdges();
        }
        else
        {
            ShowTouchEdges();
        }
    }

    private void OnSwipeTriggered(ScreenEdge edge)
    {
        switch (DecideSwipe(edge, ExplorerControl.IsDesktopShellRunning()))
        {
            case SwipeAction.QuickAccessApps:
                ShowOverlayOnOpenApps();
                break;
            case SwipeAction.QuickAccess:
                ShowOverlay();
                break;
            case SwipeAction.SteamMenu:
                Steam.TrySendBigPictureShortcut(BigPictureShortcut.SteamMenu);
                break;
            case SwipeAction.SteamQuickAccess:
                Steam.TrySendBigPictureShortcut(BigPictureShortcut.QuickAccess);
                break;
            case SwipeAction.None:
            default:
                Log.Info("Bottom swipe ignored in desktop mode (explorer's taskbar owns the edge).");
                break;
        }
    }

    /// <summary>
    ///     The pure edge-routing decision — the SteamOS map: left/right open
    ///     Steam's own menus, top opens WSGM's sheet, and bottom opens the sheet on its
    ///     Open apps strip in game mode but is IGNORED in desktop mode — explorer's
    ///     real taskbar owns that edge there, and falling back to the panel read as a
    ///     regression (device-reported).
    /// </summary>
    /// <param name="edge">The swiped screen edge.</param>
    /// <param name="explorerRunning">Whether the session currently has a desktop.</param>
    /// <returns>What the swipe opens, if anything.</returns>
    public static SwipeAction DecideSwipe(ScreenEdge edge, bool explorerRunning)
    {
        return edge switch
        {
            ScreenEdge.Left => SwipeAction.SteamMenu,
            ScreenEdge.Right => SwipeAction.SteamQuickAccess,
            ScreenEdge.Top => SwipeAction.QuickAccess,
            _ => explorerRunning ? SwipeAction.None : SwipeAction.QuickAccessApps
        };
    }

    private void HideTouchEdges()
    {
        _touchSwipes?.Disarm();
    }

    private void ShowTouchEdges()
    {
        _touchSwipes?.Arm();
    }

    private void DisposeTouchEdges()
    {
        if (_touchSwipes is null)
        {
            return;
        }

        _touchSwipes.Triggered -= OnSwipeTriggered;
        _touchSwipes.Dispose();
        _touchSwipes = null;
    }
}
