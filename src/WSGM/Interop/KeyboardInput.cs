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

    // The scan code is NOT optional. SendInput accepts a virtual-key-only record and
    // reports success, but Steam's Big Picture UI resolves keys through the SCAN CODE,
    // so a record with wScan = 0 arrives and does nothing — "shortcut sent" in the log
    // and no menu on screen.
    //
    // This mirrors RustDesk's rdev fork (src/windows/simulate.rs, simulate_code), which
    // is the reference that demonstrably drives Big Picture on this hardware: when a
    // scan code resolves it sends SCAN-CODE-ONLY (wVk = 0, KEYEVENTF_SCANCODE) and lets
    // Windows derive the virtual key, falling back to the bare virtual key only when no
    // scan code exists. The layout comes from the FOREGROUND window's thread, not ours,
    // so the chord matches the keyboard Steam itself is reading.
    private static NativeMethods.InputRecord Key(ushort virtualKey, bool up)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var layout = NativeMethods.GetKeyboardLayout(threadId);
        var scan = NativeMethods.MapVirtualKeyExW(virtualKey, NativeMethods.MapVkToVsc, layout);

        ushort sentVirtualKey;
        uint flags;
        if (scan != 0)
        {
            sentVirtualKey = 0;
            flags = NativeMethods.KeyEventScanCode;
            // 0xE0/0xE1 prefixed scan codes are the extended set (arrows, right Ctrl,
            // numpad Enter). Ctrl+1/Ctrl+2 are not, but the mapping must stay correct
            // if this ever carries another chord.
            if ((scan >> 8) == 0xE0 || (scan >> 8) == 0xE1)
            {
                flags |= NativeMethods.KeyEventExtendedKey;
            }
        }
        else
        {
            sentVirtualKey = virtualKey;
            flags = 0;
        }
        if (up)
        {
            flags |= NativeMethods.KeyEventKeyUp;
        }

        return new NativeMethods.InputRecord
        {
            type = NativeMethods.InputKeyboard,
            data = new NativeMethods.InputUnion
            {
                keyboard = new NativeMethods.KeyboardInputData
                {
                    virtualKey = sentVirtualKey,
                    scanCode = (ushort)scan,
                    flags = flags,
                },
            },
        };
    }
}
