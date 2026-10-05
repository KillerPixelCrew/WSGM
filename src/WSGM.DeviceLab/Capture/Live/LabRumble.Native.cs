using System.Runtime.InteropServices;

namespace WSGM.DeviceLab.Capture.Live;

/// <summary>The XInput calls the rumble stage makes.</summary>
internal static class LabRumbleNative
{
    /// <summary>XInput's "no controller in this slot" result.</summary>
    public const uint ErrorDeviceNotConnected = 1167;

    /// <summary>Whether an XInput slot has a controller.</summary>
    /// <param name="slot">Slot 0 to 3.</param>
    public static bool XInputConnected(uint slot)
    {
        return XInputGetState(slot, out _) == 0;
    }

    /// <summary>Reads an XInput slot's buttons.</summary>
    /// <param name="slot">Slot 0 to 3.</param>
    /// <param name="buttons">The XInput button bits, when connected.</param>
    /// <returns>Whether the slot has a controller.</returns>
    public static bool XInputButtons(uint slot, out ushort buttons)
    {
        var connected = XInputGetState(slot, out var state) == 0;
        buttons = connected ? state.Buttons : (ushort)0;
        return connected;
    }

    /// <summary>Sets both XInput motors.</summary>
    /// <param name="slot">Slot 0 to 3.</param>
    /// <param name="left">Left (low-frequency) motor, 0 to 65535.</param>
    /// <param name="right">Right (high-frequency) motor, 0 to 65535.</param>
    /// <returns>XInput's result code; 0 is success.</returns>
    public static uint XInputVibrate(uint slot, ushort left, ushort right)
    {
        XInputVibration vibration = new() { Left = left, Right = right };
        return XInputSetState(slot, ref vibration);
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint slot, out XInputState state);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputSetState(uint slot, ref XInputVibration vibration);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLeftX;
        public short ThumbLeftY;
        public short ThumbRightX;
        public short ThumbRightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort Left;
        public ushort Right;
    }
}
