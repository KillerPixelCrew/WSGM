using System;
using WSGM.Core;
using WSGM.Input;
using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The global ways to summon the sheet: the hotkey, the controller chord and the edge swipes, with the
///     controller polling the chord shares with the sheet's navigation.
/// </summary>
/// <remarks>
///     Only a surface with the composition's message window owns the triggers; without one, such as the
///     Settings preview, the pad still drives navigation while the sheet is open. Construct, configure and
///     dispose on the UI thread.
/// </remarks>
internal sealed class OverlayActivation : IDisposable
{
    private readonly GamepadChordWatcher? _chordWatcher;
    private readonly bool _enabled;
    private readonly HotkeyService? _hotkey;
    private TouchSwipeMonitor? _touchSwipes;

    /// <summary>Creates the triggers for a surface that owns them and starts polling for an active chord.</summary>
    /// <param name="config">The initial configuration.</param>
    /// <param name="activationWindow">The composition's message window, or null for a surface without triggers.</param>
    internal OverlayActivation(AppConfig config, MessageWindow? activationWindow)
    {
        _enabled = activationWindow is not null;
        if (activationWindow is not null)
        {
            _hotkey = new HotkeyService(activationWindow);
            _hotkey.Pressed += OnTriggered;
            _hotkey.Apply(config.Hotkey);
            _chordWatcher = new GamepadChordWatcher(Gamepad, config.GamepadChord);
            _chordWatcher.Triggered += OnTriggered;
            if (ChordActive(config))
            {
                Gamepad.Start();
            }
        }

        ApplyGestures(config.Gestures);
    }

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

    /// <summary>The controller polling the chord and the sheet's navigation read.</summary>
    internal GamepadService Gamepad { get; } = new();

    /// <summary>Raised when the hotkey, the chord or the top edge asks for the sheet.</summary>
    internal event Action? QuickAccessRequested;

    /// <summary>Raised when the bottom edge asks for the sheet on its Open apps strip.</summary>
    internal event Action? OpenAppsRequested;

    /// <summary>Releases the hotkey, the chord, the controller polling and the edge recognizer, in that order.</summary>
    public void Dispose()
    {
        if (_hotkey is not null)
        {
            _hotkey.Pressed -= OnTriggered;
            _hotkey.Dispose();
        }

        if (_chordWatcher is not null)
        {
            _chordWatcher.Triggered -= OnTriggered;
            _chordWatcher.Dispose();
        }

        // Before the service it subscribes to, so the unsubscribe lands on a live object.
        Gamepad.Dispose();
        if (_touchSwipes is null)
        {
            return;
        }

        _touchSwipes.Triggered -= OnSwipeTriggered;
        _touchSwipes.Dispose();
        _touchSwipes = null;
    }

    /// <summary>Whether the controller chord opens the sheet, which keeps the gamepad polled while it is closed.</summary>
    /// <param name="config">The configuration to read.</param>
    /// <returns>True when this surface owns the chord and the chord is enabled with buttons.</returns>
    internal bool ChordActive(AppConfig config)
    {
        return _enabled && config.GamepadChord.Enabled && config.GamepadChord.Buttons != 0;
    }

    /// <summary>Applies a reloaded configuration to the hotkey, the chord, the controller polling and the edges.</summary>
    /// <param name="config">The freshly loaded configuration.</param>
    /// <param name="sheetOpen">Whether the sheet is open, which keeps the pad polled and the edges disarmed.</param>
    internal void ApplyConfig(AppConfig config, bool sheetOpen)
    {
        _hotkey?.Apply(config.Hotkey);
        _chordWatcher?.ApplyConfig(config.GamepadChord);
        switch (ChordActive(config))
        {
            case true when !Gamepad.IsRunning:
                Gamepad.Start();
                break;
            case false when !sheetOpen && Gamepad.IsRunning:
                Gamepad.Stop();
                break;
        }

        ApplyGestures(config.Gestures, sheetOpen);
    }

    /// <summary>Stops reading the edges while the sheet is open.</summary>
    internal void Disarm()
    {
        _touchSwipes?.Disarm();
    }

    /// <summary>Reads the edges again once the sheet has closed.</summary>
    internal void Arm()
    {
        _touchSwipes?.Arm();
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
    internal static SwipeAction DecideSwipe(ScreenEdge edge, bool explorerRunning)
    {
        return edge switch
        {
            ScreenEdge.Left => SwipeAction.SteamMenu,
            ScreenEdge.Right => SwipeAction.SteamQuickAccess,
            ScreenEdge.Top => SwipeAction.QuickAccess,
            _ => explorerRunning ? SwipeAction.None : SwipeAction.QuickAccessApps
        };
    }

    /// <summary>Applies changed gesture settings without replacing the monitor.</summary>
    /// <param name="gestures">The new edge-swipe configuration.</param>
    /// <param name="sheetOpen">Whether the sheet is open.</param>
    private void ApplyGestures(GestureConfig gestures, bool sheetOpen = false)
    {
        if (!_enabled)
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
        if (sheetOpen)
        {
            _touchSwipes.Disarm();
        }
        else
        {
            _touchSwipes.Arm();
        }
    }

    private void OnTriggered()
    {
        QuickAccessRequested?.Invoke();
    }

    private void OnSwipeTriggered(ScreenEdge edge)
    {
        switch (DecideSwipe(edge, ExplorerControl.IsDesktopShellRunning()))
        {
            case SwipeAction.QuickAccessApps:
                OpenAppsRequested?.Invoke();
                break;
            case SwipeAction.QuickAccess:
                QuickAccessRequested?.Invoke();
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
}
