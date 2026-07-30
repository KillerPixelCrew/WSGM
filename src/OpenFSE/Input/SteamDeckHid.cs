using System;
using System.Threading;
using OpenFSE.Core;

namespace OpenFSE.Input;

/// <summary>Reads whichever controller is present over HID, so OpenFSE sees buttons
/// XInput cannot express and works with every pad Handheld Companion can emulate:
///
///  • Steam Deck (Valve 28DE:1205) and Steam Controller (28DE:1102) use Valve's
///    vendor report format — paddles, Steam and Quick Access buttons included.
///  • DualShock 4, DualSense, Switch Pro and any other HID pad are read through
///    Windows' HID parser (see HidGamepad); those are invisible to XInput entirely,
///    so without this they would register no buttons at all.
///
/// A plain Xbox pad needs none of this — XInput already covers it.</summary>
public sealed class SteamDeckHid : IDisposable
{
    private const ushort ValveVid = 0x28DE;
    private const ushort DeckPid = 0x1205;
    private const ushort SteamControllerPid = 0x1102;
    private const byte DeckInputData = 0x09;
    private const int ValveReportLength = 64;

    private Thread? _thread;
    private volatile bool _running;
    private nint _valveHandle = -1;
    private HidGamepad? _hidPad;
    private bool _isDeck;

    /// <summary>Latest button state; zero when no supported device is connected.</summary>
    public GamepadButtons State { get; private set; }

    public bool IsConnected => _valveHandle != -1 || _hidPad is not null;

    public void Start()
    {
        if (_running)
        {
            return;
        }
        _running = true;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "OpenFSE.ControllerHid" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        CloseDevices();
        State = 0;
    }

    private void CloseDevices()
    {
        if (_valveHandle != -1)
        {
            HidDevices.Close(_valveHandle);
            _valveHandle = -1;
        }
        _hidPad?.Close();
        _hidPad = null;
    }

    private void ReadLoop()
    {
        while (_running)
        {
            if (!IsConnected && !TryOpenDevice())
            {
                Thread.Sleep(3000);     // nothing supported attached — retry later
                continue;
            }

            if (_valveHandle != -1)
            {
                ReadValveReport();
            }
            else if (_hidPad is not null)
            {
                var state = _hidPad.Read();
                if (state is null)
                {
                    Log.Warn("HID gamepad read failed — reopening.");
                    CloseDevices();
                    State = 0;
                }
                else
                {
                    State = state.Value;
                }
            }
        }
    }

    private bool TryOpenDevice()
    {
        // Valve devices first: their vendor format exposes the extra buttons.
        foreach (var (path, vid, pid) in HidDevices.Enumerate())
        {
            if (vid != ValveVid || pid is not (DeckPid or SteamControllerPid))
            {
                continue;
            }
            var handle = HidDevices.OpenRead(path);
            if (handle == -1)
            {
                continue;
            }
            if (HidDevices.TryGetPreparsed(handle, out var preparsed, out var caps) &&
                caps.InputReportByteLength >= ValveReportLength)
            {
                HidDevices.FreePreparsed(preparsed);
                _valveHandle = handle;
                _isDeck = pid == DeckPid;
                Log.Info(_isDeck
                    ? "Steam Deck controller opened (paddles, Steam and Quick Access bindable)."
                    : "Steam Controller opened (grips and Steam button bindable).");
                return true;
            }
            HidDevices.FreePreparsed(preparsed);
            HidDevices.Close(handle);
        }

        // Anything else: DualShock 4, DualSense, Switch Pro, generic pads.
        _hidPad = HidGamepad.Open(skip: (vid, _) => vid == ValveVid);
        return _hidPad is not null;
    }

    private void ReadValveReport()
    {
        var buffer = new byte[ValveReportLength + 1];
        if (!HidDevices.Read(_valveHandle, buffer, out var read) || read == 0)
        {
            if (_running)
            {
                Log.Warn("Valve controller HID read failed — reopening.");
            }
            CloseDevices();
            State = 0;
            return;
        }

        // Windows may prepend a report-id byte; locate the 0x01 0x00 header.
        var offset = -1;
        for (var i = 0; i <= 1 && i + 14 < read; i++)
        {
            if (buffer[i] == 0x01 && buffer[i + 1] == 0x00)
            {
                offset = i;
                break;
            }
        }
        if (offset < 0 || buffer[offset + 2] != DeckInputData)
        {
            return;     // not an input-state report
        }

        State = _isDeck ? ParseDeck(buffer, offset) : ParseSteamController(buffer, offset);
    }

    /// <summary>Steam Deck ("Neptune") button bitfields.</summary>
    private static GamepadButtons ParseDeck(byte[] b, int o)
    {
        byte b0 = b[o + 8], b1 = b[o + 9], b2 = b[o + 10], b3 = b[o + 11], b5 = b[o + 13], b6 = b[o + 14];
        var state = ParseCommonValve(b0, b1, b2);

        if ((b1 & 0x80) != 0) state |= GamepadButtons.L5;
        if ((b2 & 0x01) != 0) state |= GamepadButtons.R5;
        if ((b3 & 0x04) != 0) state |= GamepadButtons.RightThumb;
        if ((b5 & 0x02) != 0) state |= GamepadButtons.L4;
        if ((b5 & 0x04) != 0) state |= GamepadButtons.R4;
        if ((b6 & 0x04) != 0) state |= GamepadButtons.QuickAccess;
        return state;
    }

    /// <summary>Steam Controller ("Gordon"): same header, paddles sit in other bits
    /// and there is no Quick Access button.</summary>
    private static GamepadButtons ParseSteamController(byte[] b, int o)
    {
        byte b0 = b[o + 8], b1 = b[o + 9], b2 = b[o + 10];
        var state = ParseCommonValve(b0, b1, b2);

        if ((b1 & 0x80) != 0) state |= GamepadButtons.L4;   // left grip
        if ((b2 & 0x01) != 0) state |= GamepadButtons.R4;   // right grip
        return state;
    }

    private static GamepadButtons ParseCommonValve(byte b0, byte b1, byte b2)
    {
        GamepadButtons state = 0;

        if ((b0 & 0x80) != 0) state |= GamepadButtons.A;
        if ((b0 & 0x20) != 0) state |= GamepadButtons.B;
        if ((b0 & 0x40) != 0) state |= GamepadButtons.X;
        if ((b0 & 0x10) != 0) state |= GamepadButtons.Y;
        if ((b0 & 0x08) != 0) state |= GamepadButtons.LeftShoulder;
        if ((b0 & 0x04) != 0) state |= GamepadButtons.RightShoulder;
        if ((b0 & 0x02) != 0) state |= GamepadButtons.LeftTrigger;
        if ((b0 & 0x01) != 0) state |= GamepadButtons.RightTrigger;

        if ((b1 & 0x01) != 0) state |= GamepadButtons.DPadUp;
        if ((b1 & 0x02) != 0) state |= GamepadButtons.DPadRight;
        if ((b1 & 0x04) != 0) state |= GamepadButtons.DPadLeft;
        if ((b1 & 0x08) != 0) state |= GamepadButtons.DPadDown;
        if ((b1 & 0x10) != 0) state |= GamepadButtons.Start;        // Menu (☰)
        if ((b1 & 0x20) != 0) state |= GamepadButtons.Steam;
        if ((b1 & 0x40) != 0) state |= GamepadButtons.Back;         // Options (⧉)

        if ((b2 & 0x02) != 0) state |= GamepadButtons.LeftPadPress;
        if ((b2 & 0x04) != 0) state |= GamepadButtons.RightPadPress;
        if ((b2 & 0x40) != 0) state |= GamepadButtons.LeftThumb;

        return state;
    }

    public void Dispose() => Stop();
}
