using System.Runtime.InteropServices;

namespace WSGM.Interop;

internal static class KeyboardInput
{
    internal readonly record struct SendResult(uint Sent, uint Requested, int Error);

    internal static SendResult SendControlChord(ushort virtualKey)
    {
        var controlAlreadyDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VkControl) &
                                  NativeMethods.KeyDownState) != 0;
        NativeMethods.InputRecord[] inputs = controlAlreadyDown
            ? [Key(virtualKey, up: false), Key(virtualKey, up: true)]
            :
            [
                Key(NativeMethods.VkControl, up: false),
                Key(virtualKey, up: false),
                Key(virtualKey, up: true),
                Key(NativeMethods.VkControl, up: true),
            ];

        var sent = NativeMethods.SendInput(
            (uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.InputRecord>());
        var error = sent == inputs.Length ? 0 : Marshal.GetLastPInvokeError();
        if (sent != inputs.Length)
        {
            // Never leave one of our synthetic keys down after a partial SendInput.
            NativeMethods.InputRecord[] releases = controlAlreadyDown
                ? [Key(virtualKey, up: true)]
                : [Key(virtualKey, up: true), Key(NativeMethods.VkControl, up: true)];
            NativeMethods.SendInput(
                (uint)releases.Length, releases, Marshal.SizeOf<NativeMethods.InputRecord>());
        }

        return new SendResult(sent, (uint)inputs.Length, error);
    }

    private static NativeMethods.InputRecord Key(ushort virtualKey, bool up) => new()
    {
        type = NativeMethods.InputKeyboard,
        data = new NativeMethods.InputUnion
        {
            keyboard = new NativeMethods.KeyboardInputData
            {
                virtualKey = virtualKey,
                flags = up ? NativeMethods.KeyEventKeyUp : 0,
            },
        },
    };
}
