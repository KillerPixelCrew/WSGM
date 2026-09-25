using System;
using System.Runtime.InteropServices;

namespace WSGM.Setup.UI;

/// <summary>
///     Reads the first XInput controller's buttons so setup can be driven with a gamepad. Setup runs
///     before WSGM's own input stack exists, so it polls the system's XInput directly.
/// </summary>
internal static partial class XInput
{
    internal const ushort DpadUp = 0x0001;
    internal const ushort DpadDown = 0x0002;
    internal const ushort DpadLeft = 0x0004;
    internal const ushort DpadRight = 0x0008;
    internal const ushort A = 0x1000;
    internal const ushort B = 0x2000;

    /// <summary>Reads the buttons of the first connected controller.</summary>
    /// <param name="buttons">The pressed buttons.</param>
    /// <returns>Whether a controller answered.</returns>
    public static bool TryRead(out ushort buttons)
    {
        buttons = 0;
        for (uint index = 0; index < 4; index++)
        {
            try
            {
                if (XInputGetState(index, out var state) == 0)
                {
                    buttons = state.Gamepad.wButtons;
                    return true;
                }
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }

        return false;
    }

    [LibraryImport("xinput1_4.dll")]
    private static partial uint XInputGetState(uint dwUserIndex, out State pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint dwPacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }
}
