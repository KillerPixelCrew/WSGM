using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace OpenFSE.Input;

/// <summary>Buttons OpenFSE can bind. The low 16 bits deliberately match XInput's
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

/// <summary>Polls XInput (all four slots) on the UI thread while enabled. Emits
/// edge-triggered button events with D-pad/stick auto-repeat. Only runs while an
/// OpenFSE window is visible, so it never fights Steam for the controller.</summary>
public sealed partial class GamepadService : IDisposable
{
    private const short StickDeadzone = 16000;
    private static readonly TimeSpan RepeatInitial = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatRate = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer _timer;
    private GamepadButtons _previous;
    private GamepadButtons _repeating;
    private DateTime _nextRepeat;

    /// <summary>Newly pressed buttons (edge-triggered), with auto-repeat for directions.</summary>
    public event Action<GamepadButtons>? ButtonPressed;

    /// <summary>The full button state, raised whenever it changes. Chord detection
    /// needs the whole state, not just the new edges.</summary>
    public event Action<GamepadButtons>? StateChanged;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static partial uint XInputGetState(uint userIndex, out XInputState state);

    public GamepadService()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => Poll());
    }

    private readonly SteamDeckHid _deck = new();

    public void Start()
    {
        _previous = 0;
        _repeating = 0;
        _deck.Start();      // extra Deck buttons when HC's emulation is active
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _deck.Stop();
    }

    /// <summary>True when a Steam Deck controller (real or Handheld Companion's
    /// emulated one) is being read over HID, so paddles/Steam/QAM are bindable.</summary>
    public bool HasDeckButtons => _deck.IsConnected;

    private void Poll()
    {
        // Steam Deck HID first: it carries the buttons XInput can't express.
        GamepadButtons current = _deck.State;

        for (uint i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out var state) == 0)
            {
                current |= (GamepadButtons)state.Gamepad.Buttons;
                // Fold the left stick into the D-pad directions.
                if (state.Gamepad.ThumbLY > StickDeadzone) current |= GamepadButtons.DPadUp;
                if (state.Gamepad.ThumbLY < -StickDeadzone) current |= GamepadButtons.DPadDown;
                if (state.Gamepad.ThumbLX < -StickDeadzone) current |= GamepadButtons.DPadLeft;
                if (state.Gamepad.ThumbLX > StickDeadzone) current |= GamepadButtons.DPadRight;
            }
        }

        if (current != _previous)
        {
            StateChanged?.Invoke(current);
        }

        var pressed = current & ~_previous;
        if (pressed != 0)
        {
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

    public void Dispose()
    {
        _timer.Stop();
        _deck.Dispose();
    }
}
