using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>One bounded mouse-button edge; the caller owns the matching release.</summary>
internal static class MouseInput
{
    internal static bool SetSecondaryButton(bool down)
    {
        NativeMethods.InputRecord[] inputs = [SecondaryButtonRecord(down)];
        return NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.InputRecord>()) == 1;
    }

    /// <summary>Injects a zero-distance mouse move, which resets the idle timer and turns a dark display on.</summary>
    /// <returns>Whether Windows accepted the input.</returns>
    internal static bool NudgeIdle()
    {
        NativeMethods.InputRecord[] inputs =
        [
            new()
            {
                type = NativeMethods.InputMouse,
                data = new NativeMethods.InputUnion
                {
                    mouse = new NativeMethods.MouseInputData { flags = NativeMethods.MouseEventMove }
                }
            }
        ];
        return NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.InputRecord>()) == 1;
    }

    internal static NativeMethods.InputRecord SecondaryButtonRecord(bool down)
    {
        return new NativeMethods.InputRecord
        {
            type = NativeMethods.InputMouse,
            data = new NativeMethods.InputUnion
            {
                mouse = new NativeMethods.MouseInputData
                {
                    flags = down ? NativeMethods.MouseEventRightDown : NativeMethods.MouseEventRightUp
                }
            }
        };
    }
}
