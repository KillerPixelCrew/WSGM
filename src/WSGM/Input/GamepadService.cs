using System;
using System.Collections.Generic;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Input;

/// <summary>Buttons WSGM can bind. The low 16 bits deliberately match XInput's
/// wButtons so the mapping is a straight cast; the high bits are what SDL reports
/// beyond XInput — analog triggers folded into buttons (any pad), plus the back
/// paddles, Steam and Quick Access buttons of Deck-class (real or emulated) pads.</summary>
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

    // Beyond XInput's 16 bits. The triggers are synthesized from SDL's trigger
    // axes on any pad; only the rest need Deck-class hardware.
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
    private const GamepadButtons DirectionMask = GamepadButtons.DPadUp | GamepadButtons.DPadDown |
                                                 GamepadButtons.DPadLeft | GamepadButtons.DPadRight;

    private readonly DispatcherTimer _timer;
    /// <summary>Last observed state per pad id. Edges and chords are evaluated per
    /// pad so one controller holding a button cannot mask or complete another's.</summary>
    private readonly Dictionary<uint, GamepadButtons> _perPad = new();
    private readonly List<uint> _stalePads = new();
    private GamepadButtons _repeating;
    private DateTime _nextRepeat;
    private bool _loggedFirstPress;

    /// <summary>Newly pressed buttons across all pads (edge-triggered per pad),
    /// with auto-repeat for directions.</summary>
    public event Action<GamepadButtons>? ButtonPressed;

    /// <summary>One pad's full button state, raised whenever it changes. Chord
    /// detection needs the whole state per physical pad, not just the new edges.</summary>
    public event Action<uint, GamepadButtons>? StateChanged;

    public GamepadService()
    {
        // The convenience ctor taking a callback auto-starts the timer, which made
        // IsRunning permanently true and broke every "start if not running" guard.
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        _perPad.Clear();
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
        var pads = SdlGamepads.Update();

        GamepadButtons current = 0;
        GamepadButtons pressed = 0;
        foreach (var pad in pads)
        {
            current |= pad.Buttons;
            _perPad.TryGetValue(pad.Id, out var previous);
            // Edge-trigger per pad: pad A holding a button must not mask pad B
            // freshly pressing the same button.
            pressed |= pad.Buttons & ~previous;
            if (pad.Buttons != previous)
            {
                _perPad[pad.Id] = pad.Buttons;
                StateChanged?.Invoke(pad.Id, pad.Buttons);
            }
        }

        // A pad unplugged mid-chord counts as a full release, so its chord state
        // downstream can't stay stuck holding phantom buttons.
        _stalePads.Clear();
        foreach (var (id, _) in _perPad)
        {
            var present = false;
            foreach (var pad in pads)
            {
                if (pad.Id == id)
                {
                    present = true;
                    break;
                }
            }
            if (!present)
            {
                _stalePads.Add(id);
            }
        }
        foreach (var id in _stalePads)
        {
            var previous = _perPad[id];
            _perPad.Remove(id);
            if (previous != 0)
            {
                StateChanged?.Invoke(id, 0);
            }
        }

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

        // Auto-repeat for held directions (any pad).
        var directions = current & DirectionMask;
        if (directions != 0)
        {
            var newDirections = pressed & DirectionMask;
            if (newDirections != 0)
            {
                // A fresh press re-arms the repeat and becomes the repeated
                // direction, so a diagonal repeats the direction that initiated it
                // instead of the whole held set (which navigation resolves as Next).
                _repeating = newDirections;
                _nextRepeat = DateTime.UtcNow + RepeatInitial;
            }
            else if ((directions & _repeating) == 0)
            {
                // The repeated direction was released but another is still held
                // (diagonal released in the other order): re-arm on what remains.
                _repeating = directions;
                _nextRepeat = DateTime.UtcNow + RepeatInitial;
            }
            else if (DateTime.UtcNow >= _nextRepeat)
            {
                _nextRepeat = DateTime.UtcNow + RepeatRate;
                ButtonPressed?.Invoke(directions & _repeating);
            }
        }
        else
        {
            _repeating = 0;
        }
    }

    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Human-readable chord text, e.g. "Hold LB + Start" or "None".</summary>
    public static string Describe(GamepadButtons buttons, bool hold)
    {
        if (buttons == 0)
        {
            return "None";
        }
        var names = new List<string>();
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
