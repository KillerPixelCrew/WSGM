using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Threading;

namespace WSGM.OverlayMockup;

// Read-only XInput for trying the design. No capture, hooks, virtual target or Steam lease.
internal sealed class PreviewGamepad(Func<bool> active, Action<int> changePage, Action<Key> key) : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private ushort _buttons;
    private int _direction;
    private bool _left;
    private long _repeatAt;
    private bool _right;
    private uint? _slot;

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Poll;
    }

    public void Start()
    {
        _timer.Tick += Poll;
        _timer.Start();
    }

    private void Poll(object? sender, EventArgs e)
    {
        if (!active())
        {
            _slot = null;
            return;
        }

        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputGetState(slot, out var state) != 0)
            {
                continue;
            }

            var pad = state.Gamepad;
            var left = pad.LeftTrigger > (_left ? 80 : 160);
            var right = pad.RightTrigger > (_right ? 80 : 160);
            var direction = pad.Buttons & 15;
            if (_slot != slot)
            {
                _slot = slot;
                _buttons = pad.Buttons;
                _left = left;
                _right = right;
                _direction = direction;
                _repeatAt = Environment.TickCount64 + 350;
                return;
            }

            var pressed = pad.Buttons & ~_buttons;
            var previousLeft = _left;
            var previousRight = _right;
            _buttons = pad.Buttons;
            _left = left;
            _right = right;
            if ((pressed & 0x2000) != 0)
            {
                key(Key.Escape);
            }
            else if ((pressed & 0x1000) != 0)
            {
                key(Key.Enter);
            }
            else if (left && !previousLeft && !right)
            {
                changePage(-1);
            }
            else if (right && !previousRight && !left)
            {
                changePage(1);
            }
            else if (direction != 0 && (direction != _direction || Environment.TickCount64 >= _repeatAt))
            {
                var movement = direction switch
                {
                    1 => Key.Up, 2 => Key.Down, 4 => Key.Left, 8 => Key.Right, _ => Key.None
                };
                if (movement != Key.None)
                {
                    key(movement);
                }

                _repeatAt = Environment.TickCount64 + (direction != _direction ? 350 : 150);
            }

            _direction = direction;
            return;
        }

        _slot = null;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetState(uint index, out ControllerState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct GamepadState
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ControllerState
    {
        public uint Packet;
        public GamepadState Gamepad;
    }
}
