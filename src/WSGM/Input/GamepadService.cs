using System;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Input;

/// <summary>Buttons WSGM can bind. The low 16 bits deliberately match XInput's
/// wButtons so the mapping is a straight cast; the high bits are the extra buttons a
/// virtual Steam Deck controller (Handheld Companion's emulation) reports over HID —
/// back paddles, Steam and Quick Access — which XInput cannot express at all.</summary>
[Flags]
public enum GamepadButtons : uint
{
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,

    // Steam Deck extras (HID only)
    LeftTrigger = 0x0001_0000,
    RightTrigger = 0x0002_0000,
    L4 = 0x0004_0000,
    R4 = 0x0008_0000,
    L5 = 0x0010_0000,
    R5 = 0x0020_0000,
    Steam = 0x0040_0000,
    QuickAccess = 0x0080_0000,
    LeftPadPress = 0x0100_0000,
    RightPadPress = 0x0200_0000,
}

/// <summary>Polls all connected controllers through SDL3 on the UI thread while
/// enabled. Emits edge-triggered button events with D-pad/stick auto-repeat.</summary>
public sealed class GamepadService : IDisposable
{
    private static readonly TimeSpan RepeatInitial = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatRate = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer _timer;
    private GamepadButtons _previous;
    private GamepadButtons _repeating;
    private DateTime _nextRepeat;
    private bool _loggedFirstPress;

    /// <summary>Newly pressed buttons (edge-triggered), with auto-repeat for directions.</summary>
    public event Action<GamepadButtons>? ButtonPressed;

    /// <summary>The full button state, raised whenever it changes. Chord detection
    /// needs the whole state, not just the new edges.</summary>
    public event Action<GamepadButtons>? StateChanged;

    public GamepadService()
    {
        // The convenience ctor taking a callback auto-starts the timer, which made
        // IsRunning permanently true and broke every "start if not running" guard.
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        _previous = 0;
        _repeating = 0;
        _loggedFirstPress = false;
        SdlGamepads.EnsureInitialized();
        _timer.Start();
        Log.Info("Gamepad polling started.");
    }

    public void Stop() => _timer.Stop();

    /// <summary>True when a controller with back paddles (a real or emulated Steam
    /// Deck class pad) is connected, so paddles/Steam/QAM are bindable.</summary>
    public bool HasDeckButtons => SdlGamepads.HasDeckButtons;

    private void Poll()
    {
        var current = SdlGamepads.Update();

        if (current != _previous)
        {
            StateChanged?.Invoke(current);
        }

        var pressed = current & ~_previous;
        if (pressed != 0)
        {
            if (!_loggedFirstPress)
            {
                // One line per Start() so a pasted log proves input arrives at all.
                _loggedFirstPress = true;
                Log.Info($"Controller input: {Describe(pressed, false)}");
            }
            ButtonPressed?.Invoke(pressed);
        }

        // Auto-repeat for held directions.
        var directions = current & (GamepadButtons.DPadUp | GamepadButtons.DPadDown |
                                    GamepadButtons.DPadLeft | GamepadButtons.DPadRight);
        if (directions != 0)
        {
            // Re-arm whenever the held direction set changes. The prior equality
            // check left _repeating stale after, for example, changing Up to Right.
            if (directions != _repeating)
            {
                _repeating = directions;
                _nextRepeat = DateTime.UtcNow + RepeatInitial;
            }
            else if (DateTime.UtcNow >= _nextRepeat)
            {
                _nextRepeat = DateTime.UtcNow + RepeatRate;
                ButtonPressed?.Invoke(directions);
            }
        }
        else
        {
            _repeating = 0;
        }

        _previous = current;
    }

    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Human-readable chord text, e.g. "Hold LB + Start" or "None".</summary>
    public static string Describe(GamepadButtons buttons, bool hold)
    {
        if (buttons == 0)
        {
            return "None";
        }
        var names = new System.Collections.Generic.List<string>();
        foreach (var (flag, name) in ButtonNames)
        {
            if (buttons.HasFlag(flag))
            {
                names.Add(name);
            }
        }
        var combo = string.Join(" + ", names);
        return hold ? $"Hold {combo}" : combo;
    }

    private static readonly (GamepadButtons Flag, string Name)[] ButtonNames =
    [
        (GamepadButtons.A, "A"), (GamepadButtons.B, "B"), (GamepadButtons.X, "X"), (GamepadButtons.Y, "Y"),
        (GamepadButtons.LeftShoulder, "LB"), (GamepadButtons.RightShoulder, "RB"),
        (GamepadButtons.LeftThumb, "L3"), (GamepadButtons.RightThumb, "R3"),
        (GamepadButtons.Start, "Start"), (GamepadButtons.Back, "Back"),
        (GamepadButtons.DPadUp, "D-Up"), (GamepadButtons.DPadDown, "D-Down"),
        (GamepadButtons.DPadLeft, "D-Left"), (GamepadButtons.DPadRight, "D-Right"),
        (GamepadButtons.LeftTrigger, "L2"), (GamepadButtons.RightTrigger, "R2"),
        (GamepadButtons.L4, "L4"), (GamepadButtons.R4, "R4"),
        (GamepadButtons.L5, "L5"), (GamepadButtons.R5, "R5"),
        (GamepadButtons.Steam, "Steam"), (GamepadButtons.QuickAccess, "Quick Access"),
        (GamepadButtons.LeftPadPress, "L-Pad"), (GamepadButtons.RightPadPress, "R-Pad"),
    ];

    // SDL stays initialized process-wide (see SdlGamepads); only the poll stops.
    public void Dispose() => _timer.Stop();
}
