using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace OpenFSE.Input;

[Flags]
public enum GamepadButtons : ushort
{
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
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

    public event Action<GamepadButtons>? ButtonPressed;

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

    public void Start()
    {
        _previous = 0;
        _repeating = 0;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        GamepadButtons current = 0;
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

    public void Dispose() => _timer.Stop();
}
